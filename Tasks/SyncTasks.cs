using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Emby.Bingers.Sync;
using MediaBrowser.Model.Tasks;

namespace Emby.Bingers.Tasks
{
    /// <summary>
    /// Every 15 minutes (and shortly after start-up): send anything still queued, apply what
    /// changed on Bingers, and on the first run import Emby's existing watched history.
    /// </summary>
    public sealed class SyncWithBingersTask : IScheduledTask
    {
        public string Name => "Sync with Bingers";

        public string Key => "BingersSync";

        public string Description => "Sends queued Emby watches to Bingers, marks Bingers watches as played in Emby, and imports Emby's watched history once.";

        public string Category => "Bingers";

        public Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var service = SyncService.Instance ?? throw new InvalidOperationException("The Bingers plugin has not started yet.");
            return service.RunScheduledSyncAsync(cancellationToken, progress);
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
        {
            new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerStartup },
            new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerInterval, IntervalTicks = TimeSpan.FromMinutes(15).Ticks },
        };
    }

    /// <summary>Manual recovery: forget the cursor and the backfill flag and sync everything again.</summary>
    public sealed class FullResyncTask : IScheduledTask
    {
        public string Name => "Full resync with Bingers";

        public string Key => "BingersFullResync";

        public string Description => "Re-reads your whole Bingers history and re-checks every played item in Emby. Safe to run; nothing is recorded twice.";

        public string Category => "Bingers";

        public Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var service = SyncService.Instance ?? throw new InvalidOperationException("The Bingers plugin has not started yet.");
            return service.FullResyncAsync(cancellationToken, progress);
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
    }
}
