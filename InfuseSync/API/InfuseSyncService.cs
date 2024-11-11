using System;
using System.Collections.Generic;
using System.Linq;
using InfuseSync.Models;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;

#if EMBY
using MediaBrowser.Model.Services;
using MediaBrowser.Controller.Net;
using InfuseSync.Logging;
using ILogger = MediaBrowser.Model.Logging.ILogger;
#else
using Jellyfin.Data.Entities;
using Microsoft.Extensions.Logging;
using System.Globalization;
#endif

namespace InfuseSync.API
{
#if EMBY
    [Route("/InfuseSync/Checkpoint", "POST", Summary = "Create new synchronization checkpoint and remove previous device checkpoints")]
    [Authenticated]
#endif
    public class CreateCheckpoint
#if EMBY
     : IReturn<CheckpointId>
#endif
    {
#if EMBY
        [ApiMember(Name = "DeviceID", Description = "Unique device identifier", IsRequired = true, DataType = "string", ParameterType = "query", Verb = "POST")]
        [ApiMember(Name = "UserID", Description = "User identifier", IsRequired = true, DataType = "string", ParameterType = "query", Verb = "POST")]
#endif
        public string DeviceID { get; set; }
        public string UserID { get; set; }
    }

#if EMBY
    [Route("/InfuseSync/Checkpoint/{CheckpointID}/StartSync", "POST", Summary = "Start synchronization session for a checkpoint and return items statistics")]
    [Authenticated]
#endif
    public class StartCheckpointSync
#if EMBY
     : IReturn<SyncStats>
#endif
    {
#if EMBY
        [ApiMember(Name = "CheckpointID", Description = "Checkpoint identifier", IsRequired = true, DataType = "string", ParameterType = "path", Verb = "POST")]
#endif
        public Guid CheckpointID { get; set; }
    }

#if EMBY
    [Route("/InfuseSync/Checkpoint/{CheckpointID}/UpdatedItems", "GET", Summary = "Get updated items for {CheckpointID}")]
    [Authenticated]
#endif
    public class GetUpdatedItemsQuery
#if EMBY
     : IReturn<QueryResult<ItemRec>>
#endif
    {
#if EMBY
        [ApiMember(Name = "CheckpointID", Description = "Checkpoint identifier", IsRequired = true, DataType = "string", ParameterType = "path", Verb = "GET")]
        [ApiMember(Name = "IncludeItemTypes", Description = "Optional list of item types to include in the result", IsRequired = false, DataType = "string", ParameterType = "query", Verb = "GET")]
        [ApiMember(Name = "Fields", Description = "Optional. Specify additional fields of information to return in the output. This allows multiple, comma delimeted values.", IsRequired = false, DataType = "string", ParameterType = "query", Verb = "GET", AllowMultiple = true)]
        [ApiMember(Name = "StartIndex", Description = "Offset for items to fetch", IsRequired = false, DataType = "int", ParameterType = "query", Verb = "GET")]
        [ApiMember(Name = "Limit", Description = "Maximum number of items to fetch", IsRequired = false, DataType = "int", ParameterType = "query", Verb = "GET")]
#endif
        public Guid CheckpointID { get; set; }
        public string IncludeItemTypes { get; set; }
        public string Fields { get; set; }
        public int? StartIndex { get; set; }
        public int? Limit { get; set; }

        public ItemFields[] GetItemFields()
        {
            if (string.IsNullOrEmpty(Fields))
            {
                return Array.Empty<ItemFields>();
            }

            return Fields.Split(',').Select(v =>
            {
                if (Enum.TryParse(v, true, out ItemFields value))
                {
                    return (ItemFields?)value;
                }
                return null;
            }).Where(i => i.HasValue).Select(i => i.Value).ToArray();
        }
    }

#if EMBY
    [Route("/InfuseSync/Checkpoint/{CheckpointID}/RemovedItems", "GET", Summary = "Get removed item IDs for {CheckpointID}")]
    [Authenticated]
#endif
    public class GetRemovedItemsQuery
#if EMBY
     : IReturn<QueryResult<RemovedItem>>
#endif
    {
#if EMBY
        [ApiMember(Name = "CheckpointID", Description = "Checkpoint identifier", IsRequired = true, DataType = "string", ParameterType = "path", Verb = "GET")]
        [ApiMember(Name = "IncludeItemTypes", Description = "Optional list of item types to include in the result", IsRequired = false, DataType = "string", ParameterType = "query", Verb = "GET")]
        [ApiMember(Name = "StartIndex", Description = "Offset for items to fetch", IsRequired = false, DataType = "int", ParameterType = "query", Verb = "GET")]
        [ApiMember(Name = "Limit", Description = "Maximum number of items to fetch", IsRequired = false, DataType = "int", ParameterType = "query", Verb = "GET")]
#endif
        public Guid CheckpointID { get; set; }
        public string IncludeItemTypes { get; set; }
        public int? StartIndex { get; set; }
        public int? Limit { get; set; }
    }

#if EMBY
    [Route("/InfuseSync/Checkpoint/{CheckpointID}/UserData", "GET", Summary = "Get updated user data for {CheckpointID}")]
    [Authenticated]
#endif
    public class GetUserDataQuery
#if EMBY
     : IReturn<QueryResult<UserItemDataDto>>
#endif
    {
#if EMBY
        [ApiMember(Name = "CheckpointID", Description = "Checkpoint identifier", IsRequired = true, DataType = "string", ParameterType = "path", Verb = "GET")]
        [ApiMember(Name = "IncludeItemTypes", Description = "Optional list of item types to include in the result", IsRequired = false, DataType = "string", ParameterType = "query", Verb = "GET")]
        [ApiMember(Name = "StartIndex", Description = "Offset for items to fetch", IsRequired = false, DataType = "int", ParameterType = "query", Verb = "GET")]
        [ApiMember(Name = "Limit", Description = "Maximum number of items to fetch", IsRequired = false, DataType = "int", ParameterType = "query", Verb = "GET")]
#endif
        public Guid CheckpointID { get; set; }
        public string IncludeItemTypes { get; set; }
        public int? StartIndex { get; set; }
        public int? Limit { get; set; }
    }

#if EMBY
    [Route("/InfuseSync/UserFolders/{UserID}", "GET", Summary = "Get updated user data for {CheckpointID}")]
    [Authenticated]
#endif
    public class GetUserFolders
#if EMBY
     : IReturn<List<VirtualFolderInfo>>
#endif
    {
#if EMBY
        [ApiMember(Name = "UserID", Description = "User identifier", IsRequired = true, DataType = "string", ParameterType = "path", Verb = "GET")]
#endif
        public string UserID { get; set; }
    }

