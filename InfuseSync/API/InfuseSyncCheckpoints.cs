using System;
using MediaBrowser.Model.Services;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Net;
using InfuseSync.Models;

#if EMBY
using MediaBrowser.Model.Logging;
using InfuseSync.Logging;
#else
using Microsoft.Extensions.Logging;
#endif

namespace InfuseSync.API
{
    [Route("/InfuseSync/Checkpoint", "POST", Summary = "Create new synchronization checkpoint and remove previous device checkpoints")]
    [Authenticated]
    public class CreateCheckpoint : IReturn<CheckpointId>
    {
        [ApiMember(Name = "DeviceID", Description = "Unique device identifier", IsRequired = true, DataType = "string", ParameterType = "query", Verb = "POST")]
        [ApiMember(Name = "UserID", Description = "User identifier", IsRequired = true, DataType = "string", ParameterType = "query", Verb = "POST")]
        public string DeviceID { get; set; }
        public string UserID { get; set; }
    }

    [Route("/InfuseSync/Checkpoint/{CheckpointID}/StartSync", "POST", Summary = "Start synchronization session for a checkpoint and return items statistics")]
    [Authenticated]
    public class StartCheckpointSync : IReturn<SyncStats>
    {
        [ApiMember(Name = "CheckpointID", Description = "Checkpoint identifier", IsRequired = true, DataType = "string", ParameterType = "path", Verb = "POST")]
        public Guid CheckpointID { get; set; }
    }

    public class InfuseSyncCheckpoints : IService
    {
        private readonly ILogger _logger;

        public InfuseSyncCheckpoints(ILogger logger)
        {
            _logger = logger;
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

            var boxSetTypes = new string [] {"BoxSet"};
            var seriesTypes = new string [] {"Series"};
            var seasonTypes = new string [] {"Season"};
            var collectionFolderTypes = new string [] {"CollectionFolder"};
            var videoTypes = new string [] {"Video", "MusicVideo", "Movie", "Episode"};

            return new SyncStats {
                UpdatedBoxSets = db.ItemsCount(checkpoint.Timestamp, syncTimestamp, ItemStatus.Updated, boxSetTypes),
                RemovedBoxSets = db.ItemsCount(checkpoint.Timestamp, syncTimestamp, ItemStatus.Removed, boxSetTypes),
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
    }
}
