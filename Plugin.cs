using System;
using System.IO;
using System.Linq;
using Emby.Bingers.Sync;
using Emby.Web.GenericEdit.Common;
using MediaBrowser.Common;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;

namespace Emby.Bingers
{
    public class Plugin : BasePluginSimpleUI<PluginOptions>, IHasThumbImage
    {
        public const string PluginName = "Bingers";

        private static readonly Guid PluginId = new Guid("6f2c1f4e-8a57-4d0b-9b8e-3c1d2b7a5e41");

        private readonly IUserManager userManager;
        private readonly ILogger logger;

        public Plugin(IApplicationHost applicationHost, ILogManager logManager, IUserManager userManager)
            : base(applicationHost)
        {
            this.userManager = userManager;
            this.logger = logManager.GetLogger(PluginName);
            Instance = this;
        }

        public static Plugin Instance { get; private set; }

        public override string Name => PluginName;

        public override string Description => "Two-way sync of watched movies and episodes with Bingers (bingers.app).";

        public override Guid Id => PluginId;

        public PluginOptions Options => this.GetOptions();

        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        public Stream GetThumbImage()
            => this.GetType().Assembly.GetManifestResourceStream(this.GetType().Namespace + ".thumb.png");

        protected override void OnCreatePageInfo(PluginPageInfo pageInfo)
        {
            base.OnCreatePageInfo(pageInfo);
            pageInfo.EnableInMainMenu = true;
            pageInfo.DisplayName = PluginName;
            pageInfo.MenuIcon = "sync";
        }

        protected override PluginOptions OnBeforeShowUI(PluginOptions options)
        {
            try
            {
#pragma warning disable CS0618 // Users is marked obsolete but is the one listing available across Emby 4.8–4.10.
                options.UserChoices = this.userManager.Users
#pragma warning restore CS0618
                    .OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(u => new EditorSelectOption(u.Id.ToString("N"), u.Name))
                    .ToList();

                var service = SyncService.Instance;
                if (service != null)
                {
                    service.DescribeStatus(options);
                }
            }
            catch (Exception ex)
            {
                this.logger.ErrorException("Bingers: could not prepare the settings page", ex);
            }

            return base.OnBeforeShowUI(options);
        }

        protected override void OnOptionsSaved(PluginOptions options)
        {
            base.OnOptionsSaved(options);
            this.logger.Info("Bingers: settings saved");
            SyncService.Instance?.OnOptionsSaved(options);
        }
    }
}
