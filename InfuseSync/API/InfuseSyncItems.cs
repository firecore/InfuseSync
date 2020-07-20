using System;
using System.Linq;
using System.Collections.Generic;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Services;
using MediaBrowser.Model.Querying;
using InfuseSync.Models;

#if EMBY
using MediaBrowser.Model.Logging;
using InfuseSync.Logging;
#else
using Jellyfin.Data.Entities;
using System.Globalization;
using Microsoft.Extensions.Logging;
#endif

namespace InfuseSync.API
{
    [Route("/InfuseSync/Checkpoint/{CheckpointID}/UpdatedItems", "GET", Summary = "Get updated items for {CheckpointID}")]
    [Authenticated]
    public class GetUpdatedItemsQuery : IReturn<QueryResult<ItemRec>>
    {
        [ApiMember(Name = "CheckpointID", Description = "Checkpoint identifier", IsRequired = true, DataType = "string", ParameterType = "path", Verb = "GET")]
        [ApiMember(Name = "IncludeItemTypes", Description = "Optional list of item types to include in the result", IsRequired = false, DataType = "string", ParameterType = "query", Verb = "GET")]
        [ApiMember(Name = "Fields", Description = "Optional. Specify additional fields of information to return in the output. This allows multiple, comma delimeted values.", IsRequired = false, DataType = "string", ParameterType = "query", Verb = "GET", AllowMultiple = true)]
        [ApiMember(Name = "StartIndex", Description = "Offset for items to fetch", IsRequired = false, DataType = "int", ParameterType = "query", Verb = "GET")]
        [ApiMember(Name = "Limit", Description = "Maximum number of items to fetch", IsRequired = false, DataType = "int", ParameterType = "query", Verb = "GET")]
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

    [Route("/InfuseSync/Checkpoint/{CheckpointID}/RemovedItems", "GET", Summary = "Get removed item IDs for {CheckpointID}")]
    [Authenticated]
    public class GetRemovedItemsQuery : IReturn<QueryResult<RemovedItem>>
    {
        [ApiMember(Name = "CheckpointID", Description = "Checkpoint identifier", IsRequired = true, DataType = "string", ParameterType = "path", Verb = "GET")]
        [ApiMember(Name = "IncludeItemTypes", Description = "Optional list of item types to include in the result", IsRequired = false, DataType = "string", ParameterType = "query", Verb = "GET")]
        [ApiMember(Name = "StartIndex", Description = "Offset for items to fetch", IsRequired = false, DataType = "int", ParameterType = "query", Verb = "GET")]
        [ApiMember(Name = "Limit", Description = "Maximum number of items to fetch", IsRequired = false, DataType = "int", ParameterType = "query", Verb = "GET")]
        public Guid CheckpointID { get; set; }
        public string IncludeItemTypes { get; set; }
        public int? StartIndex { get; set; }
        public int? Limit { get; set; }
    }

    [Route("/InfuseSync/Checkpoint/{CheckpointID}/UserData", "GET", Summary = "Get updated user data for {CheckpointID}")]
    [Authenticated]
    public class GetUserDataQuery : IReturn<QueryResult<UserItemDataDto>>
    {
        [ApiMember(Name = "CheckpointID", Description = "Checkpoint identifier", IsRequired = true, DataType = "string", ParameterType = "path", Verb = "GET")]
        [ApiMember(Name = "IncludeItemTypes", Description = "Optional list of item types to include in the result", IsRequired = false, DataType = "string", ParameterType = "query", Verb = "GET")]
        [ApiMember(Name = "StartIndex", Description = "Offset for items to fetch", IsRequired = false, DataType = "int", ParameterType = "query", Verb = "GET")]
        [ApiMember(Name = "Limit", Description = "Maximum number of items to fetch", IsRequired = false, DataType = "int", ParameterType = "query", Verb = "GET")]
        public Guid CheckpointID { get; set; }
        public string IncludeItemTypes { get; set; }
        public int? StartIndex { get; set; }
        public int? Limit { get; set; }
    }

    public class InfuseSyncItems : IService
    {
#if EMBY
        private readonly ILogger _logger;
#else
        private readonly ILogger<InfuseSyncItems> _logger;
#endif

        private readonly IUserManager _userManager;
        private readonly IUserDataManager _userDataManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IDtoService _dtoService;

#if EMBY
        public InfuseSyncItems(ILogger logger, IUserManager userManager, IUserDataManager userDataManager, ILibraryManager libraryManager, IDtoService dtoService)
#else
        public InfuseSyncItems(ILogger<InfuseSyncItems> logger, IUserManager userManager, IUserDataManager userDataManager, ILibraryManager libraryManager, IDtoService dtoService)
#endif
        {
            _logger = logger;
            _userManager = userManager;
            _userDataManager = userDataManager;
            _libraryManager = libraryManager;
            _dtoService = dtoService;
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

            var userData = updatedUserData
#if EMBY
                .Select(data => new KeyValuePair<string, BaseItem>(data.ItemId, _libraryManager.GetItemById(data.Guid)))
#else
                .Select(data => KeyValuePair.Create(data.Guid, _libraryManager.GetItemById(data.Guid)))
#endif
                .Where(pair => pair.Value != null)
                .Select(pair => {
                    var dto = _userDataManager.GetUserDataDto(pair.Value, user);
#if EMBY
                    dto.ItemId = pair.Key;
#else
                    dto.ItemId = pair.Key.ToString("N", CultureInfo.InvariantCulture);
#endif
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
    }
}
