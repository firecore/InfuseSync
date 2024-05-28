using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Entities;
using InfuseSync.Models;

#if EMBY
using InfuseSync.Logging;
using ILogger = MediaBrowser.Model.Logging.ILogger;
#else
using System.Threading.Tasks;
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

        private readonly object _syncLock = new object();
        private Timer UpdateTimer { get; set; }
        private const int UpdateDuration = 500;

        private readonly Dictionary<Guid, List<BaseItem>> _changedItems = new Dictionary<Guid, List<BaseItem>>();

        public UserSyncManager(IUserDataManager userDataManager, ILogger logger, IUserManager userManager)
        {
            _userDataManager = userDataManager;
            _logger = logger;
            _userManager = userManager;
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
            if (e.SaveReason == UserDataSaveReason.PlaybackProgress)
            {
                return;
            }

            var message = $"InfuseSync received user data for item '{e.Item.Name}' of type '{e.Item.GetClientTypeName()}' Guid '{e.Item.Id}'";
#if EMBY
            message += $" ItemID '{e.Item.GetClientId()}'";
#endif
            _logger.LogDebug(message);

            lock (_syncLock)
            {
                if (e.Item != null)
                {
                    if (!Shared.ShouldSyncUpdatedItem(e.Item))
                    {
                        return;
                    }

                    if (UpdateTimer == null)
                    {
                        UpdateTimer = new Timer(
                            TimerCallback,
                            null,
                            UpdateDuration,
                            Timeout.Infinite
                        );
                    }
                    else
                    {
                        UpdateTimer.Change(UpdateDuration, Timeout.Infinite);
                    }
#if EMBY
                    var userId = e.User.Id;
#else
                    var userId = e.UserId;
#endif
                    if (!_changedItems.TryGetValue(userId, out var keys))
                    {
                        keys = new List<BaseItem>();
                        _changedItems[userId] = keys;
                    }

                    keys.Add(e.Item);

                    _logger.LogDebug($"InfuseSync will save user data for item {e.Item.Id} user {userId}");
                }
            }
        }

        private void TimerCallback(object state)
        {
            lock (_syncLock)
            try
            {
                var changes = _changedItems.ToList();
                _changedItems.Clear();

                SendNotifications(changes);

                if (UpdateTimer != null)
                {
                    UpdateTimer.Dispose();
                    UpdateTimer = null;
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"An Error Has Occurred in TimerCallback: {e}");
            }
        }

        private void SendNotifications(IEnumerable<KeyValuePair<Guid, List<BaseItem>>> changes)
        {
            var options = new DtoOptions();
            var infoRecs = changes
                .SelectMany(change => change.Value
                    .GroupBy(i => i.Id)
                    .Select(i => i.First())
                    .Select(i => {
                        return new UserInfoRec {
                            Guid = i.Id,
#if EMBY
                            ItemId = i.GetClientId(),
#endif
                            UserId = change.Key.ToString("N", CultureInfo.InvariantCulture),
                            LastModified = DateTime.UtcNow.ToFileTime(),
                            Type = i.GetClientTypeName()
                        };
                    })
                ).ToList();

            Plugin.Instance.Db.SaveUserInfo(infoRecs);
        }

        private bool _disposed;

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                if (UpdateTimer != null)
                {
                    UpdateTimer.Dispose();
                    UpdateTimer = null;
                }

                _userDataManager.UserDataSaved -= UserDataSaved;
            }

            _disposed = true;
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
            Dispose(true);

            return Task.CompletedTask;
        }
#endif
    }
}
