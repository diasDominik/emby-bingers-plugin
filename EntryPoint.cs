using Emby.Bingers.Sync;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Logging;

namespace Emby.Bingers
{
    /// <summary>Started by Emby at server start-up; owns the sync service.</summary>
    public sealed class EntryPoint : IServerEntryPoint
    {
        private readonly SyncService service;

        public EntryPoint(ILibraryManager libraryManager, IUserManager userManager, IUserDataManager userDataManager, IApplicationPaths paths, ILogManager logManager)
        {
            this.service = new SyncService(libraryManager, userManager, userDataManager, paths, logManager.GetLogger(Plugin.PluginName));
        }

        public void Run() => this.service.Start();

        public void Dispose() => this.service.Dispose();
    }
}
