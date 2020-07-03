using System;

namespace InfuseSync.Models
{
    public enum ItemStatus
    {
        Updated = 0,
        Removed = 1
    }

    public class ItemRec
    {
        public Guid Guid { get; set; }
#if EMBY
        public string ItemId { get; set; }
        public long? SeriesId { get; set; }
#else
        public Guid? SeriesId { get; set; }
#endif
        public int? Season { get; set; }
        public ItemStatus Status { get; set; }
        public long LastModified { get; set; }
        public string Type { get; set; }
    }
}
