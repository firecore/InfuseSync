using System;

namespace InfuseSync.Models
{
    public class RemovedItem
    {
#if EMBY
        public string ItemId { get; set; }
        public string SeriesId { get; set; }
#else
        public Guid ItemId { get; set; }
        public Guid? SeriesId { get; set; }
#endif
        public int? Season { get; set; }
    }
}