using InfuseSync.EntryPoints;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace InfuseSync
{
    public class PluginServiceRegistrator: IPluginServiceRegistrator
    {
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            serviceCollection.AddHostedService<LibrarySyncManager>();
            serviceCollection.AddHostedService<UserSyncManager>();
        }
    }
}