    public class InfuseSyncService
#if EMBY
     : IService
#endif
    {
        private readonly ILogger _logger;
        private readonly IUserManager _userManager;
        private readonly IUserDataManager _userDataManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IDtoService _dtoService;

        public InfuseSyncService(
            ILogger logger,
            IUserManager userManager,
            IUserDataManager userDataManager,
            ILibraryManager libraryManager,
            IDtoService dtoService)
        {
            _logger = logger;
            _userManager = userManager;
            _userDataManager = userDataManager;
            _libraryManager = libraryManager;
            _dtoService = dtoService;
        }

        public CheckpointId Post(CreateCheckpoint request)
        {
            _logger.LogDebug($"InfuseSync: Create checkpoint request for DeviceID '{request.DeviceID}' UserID '{request.UserID}'");

            var newCheckpoint = Plugin.Instance.Db.CreateCheckpoint(request.DeviceID, request.UserID);

            return new CheckpointId { Id = newCheckpoint.Guid };
        }

        public SyncStats Post(StartCheckpointSync request)
        {
            _logger.LogDebug($"InfuseSync: Sync request for CheckpointID '{request.CheckpointID}'");

            var checkpoint = Plugin.Instance.Db.GetCheckpoint(request.CheckpointID);
            if (checkpoint == null)
            {
                throw new ResourceNotFoundException($"Checkpoint with ID '{request.CheckpointID}' not found.");
            }

            var db = Plugin.Instance.Db;

            var syncTimestamp = DateTime.UtcNow.ToFileTime();
            db.UpdateCheckpoint(request.CheckpointID, syncTimestamp);

            var folderTypes = new string [] {"Folder"};
            var boxSetTypes = new string [] {"BoxSet"};
            var playlistTypes = new string [] {"Playlist"};
            var seriesTypes = new string [] {"Series"};
            var seasonTypes = new string [] {"Season"};
            var collectionFolderTypes = new string [] {"CollectionFolder"};
            var videoTypes = new string [] {"Video", "MusicVideo", "Movie", "Episode"};

            return new SyncStats {
                UpdatedFolders = db.ItemsCount(checkpoint.Timestamp, syncTimestamp, ItemStatus.Updated, folderTypes),
                RemovedFolders = db.ItemsCount(checkpoint.Timestamp, syncTimestamp, ItemStatus.Removed, folderTypes),
                UpdatedBoxSets = db.ItemsCount(checkpoint.Timestamp, syncTimestamp, ItemStatus.Updated, boxSetTypes),
                RemovedBoxSets = db.ItemsCount(checkpoint.Timestamp, syncTimestamp, ItemStatus.Removed, boxSetTypes),
                UpdatedPlaylists = db.ItemsCount(checkpoint.Timestamp, syncTimestamp, ItemStatus.Updated, playlistTypes),
                RemovedPlaylists = db.ItemsCount(checkpoint.Timestamp, syncTimestamp, ItemStatus.Removed, playlistTypes),
                UpdatedTvShows = db.ItemsCount(checkpoint.Timestamp, syncTimestamp, ItemStatus.Updated, seriesTypes),
                RemovedTvShows = db.ItemsCount(checkpoint.Timestamp, syncTimestamp, ItemStatus.Removed, seriesTypes),
                UpdatedSeasons = db.ItemsCount(checkpoint.Timestamp, syncTimestamp, ItemStatus.Updated, seasonTypes),
                RemovedSeasons = db.ItemsCount(checkpoint.Timestamp, syncTimestamp, ItemStatus.Removed, seasonTypes),
                UpdatedVideos = db.ItemsCount(checkpoint.Timestamp, syncTimestamp, ItemStatus.Updated, videoTypes),
                RemovedVideos = db.ItemsCount(checkpoint.Timestamp, syncTimestamp, ItemStatus.Removed, videoTypes),
                UpdatedCollectionFolders = db.ItemsCount(checkpoint.Timestamp, syncTimestamp, ItemStatus.Updated, collectionFolderTypes),
                UpdatedUserData = db.UserInfoCount(checkpoint.Timestamp, syncTimestamp, checkpoint.UserId, videoTypes)
            };
        }

        public QueryResult<BaseItemDto> Get(GetUpdatedItemsQuery request)
        {
            _logger.LogDebug($"InfuseSync: Updated items requested for CheckpointID '{request.CheckpointID}'");

            var checkpoint = Plugin.Instance.Db.GetCheckpoint(request.CheckpointID);
            if (checkpoint == null)
            {
                throw new ResourceNotFoundException($"Checkpoint with ID '{request.CheckpointID}' not found.");
            }
            if (checkpoint.SyncTimestamp == null)
            {
                throw new ArgumentException($"Sync session should be started before using the checkpoint.");
            }

            var includeTypes = request.IncludeItemTypes?.Split(',');

            var itemsUpdated = Plugin.Instance.Db.GetItems(
                checkpoint.Timestamp,
                checkpoint.SyncTimestamp.Value,
                ItemStatus.Updated,
                includeTypes,
                request.StartIndex ?? 0,
                request.Limit ?? int.MaxValue
            );

            var totalCount = Plugin.Instance.Db.ItemsCount(
                checkpoint.Timestamp,
                checkpoint.SyncTimestamp.Value,
                ItemStatus.Updated,
                includeTypes
            );

            var user = _userManager.GetUserById(Guid.Parse(checkpoint.UserId));
            if (user == null)
            {
                throw new ResourceNotFoundException($"User not found for checkpoint with ID '{request.CheckpointID}'.");
            }

            var items = GetUserItems(user, itemsUpdated);

            var options = new DtoOptions { Fields = request.GetItemFields() };
            var itemDtos = _dtoService.GetBaseItemDtos(items, options, user);

            return new QueryResult<BaseItemDto> {
                Items = itemDtos,
#if JELLYFIN
                StartIndex = request.StartIndex ?? 0,
#endif
                TotalRecordCount = totalCount
            };
        }

        private BaseItem[] GetUserItems(User user, IEnumerable<ItemRec> itemRecs)
        {
            List<BaseItem> items = new List<BaseItem>();
            foreach (ItemRec rec in itemRecs)
            {
                var item = _libraryManager.GetItemById(rec.Guid);
                if (item != null
                    && !(item is AggregateFolder)
                    && item.IsVisibleStandalone(user))
                {
                    items.Add(item);
                }
            }

            return items.ToArray();
        }

        public QueryResult<RemovedItem> Get(GetRemovedItemsQuery request)
        {
            _logger.LogDebug($"InfuseSync: Removed items requested for CheckpointID '{request.CheckpointID}'");

            var checkpoint = Plugin.Instance.Db.GetCheckpoint(request.CheckpointID);
            if (checkpoint == null)
            {
                throw new ResourceNotFoundException($"Checkpoint with ID '{request.CheckpointID}' not found.");
            }
            if (checkpoint.SyncTimestamp == null)
            {
                throw new ArgumentException($"Sync session should be started before using the checkpoint.");
            }

            var includeTypes = request.IncludeItemTypes?.Split(',');

            var itemsRemoved = Plugin.Instance.Db.GetItems(
                checkpoint.Timestamp,
                checkpoint.SyncTimestamp.Value,
                ItemStatus.Removed,
                includeTypes,
                request.StartIndex ?? 0,
                request.Limit ?? int.MaxValue
            );

            var totalCount = Plugin.Instance.Db.ItemsCount(
                checkpoint.Timestamp,
                checkpoint.SyncTimestamp.Value,
                ItemStatus.Removed,
                includeTypes
            );

            var removedItems = itemsRemoved.Select(x => new RemovedItem {
#if EMBY
                ItemId = x.ItemId,
                SeriesId = x.SeriesId?.ToString(),
#else
                ItemId = x.Guid,
                SeriesId = x.SeriesId,
#endif
                Season = x.Season
            }).ToArray();

            return new QueryResult<RemovedItem> {
                Items = removedItems,
#if JELLYFIN
                StartIndex = request.StartIndex ?? 0,
#endif
                TotalRecordCount = totalCount
            };
        }

        public QueryResult<UserItemDataDto> Get(GetUserDataQuery request)
        {
            _logger.LogDebug($"InfuseSync: User data requested for CheckpointID '{request.CheckpointID}'");

            var checkpoint = Plugin.Instance.Db.GetCheckpoint(request.CheckpointID);
            if (checkpoint == null)
            {
                throw new ResourceNotFoundException($"Checkpoint with ID '{request.CheckpointID}' not found.");
            }
            if (checkpoint.SyncTimestamp == null)
            {
                throw new ArgumentException($"Sync session should be started before using the checkpoint.");
            }

            var includeTypes = request.IncludeItemTypes?.Split(',');

            var updatedUserData = Plugin.Instance.Db.GetUserInfos(
                checkpoint.Timestamp,
                checkpoint.SyncTimestamp.Value,
                checkpoint.UserId,
                includeTypes,
                request.StartIndex ?? 0,
                request.Limit ?? int.MaxValue
            );

            var totalCount = Plugin.Instance.Db.UserInfoCount(
                checkpoint.Timestamp,
                checkpoint.SyncTimestamp.Value,
                checkpoint.UserId,
                includeTypes
            );

            var user = _userManager.GetUserById(Guid.Parse(checkpoint.UserId));
            if (user == null)
            {
                throw new ResourceNotFoundException($"User not found for checkpoint with ID '{request.CheckpointID}'.");
            }

            var userData = updatedUserData
#if EMBY
                .Select(data => new KeyValuePair<string, BaseItem>(data.ItemId, _libraryManager.GetItemById(data.Guid)))
#else
                .Select(data => KeyValuePair.Create(data.Guid, _libraryManager.GetItemById(data.Guid)))
#endif
                .Where(pair => pair.Value != null)
                .Select(pair => {
                    var dto = _userDataManager.GetUserDataDto(pair.Value, user);

                    dto.ItemId = pair.Key;

                    return dto;
                })
                .ToArray();

            return new QueryResult<UserItemDataDto> {
                Items = userData,
#if JELLYFIN
                StartIndex = request.StartIndex ?? 0,
#endif
                TotalRecordCount = totalCount
            };
        }

        public List<VirtualFolderInfo> Get(GetUserFolders request)
        {
            _logger.LogDebug($"InfuseSync: User folders requested for UserID '{request.UserID}'");

            var user = _userManager.GetUserById(Guid.Parse(request.UserID));
            if (user == null)
            {
                throw new ResourceNotFoundException($"User with ID '{request.UserID}' not found.");
            }

            return _libraryManager.GetVirtualFolders()
                .Where(f =>
                {
                    var item = _libraryManager.GetItemById(f.ItemId);
                    return item != null && item.IsVisibleStandalone(user);
                })
                .ToList();
        }
    }
}
