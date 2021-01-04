using System;

namespace InfuseSync.Models
{
    public class Checkpoint
    {
        public Guid Guid { get; set; }
        public string DeviceId { get; set; }
        public string UserId { get; set; }
        public long Timestamp { get; set; }
        public long? SyncTimestamp { get; set; }
    }
}