using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using InfuseSync.Models;

#if EMBY
using InfuseSync.Logging;
using ILogger = MediaBrowser.Model.Logging.ILogger;
#else
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger<InfuseSync.EntryPoints.LibrarySyncManager>;
#endif

namespace InfuseSync.EntryPoints
{
#if EMBY
    public class LibrarySyncManager: IServerEntryPoint
#else
    public class LibrarySyncManager: IHostedService
#endif
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;
        private readonly CoalescingBatchWriter<string, ItemRec> _pendingItems;
        private readonly EventHandlerTracker _eventHandlers = new EventHandlerTracker();
        private Task<BatchStopResult> _deferredStop;

        private static readonly TimeSpan WriteDelay = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan MaximumWriteDelay = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

        internal Task<BatchStopResult> DeferredStop => _deferredStop;

        public LibrarySyncManager(ILibraryManager libraryManager, ILogger logger)
        {
            _libraryManager = libraryManager;
            _logger = logger;
            _pendingItems = new CoalescingBatchWriter<string, ItemRec>(
                WriteDelay,
                MaximumWriteDelay,
                RetryDelay,
                MergeItemChanges,
                SaveItems,
                (exception, count) => _logger.LogError(
                    exception,
                    $"Unable to save {count} pending library changes. Changes remain queued."));
        }

        public void Run()
        {
            _libraryManager.ItemAdded += ItemUpdated;
            _libraryManager.ItemUpdated += ItemUpdated;
            _libraryManager.ItemRemoved += ItemRemoved;
        }

#if JELLYFIN
        public Task StartAsync(CancellationToken cancellationToken)
        {
            Run();

            return Task.CompletedTask;
        }
#endif

        void ItemUpdated(object sender, ItemChangeEventArgs e)
        {
            if (!_eventHandlers.TryEnter())
            {
                return;
            }

            try
            {
                HandleItemUpdated(e);
            }
            finally
            {
                _eventHandlers.Exit();
            }
        }

        private void HandleItemUpdated(ItemChangeEventArgs e)
        {
            var message = $"InfuseSync received updated item '{e.Item.Name}' of type '{e.Item.GetClientTypeName()}' Guid '{e.Item.Id}'";
#if EMBY
            message += $" ItemID '{e.Item.GetClientId()}'";
#endif
            _logger.LogDebug(message);

            if (!Shared.ShouldSyncUpdatedItem(e.Item))
            {
                return;
            }

            if (!Plugin.Instance.Db.HasCheckpoints())
            {
                return;
            }

            ItemUpdated(e.Item);
        }

        private void ItemUpdated(BaseItem item)
        {
            var itemRec = new ItemRec
            {
                Guid = item.Id,
#if EMBY
                ItemId = item.GetClientId(),
#endif
                Status = ItemStatus.Updated,
                Type = item.GetClientTypeName()
            };

            if (_pendingItems.Enqueue(GetItemKey(itemRec), itemRec))
            {
                _logger.LogDebug($"InfuseSync will save updated item {item.Id}");
            }
        }

        void ItemRemoved(object sender, ItemChangeEventArgs e)
        {
            if (!_eventHandlers.TryEnter())
            {
                return;
            }

            try
            {
                HandleItemRemoved(e);
            }
            finally
            {
                _eventHandlers.Exit();
            }
        }

