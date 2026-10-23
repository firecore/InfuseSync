using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Entities;
using InfuseSync.Models;

#if EMBY
using InfuseSync.Logging;
using ILogger = MediaBrowser.Model.Logging.ILogger;
#else
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger<InfuseSync.EntryPoints.UserSyncManager>;
#endif

namespace InfuseSync.EntryPoints
{
#if EMBY
    public class UserSyncManager: IServerEntryPoint
#else
    public class UserSyncManager: IHostedService
#endif
    {
        private readonly ILogger _logger;
        private readonly IUserDataManager _userDataManager;
        private readonly IUserManager _userManager;
        private readonly CoalescingBatchWriter<string, UserInfoRec> _pendingUserInfo;
        private readonly EventHandlerTracker _eventHandlers = new EventHandlerTracker();
        private Task<BatchStopResult> _deferredStop;

        private static readonly TimeSpan UpdateDelay = TimeSpan.FromMilliseconds(500);
        private static readonly TimeSpan MaximumUpdateDelay = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

        internal Task<BatchStopResult> DeferredStop => _deferredStop;

        public UserSyncManager(IUserDataManager userDataManager, ILogger logger, IUserManager userManager)
        {
            _userDataManager = userDataManager;
            _logger = logger;
            _userManager = userManager;
            _pendingUserInfo = new CoalescingBatchWriter<string, UserInfoRec>(
                UpdateDelay,
                MaximumUpdateDelay,
                RetryDelay,
                (current, incoming) => current,
                SaveUserInfo,
                (exception, count) => _logger.LogError(
                    exception,
                    $"Unable to save {count} pending user data changes. Changes remain queued."));
        }

        public void Run()
        {
            _userDataManager.UserDataSaved += UserDataSaved;
        }

#if JELLYFIN
        public Task StartAsync(CancellationToken cancellationToken)
        {
            Run();

            return Task.CompletedTask;
        }
#endif

        void UserDataSaved(object sender, UserDataSaveEventArgs e)
        {
            if (!_eventHandlers.TryEnter())
            {
                return;
            }

            try
            {
                HandleUserDataSaved(e);
            }
            finally
            {
                _eventHandlers.Exit();
            }
        }

        private void HandleUserDataSaved(UserDataSaveEventArgs e)
        {
            if (e.SaveReason == UserDataSaveReason.PlaybackProgress || e.Item == null)
            {
                return;
            }

            var message = $"InfuseSync received user data for item '{e.Item.Name}' of type '{e.Item.GetClientTypeName()}' Guid '{e.Item.Id}'";
#if EMBY
            message += $" ItemID '{e.Item.GetClientId()}'";
#endif
            _logger.LogDebug(message);

            if (!Shared.ShouldSyncUpdatedItem(e.Item))
            {
                return;
            }

#if EMBY
            var userId = e.User.Id;
#else
            var userId = e.UserId;
#endif
            var infoRec = new UserInfoRec
            {
                Guid = e.Item.Id,
#if EMBY
                ItemId = e.Item.GetClientId(),
#endif
                UserId = userId.ToString("N", CultureInfo.InvariantCulture),
                Type = e.Item.GetClientTypeName()
            };

            if (_pendingUserInfo.Enqueue(GetUserItemKey(userId, infoRec), infoRec))
            {
                _logger.LogDebug($"InfuseSync will save user data for item {e.Item.Id} user {userId}");
            }
        }

        private static string GetUserItemKey(Guid userId, UserInfoRec infoRec)
        {
#if EMBY
            return $"{userId:N}:{infoRec.ItemId}";
#else
            return $"{userId:N}:{infoRec.Guid:N}";
#endif
        }

        private void SaveUserInfo(IReadOnlyCollection<UserInfoRec> infoRecs)
        {
            Plugin.Instance.Db.SaveUserInfoNow(infoRecs);
        }

        private bool _disposed;

        protected virtual void Dispose(bool disposing)
        {
            if (!disposing)
            {
                _disposed = true;
                return;
            }

            Stop(CancellationToken.None);
        }

        private void Stop(CancellationToken cancellationToken)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _userDataManager.UserDataSaved -= UserDataSaved;

            var elapsed = Stopwatch.StartNew();
            var handlerError = _eventHandlers.StopAndWait(ShutdownTimeout, cancellationToken);
            if (handlerError != null)
            {
                _deferredStop = _eventHandlers.ContinueWhenIdle(
                    () => FlushPendingAndObserve(ShutdownTimeout, true)).Unwrap();
                _logger.LogError(
                    handlerError,
                    $"User sync shutdown stopped with {_eventHandlers.ActiveCount} active handlers and " +
                    $"{_pendingUserInfo.OutstandingCount} queued changes not confirmed persisted. " +
                    "A deferred drain has been scheduled.");
                return;
            }

            var remaining = ShutdownTimeout - elapsed.Elapsed;
            if (remaining < TimeSpan.Zero)
            {
                remaining = TimeSpan.Zero;
            }

            var result = FlushPending(remaining, cancellationToken, false);
            if (!result.IsFinal)
            {
                _deferredStop = ObserveDeferredStop(_pendingUserInfo.DeferredStop);
            }
        }

        private BatchStopResult FlushPending(
            TimeSpan timeout,
            CancellationToken cancellationToken,
            bool deferred)
        {
            var result = _pendingUserInfo.StopAndFlush(timeout, cancellationToken);
            if (!result.IsFinal)
            {
                _logger.LogError(
                    result.Error,
                    $"Unable to confirm {result.UnsavedCount} user data changes persisted before the shutdown deadline " +
                    $"after {result.Attempts} batch write attempts. The final drain is still running.");
                return result;
            }

            LogStopResult(result, deferred);
            return result;
        }

        private async Task<BatchStopResult> FlushPendingAndObserve(
            TimeSpan timeout,
            bool deferred)
        {
            var result = FlushPending(timeout, CancellationToken.None, deferred);
            return result.IsFinal
                ? result
                : await ObserveDeferredStop(_pendingUserInfo.DeferredStop).ConfigureAwait(false);
        }

        private async Task<BatchStopResult> ObserveDeferredStop(Task<BatchStopResult> deferredStop)
        {
            var result = await deferredStop.ConfigureAwait(false);
            LogStopResult(result, true);
            return result;
        }

        private void LogStopResult(BatchStopResult result, bool deferred)
        {
            if (!result.Succeeded)
            {
                _logger.LogError(
                    result.Error,
                    $"Unable to confirm {result.UnsavedCount} user data changes persisted during shutdown " +
                    $"after {result.Attempts} batch write attempts.");
            }
            else if (deferred)
            {
                _logger.LogDebug(
                    $"Deferred user sync drain completed after {result.Attempts} batch write attempts.");
            }
        }

#if EMBY
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
#else
        public Task StopAsync(CancellationToken cancellationToken)
        {
            Stop(cancellationToken);

            return Task.CompletedTask;
        }
#endif
    }
}
