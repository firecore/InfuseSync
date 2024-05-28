using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using InfuseSync.Configuration;
using InfuseSync.Storage;

#if EMBY
using System.IO;
using MediaBrowser.Common;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Logging;
using InfuseSync.Logging;
#else
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger<InfuseSync.Plugin>;
#endif

namespace InfuseSync
{
#if EMBY
    public class Plugin: BasePluginSimpleUI<PluginOptions>, IHasThumbImage
#else
    public class Plugin: BasePlugin<PluginConfiguration>, IHasWebPages
#endif
    {
#if EMBY
        public PluginOptions Configuration
        {
            get => GetOptions();
        }

        public Plugin(IApplicationHost applicationHost, ILogManager logManager) : base(applicationHost)
        {
            Instance = this;

            var logger = logManager.GetLogger(this.Name);

            logger.LogInformation("InfuseSync is starting.");

            var applicationPaths = applicationHost.Resolve<IApplicationPaths>();

            Db = new Db(applicationPaths.DataPath, logger);
        }
#else
        public Plugin(
            IApplicationPaths applicationPaths,
            IXmlSerializer xmlSerializer,
            ILogger logger) : base(applicationPaths, xmlSerializer)
        {
            Instance = this;

            logger.LogInformation("InfuseSync is starting.");

            Db = new Db(applicationPaths.DataPath, logger);
        }
#endif

        public Db Db { get; }

        public override string Name => "InfuseSync";

        public override string Description
            => "Plugin for fast synchronization with Infuse.";

        public override Guid Id => Guid.Parse("022a3003-993f-45f1-8565-87d12af2e12a");

        public static Plugin Instance { get; private set; }

#if EMBY
        public Stream GetThumbImage()
        {
            var type = GetType();
            return type.Assembly.GetManifestResourceStream(type.Namespace + ".thumb.png");
        }

        public ImageFormat ThumbImageFormat
        {
            get
            {
                return ImageFormat.Png;
            }
        }
#endif

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "InfuseSyncConfigPage",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
                }
            };
        }
    }
}