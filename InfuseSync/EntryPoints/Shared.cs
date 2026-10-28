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
            "MusicVideo",
            "Folder",
            "Playlist"
        };

        public static bool ShouldSyncUpdatedItem(BaseItem item)
        {
            return ShouldSyncItem(item, t => SyncTypes.Contains(t) || t == "CollectionFolder");
        }

        public static bool ShouldSyncRemovedItem(BaseItem item)
        {
            return ShouldSyncItem(item, t => SyncTypes.Contains(t));
        }

#if JELLYFIN
        public static BaseItem ResolveUpdatedItem(
            BaseItem item,
            Func<Guid, BaseItem> itemResolver,
            Func<Video, Guid, bool> isLocalVersion)
        {
            if (item is not Video video)
            {
                return item;
            }

            var primaryId = video.PrimaryVersionId;
            if (!primaryId.HasValue && video.OwnerId != Guid.Empty)
            {
                primaryId = video.OwnerId;
            }

            if (!primaryId.HasValue || itemResolver(primaryId.Value) is not Video primary)
            {
                return item;
            }

            var isOwnedByPrimary = video.PrimaryVersionId == primary.Id
                && video.OwnerId == primary.Id;

            return isOwnedByPrimary || isLocalVersion(primary, video.Id)
                ? primary
                : item;
        }
#endif

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
