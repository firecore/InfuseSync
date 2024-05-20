namespace InfuseSync.Configuration
{
    using System.ComponentModel;
    using Emby.Web.GenericEdit;

    public class PluginOptions : EditableOptionsBase
    {
        public override string EditorTitle => "Infuse Sync";

        [DisplayName("Cache Expiration")]
        [Description("Maximum days to keep cached data")]
        public int CacheExpirationDays { get; set; }

        public PluginOptions()
        {
            CacheExpirationDays = 30;
        }
    }
}
