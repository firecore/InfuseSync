namespace InfuseSync.Models
{
    public class SyncStats
    {
        public int UpdatedFolders { get; set; }
        public int RemovedFolders { get; set; }
        public int UpdatedBoxSets { get; set; }
        public int RemovedBoxSets { get; set; }
        public int UpdatedPlaylists { get; set; }
        public int RemovedPlaylists { get; set; }
        public int UpdatedTvShows { get; set; }
        public int RemovedTvShows { get; set; }
        public int UpdatedSeasons { get; set; }
        public int RemovedSeasons { get; set; }
        public int UpdatedVideos { get; set; }
        public int RemovedVideos { get; set; }
        public int UpdatedCollectionFolders { get; set; }
        public int UpdatedUserData { get; set; }
    }
}