using System;
namespace InfuseSync.Models
{
    public class UserInfoRec
    {
        public Guid Guid { get; set; }
#if EMBY
        public string ItemId { get; set; }
#endif
        public string UserId { get; set; }
        public long LastModified { get; set; }
        public string Type { get; set; }
    }
}
