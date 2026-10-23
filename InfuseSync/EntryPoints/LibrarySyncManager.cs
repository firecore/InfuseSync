using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using InfuseSync.Models;

#if EMBY
using InfuseSync.Logging;
using ILogger = MediaBrowser.Model.Logging.ILogger;
#else
using System.Threading.Tasks;
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

            // Queue the removal before a library refresh can fail.
            ItemRemoved(e.Item);

            // The folder is already empty, so refresh its library.
            if (e.Item is Folder && ShouldRefreshAffectedLibraries(e.Item.GetClientTypeName()))
            {
                try
                {
                    RefreshAffectedLibraries(e.Item, e.Parent);
                }
                catch (Exception exception)
                {
                    _logger.LogError(
                        exception,
                        $"Unable to refresh libraries affected by removed folder '{e.Item.Id}'.");
                }
            }
        }

        private void RefreshAffectedLibraries(BaseItem removedFolder, BaseItem parent)
        {
            var ancestorPaths = parent == null
                ? Array.Empty<string>()
                : parent.GetParents().OfType<Folder>().Select(folder => folder.Path);
            var folderPaths = GetAffectedFolderPaths(
                removedFolder.Path,
                parent is Folder ? parent.Path : null,
                ancestorPaths);

            var libraryIds = _libraryManager.GetVirtualFolders()
                .Where(folder => HasMatchingLocation(folderPaths, folder.Locations))
                .Select(folder => folder.ItemId)
                .Distinct()
                .ToArray();

            foreach (var libraryId in libraryIds)
            {
                var library = _libraryManager.GetItemById(libraryId);
                if (library != null)
                {
                    ItemUpdated(library);
                }
            }
        }

        internal static IReadOnlyCollection<string> GetAffectedFolderPaths(
            string removedPath,
            string parentPath,
            IEnumerable<string> ancestorPaths)
        {
            var paths = new HashSet<string>(StringComparer.Ordinal);
            AddPath(paths, removedPath);
            AddPath(paths, parentPath);

            if (ancestorPaths != null)
            {
                foreach (var path in ancestorPaths)
                {
                    AddPath(paths, path);
                }
            }

            return paths;
        }

        internal static bool ShouldRefreshAffectedLibraries(string clientType)
        {
            return string.Equals(clientType, "Folder", StringComparison.Ordinal);
        }

        internal static bool HasMatchingLocation(
            IReadOnlyCollection<string> folderPaths,
            IEnumerable<string> locations)
        {
            return folderPaths != null
                && locations != null
                && locations.Any(location => folderPaths.Any(path => IsSameOrDescendant(path, location)));
        }

        private static bool IsSameOrDescendant(string path, string location)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(location))
            {
                return false;
            }

            var comparison = Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            var normalizedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedLocation = location.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.Equals(normalizedPath, normalizedLocation, comparison))
            {
                return true;
            }

            if (normalizedLocation.Length == 0)
            {
                return IsDirectorySeparator(location[0]) && IsDirectorySeparator(path[0]);
            }

            return normalizedPath.StartsWith(normalizedLocation + Path.DirectorySeparatorChar, comparison)
                || normalizedPath.StartsWith(normalizedLocation + Path.AltDirectorySeparatorChar, comparison);
        }

        private static bool IsDirectorySeparator(char value)
        {
            return value == Path.DirectorySeparatorChar || value == Path.AltDirectorySeparatorChar;
        }

        private static void AddPath(ISet<string> paths, string path)
        {
            if (!string.IsNullOrEmpty(path))
            {
                paths.Add(path);
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
