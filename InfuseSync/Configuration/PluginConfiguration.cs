using MediaBrowser.Model.Plugins;

namespace InfuseSync.Configuration
{
    public class PluginConfiguration: BasePluginConfiguration
    {
        public int CacheExpirationDays { get; set; }

        public PluginConfiguration()
        {
            CacheExpirationDays = 30;
        }
    }
}