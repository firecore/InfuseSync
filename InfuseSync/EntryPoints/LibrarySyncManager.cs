using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using InfuseSync.Models;

#if EMBY
using MediaBrowser.Model.Logging;
using InfuseSync.Logging;
#else
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
#endif

namespace InfuseSync.EntryPoints
{
    public class LibrarySyncManager: IServerEntryPoint
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;
        private readonly object _libraryChangedSyncLock = new object();

        private readonly List<ItemRec> _itemsUpdated = new List<ItemRec>();
        private readonly List<ItemRec> _itemsRemoved = new List<ItemRec>();

        private Timer WriteTimer { get; set; }
        private const int WriteDelay = 5000;

        public LibrarySyncManager(ILibraryManager libraryManager, ILogger logger)
        {
            _libraryManager = libraryManager;
            _logger = logger;
        }

#if EMBY
        public void Run()
        {
            _libraryManager.ItemAdded += ItemUpdated;
            _libraryManager.ItemUpdated += ItemUpdated;
            _libraryManager.ItemRemoved += ItemRemoved;
        }
#else
        public Task RunAsync()
        {
            _libraryManager.ItemAdded += ItemUpdated;
            _libraryManager.ItemUpdated += ItemUpdated;
            _libraryManager.ItemRemoved += ItemRemoved;

            return Task.CompletedTask;
        }
#endif

        void ItemUpdated(object sender, ItemChangeEventArgs e)
        {
            var message = $"InfuseSync received updated item '{e.Item.Name}' of type '{e.Item.GetClientTypeName()}' Guid '{e.Item.Id}'";
#if EMBY
            message += " ItemID '{e.Item.GetClientId()}'";
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
            lock (_libraryChangedSyncLock)
            {
                if (WriteTimer == null)
                {
                    WriteTimer = new Timer(TimerCallback, null, WriteDelay, Timeout.Infinite);
                }
                else
                {
                    WriteTimer.Change(WriteDelay, Timeout.Infinite);
                }

                var itemRec = new ItemRec
                {
                    Guid = item.Id,
#if EMBY
                    ItemId = item.GetClientId(),
#endif
                    Status = ItemStatus.Updated,
                    Type = item.GetClientTypeName()
                };

                _logger.LogDebug($"InfuseSync saving updated item {item.Id}");
                _itemsUpdated.Add(itemRec);
            }
        }

        void ItemRemoved(object sender, ItemChangeEventArgs e)
        {
            var message = $"InfuseSync received removed item '{e.Item.Name}' of type '{e.Item.GetClientTypeName()}' Guid '{e.Item.Id}'";
#if EMBY
            message += " ItemID '{e.Item.GetClientId()}'";
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
            else
            {
                ItemRemoved(e.Item);
            }
        }

        private void ItemRemoved(BaseItem item)
        {
            lock (_libraryChangedSyncLock)
            {
                if (WriteTimer == null)
                {
                    WriteTimer = new Timer(TimerCallback, null, WriteDelay, Timeout.Infinite);
                }
                else
                {
                    WriteTimer.Change(WriteDelay, Timeout.Infinite);
                }

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

                _logger.LogDebug($"InfuseSync saving removed item {item.Id}");
                _itemsRemoved.Add(itemRec);
            }
        }

        private void TimerCallback(object state)
        {
            lock (_libraryChangedSyncLock)
            {
                try
                {
                    var itemsUpdated = _itemsUpdated
#if EMBY
                        .GroupBy(i => i.ItemId)
#else
                        .GroupBy(i => i.Guid)
#endif
                        .Select(grp => grp.First())
                        .Select(i => {i.LastModified = DateTime.UtcNow.ToFileTime(); return i;})
                        .ToList();
                    var itemsRemoved = _itemsRemoved
#if EMBY
                        .GroupBy(i => i.ItemId)
#else
                        .GroupBy(i => i.Guid)
#endif
                        .Select(grp => grp.First())
                        .Select(i => {i.LastModified = DateTime.UtcNow.ToFileTime(); return i;})
                        .ToList();

                    Plugin.Instance.Db.SaveItems(itemsUpdated);
                    Plugin.Instance.Db.SaveItems(itemsRemoved);

                    if (WriteTimer != null)
                    {
                        WriteTimer.Dispose();
                        WriteTimer = null;
                    }
                }
                catch (Exception e)
                {
                    _logger.LogError(e, $"An error in TimerCallback: {e}");
                }

                _itemsRemoved.Clear();
                _itemsUpdated.Clear();
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
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
                if (WriteTimer != null)
                {
                    WriteTimer.Dispose();
                    WriteTimer = null;
                }

                _libraryManager.ItemAdded -= ItemUpdated;
                _libraryManager.ItemUpdated -= ItemUpdated;
                _libraryManager.ItemRemoved -= ItemRemoved;
            }

            _disposed = true;
        }
    }
}