        private void HandleItemRemoved(ItemChangeEventArgs e)
        {
            var message = $"InfuseSync received removed item '{e.Item.Name}' of type '{e.Item.GetClientTypeName()}' Guid '{e.Item.Id}'";
#if EMBY
            message += $" ItemID '{e.Item.GetClientId()}'";
#endif
            _logger.LogDebug(message);

            if (!Shared.ShouldSyncRemovedItem(e.Item))
            {
                return;
            }

            if (!Plugin.Instance.Db.HasCheckpoints())
            {
                return;
            }

            // Folder already have no content in it when it is removed.
            // So we have to re-fetch all affected libraries.
            if (e.Item.GetType() == typeof(Folder))
            {
                var topFolder = e.Parent.GetParents().LastOrDefault(i => i.GetType() == typeof(Folder));
                if (topFolder == null && e.Parent.GetType() == typeof(Folder))
                {
                    topFolder = e.Parent;
                }

                if (topFolder != null)
                {
                    var libs = _libraryManager.GetVirtualFolders()
                        .Where(vf => vf.Locations.Contains(topFolder.Path))
                        .Select(vf => _libraryManager.GetItemById(vf.ItemId));

                    foreach (var lib in libs)
                    {
                        ItemUpdated(lib);
                    }
                }
            }

            ItemRemoved(e.Item);
        }

        private void ItemRemoved(BaseItem item)
        {
#if EMBY
            long? seriesId;
#else
            Guid? seriesId;
#endif
            int? seasonNumber;
            if (item is Season season)
            {
                seriesId = season.SeriesId;
                seasonNumber = season.IndexNumber;
            }
            else
            {
                seriesId = null;
                seasonNumber = null;
            }

            var itemRec = new ItemRec
            {
                Guid = item.Id,
#if EMBY
                ItemId = item.GetClientId(),
#endif
                SeriesId = seriesId,
                Season = seasonNumber,
                Status = ItemStatus.Removed,
                Type = item.GetClientTypeName()
            };

            if (_pendingItems.Enqueue(GetItemKey(itemRec), itemRec))
            {
                _logger.LogDebug($"InfuseSync will save removed item {item.Id}");
            }
        }

        private static string GetItemKey(ItemRec item)
        {
#if EMBY
            return item.ItemId;
#else
            return item.Guid.ToString("N");
#endif
        }

        internal static ItemRec MergeItemChanges(ItemRec current, ItemRec incoming)
        {
            if (current.Status == ItemStatus.Removed)
            {
                return current;
            }

            return incoming.Status == ItemStatus.Removed ? incoming : current;
        }

        private void SaveItems(IReadOnlyCollection<ItemRec> items)
        {
            Plugin.Instance.Db.SaveItemsNow(items);
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
            _libraryManager.ItemAdded -= ItemUpdated;
            _libraryManager.ItemUpdated -= ItemUpdated;
            _libraryManager.ItemRemoved -= ItemRemoved;

            var elapsed = Stopwatch.StartNew();
            var handlerError = _eventHandlers.StopAndWait(ShutdownTimeout, cancellationToken);
            if (handlerError != null)
            {
                _deferredStop = _eventHandlers.ContinueWhenIdle(
                    () => FlushPendingAndObserve(ShutdownTimeout, true)).Unwrap();
                _logger.LogError(
                    handlerError,
                    $"Library sync shutdown stopped with {_eventHandlers.ActiveCount} active handlers and " +
                    $"{_pendingItems.OutstandingCount} queued changes not confirmed persisted. " +
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
                _deferredStop = ObserveDeferredStop(_pendingItems.DeferredStop);
            }
        }

        private BatchStopResult FlushPending(
            TimeSpan timeout,
            CancellationToken cancellationToken,
            bool deferred)
        {
            var result = _pendingItems.StopAndFlush(timeout, cancellationToken);
            if (!result.IsFinal)
            {
                _logger.LogError(
                    result.Error,
                    $"Unable to confirm {result.UnsavedCount} library changes persisted before the shutdown deadline " +
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
                : await ObserveDeferredStop(_pendingItems.DeferredStop).ConfigureAwait(false);
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
                    $"Unable to confirm {result.UnsavedCount} library changes persisted during shutdown " +
                    $"after {result.Attempts} batch write attempts.");
            }
            else if (deferred)
            {
                _logger.LogDebug(
                    $"Deferred library sync drain completed after {result.Attempts} batch write attempts.");
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
