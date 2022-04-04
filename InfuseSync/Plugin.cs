using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using InfuseSync.Configuration;
using InfuseSync.Storage;

#if EMBY
using System.IO;
using MediaBrowser.Model.Drawing;
using InfuseSync.Logging;
using ILogger = MediaBrowser.Model.Logging.ILogger;
#else
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger<InfuseSync.Plugin>;
#endif

namespace InfuseSync
{
#if EMBY
    public class Plugin: BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage
#else
    public class Plugin: BasePlugin<PluginConfiguration>, IHasWebPages
#endif
    {
        public Plugin(
            IApplicationPaths applicationPaths,
            IXmlSerializer xmlSerializer,
            ILogger logger) : base(applicationPaths, xmlSerializer)
        {
            Instance = this;

            logger.LogInformation("InfuseSync is starting.");

            Db = new Db(applicationPaths.DataPath, logger);
        }

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