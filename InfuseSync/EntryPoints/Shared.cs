using System;
using System.Linq;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;

namespace InfuseSync.EntryPoints
{
    public class Shared
    {
        private static string[] SyncTypes =
        {
            "Movie",
            "BoxSet",
            "Series",
            "Season",
            "Episode",
            "Video",
            "MusicVideo"
        };

        public static bool ShouldSyncUpdatedItem(BaseItem item)
        {
            return ShouldSyncItem(item, t => SyncTypes.Contains(t) || t == "CollectionFolder");
        }

        public static bool ShouldSyncRemovedItem(BaseItem item)
        {
            return ShouldSyncItem(item, t => SyncTypes.Contains(t) || t == "Folder");
        }

        private static bool ShouldSyncItem(BaseItem item, Func<string, bool> typeCheck)
        {
            if (item.LocationType == MediaBrowser.Model.Entities.LocationType.Virtual)
            {
                return false;
            }

            if (item.GetTopParent() is Channel)
            {
                return false;
            }

            var typeName = item.GetClientTypeName();
            if (string.IsNullOrEmpty(typeName))
            {
                return false;
            }

            return typeCheck(typeName);
        }
    }
}