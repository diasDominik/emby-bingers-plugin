using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Bingers.Api;
using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace Emby.Bingers.Sync
{
    /// <summary>
    /// The sync engine. One instance, created by <see cref="EntryPoint"/>.
    /// Emby → Bingers: UserDataSaved events are queued and flushed in batches to /scrobble.
    /// Bingers → Emby: the /entries change feed is read with a stored cursor and applied to the
    /// chosen Emby user. Writes into Emby use UserDataSaveReason.Import, which the event
    /// handler ignores, so nothing echoes back.
    /// </summary>
    public sealed class SyncService : IDisposable
    {
        private const int MaxPendingAttempts = 6;
        private const int MaxPending = 20000;
        private static readonly TimeSpan PushDelay = TimeSpan.FromSeconds(10);

        private readonly ILibraryManager libraryManager;
        private readonly IUserManager userManager;
        private readonly IUserDataManager userDataManager;
        private readonly ILogger logger;
        private readonly string statePath;
        private readonly BingersClient client;

        private readonly SemaphoreSlim workLock = new SemaphoreSlim(1, 1);
        private readonly object stateLock = new object();
        private readonly HashSet<string> recentlyPushed = new HashSet<string>(StringComparer.Ordinal);
        private readonly Timer pushTimer;

        private SyncState state;
        private Dictionary<string, object> cachedMe;
        private DateTime cachedMeAt;
        private string cachedMeError;

        public SyncService(ILibraryManager libraryManager, IUserManager userManager, IUserDataManager userDataManager, IApplicationPaths paths, ILogger logger)
        {
            this.libraryManager = libraryManager;
            this.userManager = userManager;
            this.userDataManager = userDataManager;
            this.logger = logger;
            this.statePath = Path.Combine(paths.PluginConfigurationsPath, "Emby.Bingers.state.json");
            this.client = new BingersClient(() => Options?.AccessToken, this.Debug);
            this.state = SyncState.Load(this.statePath);
            this.pushTimer = new Timer(_ => this.FireAndForget(ct => this.FlushPendingAsync(ct)), null, Timeout.Infinite, Timeout.Infinite);
            Instance = this;
        }

        public static SyncService Instance { get; private set; }

        private static PluginOptions Options => Plugin.Instance?.Options;

        private bool IsConfigured
        {
            get
            {
                var o = Options;
                return o != null && !string.IsNullOrWhiteSpace(o.AccessToken) && !string.IsNullOrWhiteSpace(o.EmbyUserId);
            }
        }

        public void Start()
        {
            this.userDataManager.UserDataSaved += this.OnUserDataSaved;
            if (this.IsConfigured)
            {
                this.EnsureAccount();
                if (this.state.Pending.Count > 0)
                {
                    this.pushTimer.Change(TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
                }
            }

            this.Info("Bingers sync started" + (this.IsConfigured ? string.Empty : " (not configured yet: add a token in the plugin settings)"));
        }

        public void Dispose()
        {
            this.userDataManager.UserDataSaved -= this.OnUserDataSaved;
            this.pushTimer.Dispose();
        }

        // =====================================================================
        // Emby → Bingers (live)
        // =====================================================================

        private void OnUserDataSaved(object sender, UserDataSaveEventArgs e)
        {
            try
            {
                var o = Options;
                if (o == null || !o.SyncToBingers || !this.IsConfigured || e?.Item == null || e.User == null || e.UserData == null)
                {
                    return;
                }

                // Import is what our own pull writes with (and Emby's own data imports): never echo it.
                if (e.SaveReason != UserDataSaveReason.PlaybackFinished && e.SaveReason != UserDataSaveReason.TogglePlayed)
                {
                    return;
                }

                if (!SameUser(e.User, o.EmbyUserId) || !ItemMapper.IsSyncable(e.Item))
                {
                    return;
                }

                PendingPush push;
                if (e.UserData.Played)
                {
                    push = new PendingPush
                    {
                        ItemId = e.Item.InternalId,
                        Watched = true,
                        FromPlayback = e.SaveReason == UserDataSaveReason.PlaybackFinished,
                        WatchedAt = e.UserData.LastPlayedDate ?? DateTimeOffset.UtcNow,
                    };
                }
                else if (e.SaveReason == UserDataSaveReason.TogglePlayed && o.SendUnwatchesToBingers)
                {
                    push = new PendingPush { ItemId = e.Item.InternalId, Watched = false };
                }
                else
                {
                    if (e.SaveReason == UserDataSaveReason.TogglePlayed)
                    {
                        // Unmarked while a watch was still queued: withdraw it, even with unwatch-sync off.
                        lock (this.stateLock)
                        {
                            this.state.Pending.RemoveAll(p => p.ItemId == e.Item.InternalId && p.Watched);
                        }
                    }

                    return; // Playback stopped before the end, or an unmark we do not send.
                }

                lock (this.stateLock)
                {
                    // Latest intent per item wins (marked, then unmarked, before a flush → only the unmark).
                    this.state.Pending.RemoveAll(p => p.ItemId == push.ItemId);
                    if (this.state.Pending.Count < MaxPending)
                    {
                        this.state.Pending.Add(push);
                    }
                }

                this.pushTimer.Change(PushDelay, Timeout.InfiniteTimeSpan);
            }
            catch (Exception ex)
            {
                this.logger.ErrorException("Bingers: could not queue a watched-state change", ex);
            }
        }

        /// <summary>Sends queued watches and unwatches. Safe to call any time.</summary>
        public async Task FlushPendingAsync(CancellationToken ct)
        {
            if (!this.IsConfigured)
            {
                return;
            }

            await this.workLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await this.FlushPendingLockedAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                this.workLock.Release();
            }
        }

        private async Task FlushPendingLockedAsync(CancellationToken ct)
        {
            List<PendingPush> batch;
            lock (this.stateLock)
            {
                batch = this.state.Pending.ToList();
            }

            if (batch.Count == 0)
            {
                return;
            }

            var watchedOnBingers = this.state.WatchedKeySet();
            var watchUnits = new List<Unit>();
            var unwatchUnits = new List<Unit>();

            foreach (var p in batch)
            {
                var item = this.libraryManager.GetItemById(p.ItemId);
                var targets = item == null ? new List<BingersTarget>() : ItemMapper.TargetsFor(item);
                if (targets.Count == 0)
                {
                    // Dropped from the queue below (it is not in "failed").
                    this.Debug("Skipping " + (item?.Name ?? "a deleted item") + ": no IMDb/TMDB/TVDB ids.");
                    continue;
                }

                foreach (var t in targets)
                {
                    if (p.Watched)
                    {
                        // A manual mark of something Bingers already has would count as a rewatch; skip it.
                        // A finished playback is a real watch, so it is always sent (Bingers ignores repeats within 12h).
                        if (!p.FromPlayback && t.Keys.Any(k => watchedOnBingers.Contains(k) || this.recentlyPushed.Contains(k)))
                        {
                            continue;
                        }

                        watchUnits.Add(new Unit(p, t));
                    }
                    else
                    {
                        unwatchUnits.Add(new Unit(p, t));
                    }
                }
            }

            var failed = new HashSet<PendingPush>();
            var sent = 0;
            try
            {
                sent += await this.SendWatchesAsync(watchUnits, failed, ct).ConfigureAwait(false);
                sent += await this.SendUnwatchesAsync(unwatchUnits, failed, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is BingersAuthException || ex is BingersRateLimitedException || ex is BingersApiException || ex is System.Net.Http.HttpRequestException)
            {
                // Units not yet sent stay queued; the next flush or the scheduled task retries them.
                foreach (var u in watchUnits.Concat(unwatchUnits).Where(u => !u.Sent))
                {
                    failed.Add(u.Push);
                }

                this.RecordError(ex);
            }

            lock (this.stateLock)
            {
                foreach (var p in batch)
                {
                    if (failed.Contains(p) && ++p.Attempts < MaxPendingAttempts)
                    {
                        continue;
                    }

                    this.state.Pending.Remove(p);
                }

                if (sent > 0)
                {
                    this.state.LastPushUtc = DateTimeOffset.UtcNow;
                }
            }

            this.SaveState();
            if (sent > 0)
            {
                this.Info("Sent " + sent.ToString(CultureInfo.InvariantCulture) + " change(s) to Bingers.");
            }
        }

        private sealed class Unit
        {
            public Unit(PendingPush push, BingersTarget target)
            {
                this.Push = push;
                this.Target = target;
            }

            public PendingPush Push { get; }

            public BingersTarget Target { get; }

            public bool Sent { get; set; }
        }

        private async Task<int> SendWatchesAsync(List<Unit> units, HashSet<PendingPush> failed, CancellationToken ct)
        {
            return await this.SendInBatchesAsync(units, failed, useFallback: false, watch: true, ct, async (chunk, fallback) =>
            {
                var items = chunk.Select(u =>
                {
                    var d = new Dictionary<string, object>(fallback ? u.Target.Fallback : u.Target.Primary);
                    if (u.Push.WatchedAt.HasValue)
                    {
                        d["watched_at"] = MinNow(u.Push.WatchedAt.Value);
                    }

                    return d;
                }).ToList();
                return await this.client.ScrobbleAsync(items, false, ct).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        private async Task<int> SendUnwatchesAsync(List<Unit> units, HashSet<PendingPush> failed, CancellationToken ct)
        {
            return await this.SendInBatchesAsync(units, failed, useFallback: false, watch: false, ct, async (chunk, fallback) =>
            {
                var actions = chunk.Select(u =>
                {
                    var d = new Dictionary<string, object>(fallback ? u.Target.Fallback : u.Target.Primary) { ["action"] = "unwatch" };
                    return d;
                }).ToList();
                return await this.client.LibraryActionsAsync(actions, ct).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Sends units 100 at a time, retries "unresolved" ones once with their fallback
        /// addressing, and returns how many were recorded (ok or already).
        /// </summary>
        private async Task<int> SendInBatchesAsync(
            List<Unit> units,
            HashSet<PendingPush> failed,
            bool useFallback,
            bool watch,
            CancellationToken ct,
            Func<List<Unit>, bool, Task<List<Dictionary<string, object>>>> send)
        {
            var recorded = 0;
            var retry = new List<Unit>();

            for (var i = 0; i < units.Count; i += BingersClient.MaxBatch)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = units.Skip(i).Take(BingersClient.MaxBatch).ToList();
                var results = await send(chunk, useFallback).ConfigureAwait(false);

                for (var j = 0; j < chunk.Count; j++)
                {
                    var u = chunk[j];
                    u.Sent = true;
                    var r = results.FirstOrDefault(x => Json.Int(x, "index") == j) ?? (j < results.Count ? results[j] : null);
                    var status = Json.Str(r, "status");

                    if (status == "ok" || status == "already")
                    {
                        recorded++;
                        this.RememberPushed(u.Target, r, watch);
                    }
                    else if (status == "unresolved" && !useFallback && u.Target.Fallback != null)
                    {
                        retry.Add(u);
                    }
                    else if (status == "unresolved")
                    {
                        this.Info("Bingers could not match " + u.Target.Label + ": " + (Json.Str(r, "message") ?? "unresolved") + ".");
                    }
                    else
                    {
                        this.Warn("Bingers did not record " + u.Target.Label + ": " + (Json.Str(r, "message") ?? status ?? "no result") + ".");
                        failed.Add(u.Push);
                    }
                }
            }

            if (retry.Count > 0)
            {
                foreach (var u in retry)
                {
                    u.Sent = false;
                }

                recorded += await this.SendInBatchesAsync(retry, failed, true, watch, ct, send).ConfigureAwait(false);
            }

            return recorded;
        }

        /// <summary>
        /// Keeps the "what Bingers has" index current right after a push, using the ids in the
        /// result, so a removal on Bingers before the next pull is still recognised and an Emby
        /// re-mark after an unwatch is not mistaken for a duplicate.
        /// </summary>
        private void RememberPushed(BingersTarget target, Dictionary<string, object> result, bool watch)
        {
            lock (this.stateLock)
            {
                if (!watch)
                {
                    foreach (var k in target.Keys)
                    {
                        this.recentlyPushed.Remove(k);
                    }

                    var gone = this.state.BingersWatched.Where(kv => kv.Value.Any(target.Keys.Contains)).Select(kv => kv.Key).ToList();
                    foreach (var id in gone)
                    {
                        this.state.BingersWatched.Remove(id);
                    }

                    return;
                }

                foreach (var k in target.Keys)
                {
                    this.recentlyPushed.Add(k);
                }

                var title = Json.Obj(result, "title");
                var ep = Json.Obj(result, "episode");
                var entityId = Json.Str(ep, "id") ?? (Json.Str(title, "kind") == "movie" ? Json.Str(title, "id") : null);
                if (entityId == null)
                {
                    return;
                }

                // Keep Emby's keys and Bingers' own, so later matches work from either side's numbering.
                var keys = new HashSet<string>(target.Keys, StringComparer.Ordinal);
                if (this.state.BingersWatched.TryGetValue(entityId, out var existing))
                {
                    keys.UnionWith(existing);
                }

                if (title != null)
                {
                    keys.UnionWith(ItemMapper.KeysForBingers(ep != null ? "show" : "movie", Json.Obj(title, "ids"), Json.Int(ep, "season"), Json.Int(ep, "number")));
                }

                this.state.BingersWatched[entityId] = keys.ToList();
            }
        }

        // =====================================================================
        // Bingers → Emby (change feed)
        // =====================================================================

        /// <summary>The scheduled sync: flush queued pushes, read the feed, then backfill once.</summary>
        public async Task RunScheduledSyncAsync(CancellationToken ct, IProgress<double> progress)
        {
            if (!this.IsConfigured)
            {
                this.Info("Bingers is not configured; nothing to sync.");
                return;
            }

            await this.workLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                this.EnsureAccount();
                progress?.Report(2);
                await this.FlushPendingLockedAsync(ct).ConfigureAwait(false);
                progress?.Report(10);
                var pullSummary = await this.PullLockedAsync(ct, new Progress<double>(p => progress?.Report(10 + (p * 0.6)))).ConfigureAwait(false);
                progress?.Report(70);
                var backfillSummary = await this.BackfillLockedAsync(ct, new Progress<double>(p => progress?.Report(70 + (p * 0.3)))).ConfigureAwait(false);

                lock (this.stateLock)
                {
                    this.state.LastSummary = DateTimeOffset.Now.ToString("g", CultureInfo.CurrentCulture) + ": " + pullSummary + (backfillSummary == null ? string.Empty : " " + backfillSummary);
                    this.state.LastError = null;
                }

                this.SaveState();
                progress?.Report(100);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                this.RecordError(ex);
                throw;
            }
            finally
            {
                this.workLock.Release();
            }
        }

        /// <summary>Forgets the feed cursor, the Bingers index and the backfill flag, then syncs from scratch.</summary>
        public async Task FullResyncAsync(CancellationToken ct, IProgress<double> progress)
        {
            await this.workLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                lock (this.stateLock)
                {
                    var pending = this.state.Pending;
                    this.state = new SyncState { AccountKey = this.state.AccountKey, Pending = pending };
                    this.recentlyPushed.Clear();
                }

                this.SaveState();
                this.Info("Bingers sync state reset; running a full sync.");
            }
            finally
            {
                this.workLock.Release();
            }

            await this.RunScheduledSyncAsync(ct, progress).ConfigureAwait(false);
        }

        private async Task<string> PullLockedAsync(CancellationToken ct, IProgress<double> progress)
        {
            var o = Options;
            var user = this.GetUser(o);
            var apply = o.SyncFromBingers;
            var applyUnwatches = o.SyncFromBingers && o.ApplyUnwatchesFromBingers;
            var initial = !this.state.InitialPullDone;

            var titles = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            var episodeNumbers = new Dictionary<string, Dictionary<string, (int season, int number)>>(StringComparer.Ordinal);
            var emby = new EmbyIndex(this.libraryManager, user);

            int rows = 0, played = 0, unplayed = 0, unmatched = 0, pages = 0;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var page = await this.client.GetEntriesAsync(this.state.EntriesCursor, initial ? 5000 : 1000, ct).ConfigureAwait(false);
                pages++;

                // Resolve the titles this page needs that we do not have keys for yet.
                // Unwatches of entities we never saw watched still need ids when they are to be applied.
                var needTitles = page.Items
                    .Where(r => (IsWatchedRow(r) || (applyUnwatches && !initial)) && !this.state.BingersWatched.ContainsKey(Json.Str(r, "entity_id") ?? string.Empty))
                    .Select(r => Json.Str(r, "title_id") ?? Json.Str(r, "entity_id"))
                    .Where(id => id != null && !titles.ContainsKey(id))
                    .Distinct()
                    .ToList();
                for (var i = 0; i < needTitles.Count; i += BingersClient.MaxBatch)
                {
                    foreach (var t in await this.client.GetTitlesAsync(needTitles.Skip(i).Take(BingersClient.MaxBatch), ct).ConfigureAwait(false))
                    {
                        var id = Json.Str(t, "id");
                        if (id != null)
                        {
                            titles[id] = t;
                        }
                    }
                }

                var changes = 0;
                foreach (var row in page.Items)
                {
                    rows++;
                    var entityId = Json.Str(row, "entity_id");
                    if (entityId == null)
                    {
                        continue;
                    }

                    if (IsWatchedRow(row))
                    {
                        if (!this.state.BingersWatched.TryGetValue(entityId, out var keys))
                        {
                            keys = await this.ResolveKeysAsync(row, titles, episodeNumbers, ct).ConfigureAwait(false);
                            if (keys.Count == 0)
                            {
                                unmatched++;
                                continue;
                            }

                            this.state.BingersWatched[entityId] = keys;
                            changes++;
                        }

                        if (apply)
                        {
                            var at = Json.Date(row, "last_watched_at") ?? Json.Date(row, "first_watched_at");
                            var plays = Json.Int(row, "plays") ?? 1;
                            foreach (var item in emby.Find(keys))
                            {
                                if (this.MarkPlayed(user, item, at, plays, ct))
                                {
                                    played++;
                                }
                            }
                        }
                    }
                    else
                    {
                        if (!this.state.BingersWatched.TryGetValue(entityId, out var oldKeys))
                        {
                            if (!applyUnwatches || initial)
                            {
                                continue;
                            }

                            oldKeys = await this.ResolveKeysAsync(row, titles, episodeNumbers, ct).ConfigureAwait(false);
                        }

                        this.state.BingersWatched.Remove(entityId);
                        foreach (var k in oldKeys)
                        {
                            this.recentlyPushed.Remove(k);
                        }

                        changes++;
                        if (applyUnwatches && !initial)
                        {
                            foreach (var item in emby.Find(oldKeys))
                            {
                                if (this.MarkUnplayed(user, item, ct))
                                {
                                    unplayed++;
                                }
                            }
                        }
                    }
                }

                // Only advance the cursor once the whole page has been applied.
                if (!string.IsNullOrEmpty(page.NextCursor))
                {
                    this.state.EntriesCursor = page.NextCursor;
                }

                this.SaveState();
                progress?.Report(Math.Min(95, pages * 10));

                if (!page.HasMore || string.IsNullOrEmpty(page.NextCursor))
                {
                    break;
                }
            }

            this.state.InitialPullDone = true;
            this.state.LastPullUtc = DateTimeOffset.UtcNow;
            this.SaveState();

            var summary = string.Format(
                CultureInfo.InvariantCulture,
                "{0} change(s) from Bingers; {1} marked played{2} in Emby{3}.",
                rows,
                played,
                unplayed > 0 ? ", " + unplayed.ToString(CultureInfo.InvariantCulture) + " unplayed" : string.Empty,
                unmatched > 0 ? "; " + unmatched.ToString(CultureInfo.InvariantCulture) + " without usable ids" : string.Empty);
            this.Info(summary);
            return summary;
        }

        private static bool IsWatchedRow(Dictionary<string, object> row)
            => Json.Bool(row, "watched") && !Json.Bool(row, "deleted");

        private async Task<List<string>> ResolveKeysAsync(
            Dictionary<string, object> row,
            Dictionary<string, Dictionary<string, object>> titles,
            Dictionary<string, Dictionary<string, (int season, int number)>> episodeNumbers,
            CancellationToken ct)
        {
            var kind = Json.Str(row, "entity_kind");
            var entityId = Json.Str(row, "entity_id");
            var titleId = Json.Str(row, "title_id") ?? entityId;
            if (!titles.TryGetValue(titleId, out var title))
            {
                return new List<string>();
            }

            var ids = Json.Obj(title, "ids");
            if (kind == "movie")
            {
                return ItemMapper.KeysForBingers("movie", ids, null, null);
            }

            if (!episodeNumbers.TryGetValue(titleId, out var map))
            {
                map = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
                try
                {
                    foreach (var ep in await this.client.GetEpisodesAsync(titleId, ct).ConfigureAwait(false))
                    {
                        var id = Json.Str(ep, "id");
                        var season = Json.Int(ep, "season") ?? Json.Int(ep, "season_number");
                        var number = Json.Int(ep, "number") ?? Json.Int(ep, "episode") ?? Json.Int(ep, "episode_number");
                        if (id != null && season.HasValue && number.HasValue)
                        {
                            map[id] = (season.Value, number.Value);
                        }
                    }
                }
                catch (BingersApiException ex) when (ex.Status == 404 || ex.Status == 400)
                {
                    this.Debug("No episode list for " + (Json.Str(title, "title") ?? titleId) + ": " + ex.Message);
                }

                episodeNumbers[titleId] = map;
            }

            return map.TryGetValue(entityId, out var se)
                ? ItemMapper.KeysForBingers("show", ids, se.season, se.number)
                : new List<string>();
        }

        private bool MarkPlayed(User user, BaseItem item, DateTimeOffset? watchedAt, int plays, CancellationToken ct)
        {
            var data = this.userDataManager.GetUserData(user, item);
            if (data == null || data.Played)
            {
                return false;
            }

            data.Played = true;
            data.PlayCount = Math.Max(Math.Max(data.PlayCount, plays), 1);
            data.LastPlayedDate = watchedAt ?? DateTimeOffset.UtcNow;
            data.PlaybackPositionTicks = 0;
            this.userDataManager.SaveUserData(user, item, data, UserDataSaveReason.Import, ct);
            return true;
        }

        private bool MarkUnplayed(User user, BaseItem item, CancellationToken ct)
        {
            var data = this.userDataManager.GetUserData(user, item);
            if (data == null || !data.Played)
            {
                return false;
            }

            data.Played = false;
            data.PlaybackPositionTicks = 0;
            this.userDataManager.SaveUserData(user, item, data, UserDataSaveReason.Import, ct);
            return true;
        }

        // =====================================================================
        // One-time backfill: Emby's existing watched history → Bingers
        // =====================================================================

        private async Task<string> BackfillLockedAsync(CancellationToken ct, IProgress<double> progress)
        {
            var o = Options;
            if (this.state.BackfillDone || !o.SyncToBingers)
            {
                return null;
            }

            var user = this.GetUser(o);
            var query = new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { "Movie", "Episode" },
                IsPlayed = true,
                Recursive = true,
                IsVirtualItem = false,
            };
            var items = this.libraryManager.GetItemList(query);
            var watchedOnBingers = this.state.WatchedKeySet();

            var units = new List<Unit>();
            var skippedNoIds = 0;
            var alreadyThere = 0;
            foreach (var item in items.Where(ItemMapper.IsSyncable))
            {
                var targets = ItemMapper.TargetsFor(item);
                if (targets.Count == 0)
                {
                    skippedNoIds++;
                    continue;
                }

                var data = this.userDataManager.GetUserData(user, item);
                var at = data?.LastPlayedDate ?? (item.DateCreated > DateTimeOffset.MinValue ? item.DateCreated : (DateTimeOffset?)null);
                var push = new PendingPush { ItemId = item.InternalId, Watched = true, FromPlayback = false, WatchedAt = at };
                foreach (var t in targets)
                {
                    if (t.Keys.Any(watchedOnBingers.Contains))
                    {
                        alreadyThere++;
                        continue;
                    }

                    units.Add(new Unit(push, t));
                }
            }

            this.Info(string.Format(CultureInfo.InvariantCulture, "Backfill: {0} played item(s) in Emby, {1} already on Bingers, {2} to send, {3} without ids.", items.Length, alreadyThere, units.Count, skippedNoIds));

            var failed = new HashSet<PendingPush>();
            var recorded = 0;
            for (var i = 0; i < units.Count; i += 500)
            {
                // Send in slices so progress moves and a cancellation keeps most of the work.
                recorded += await this.SendWatchesAsync(units.Skip(i).Take(500).ToList(), failed, ct).ConfigureAwait(false);
                progress?.Report(100.0 * Math.Min(units.Count, i + 500) / Math.Max(1, units.Count));
            }

            if (failed.Count > 0)
            {
                // Leave the flag unset so the next run tries the rest; recorded ones dedupe on Bingers' side
                // and are skipped here once the feed reports them.
                this.Warn("Backfill: " + failed.Count.ToString(CultureInfo.InvariantCulture) + " item(s) failed and will be retried on the next run.");
            }
            else
            {
                this.state.BackfillDone = true;
            }

            this.SaveState();
            return string.Format(CultureInfo.InvariantCulture, "Backfill sent {0} watch(es) to Bingers.", recorded);
        }

        // =====================================================================
        // Settings page and configuration changes
        // =====================================================================

        public void DescribeStatus(PluginOptions options)
        {
            if (string.IsNullOrWhiteSpace(options.AccessToken))
            {
                options.Connection = new StatusItem("Bingers", "Paste a personal access token below.", ItemStatus.Unavailable);
            }
            else
            {
                try
                {
                    if (this.cachedMe == null || DateTime.UtcNow - this.cachedMeAt > TimeSpan.FromMinutes(2))
                    {
                        this.cachedMeAt = DateTime.UtcNow;
                        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                        {
                            this.cachedMe = this.client.GetMeAsync(cts.Token).GetAwaiter().GetResult() ?? new Dictionary<string, object>();
                        }

                        this.cachedMeError = null;
                    }
                }
                catch (Exception ex)
                {
                    this.cachedMe = null;
                    this.cachedMeError = ex.Message;
                }

                if (this.cachedMeError != null)
                {
                    options.Connection = new StatusItem("Bingers", this.cachedMeError, ItemStatus.Failed);
                }
                else
                {
                    var me = this.cachedMe;
                    var userObj = Json.Obj(me, "user") ?? me;
                    var who = Json.Str(userObj, "handle") ?? Json.Str(userObj, "name") ?? Json.Str(userObj, "username");
                    var scopes = string.Join(" ", ((Json.Arr(me, "scopes") ?? new List<object>()).OfType<string>()).Concat(new[] { Json.Str(me, "scope") ?? string.Empty }));
                    var readOnly = scopes.Trim().Length > 0 && scopes.IndexOf("write", StringComparison.OrdinalIgnoreCase) < 0;
                    options.Connection = readOnly
                        ? new StatusItem("Bingers", "Connected" + (who != null ? " as " + who : string.Empty) + ", but the token is read-only: create a 'read + write' token.", ItemStatus.Warning)
                        : new StatusItem("Bingers", "Connected" + (who != null ? " as " + who : string.Empty) + ".", ItemStatus.Succeeded);
                }
            }

            SyncState s;
            int pending;
            lock (this.stateLock)
            {
                s = this.state;
                pending = s.Pending.Count;
            }

            var pendingText = pending > 0 ? " " + pending.ToString(CultureInfo.InvariantCulture) + " change(s) waiting to send." : string.Empty;
            if (s.LastError != null && (s.LastErrorUtc ?? DateTimeOffset.MinValue) >= (s.LastPullUtc ?? DateTimeOffset.MinValue))
            {
                options.LastSync = new StatusItem("Sync", "Last error: " + s.LastError + pendingText, ItemStatus.Failed);
            }
            else if (s.LastSummary != null)
            {
                options.LastSync = new StatusItem("Sync", s.LastSummary + pendingText, ItemStatus.Succeeded);
            }
            else
            {
                options.LastSync = new StatusItem("Sync", "Not run yet. It starts within a minute of saving a token." + pendingText, ItemStatus.None);
            }
        }

        public void OnOptionsSaved(PluginOptions options)
        {
            this.cachedMe = null;
            this.cachedMeError = null;
            if (!this.IsConfigured)
            {
                return;
            }

            this.EnsureAccount();
            if (!this.state.InitialPullDone || (!this.state.BackfillDone && options.SyncToBingers))
            {
                // First configuration: run the initial sync in the background rather than waiting for the schedule.
                this.FireAndForget(ct => this.RunScheduledSyncAsync(ct, null));
            }
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        /// <summary>Resets the state when the Bingers token or Emby user changed (a cursor belongs to one account).</summary>
        private void EnsureAccount()
        {
            var o = Options;
            var key = SyncState.MakeAccountKey(o?.AccessToken, NormalizeUserId(o?.EmbyUserId));
            lock (this.stateLock)
            {
                if (this.state.AccountKey == key)
                {
                    return;
                }

                var hadAccount = this.state.AccountKey != null;
                this.state = new SyncState { AccountKey = key };
                this.recentlyPushed.Clear();
                if (hadAccount)
                {
                    this.Info("Bingers token or Emby user changed; starting a fresh sync.");
                }
            }

            this.SaveState();
        }

        private User GetUser(PluginOptions o)
        {
            var user = string.IsNullOrWhiteSpace(o?.EmbyUserId) ? null : this.userManager.GetUserById(o.EmbyUserId);
            if (user == null)
            {
                throw new InvalidOperationException("The Emby user chosen in the Bingers settings no longer exists.");
            }

            return user;
        }

        private static bool SameUser(User user, string configuredId)
            => NormalizeUserId(user.Id.ToString("N")) == NormalizeUserId(configuredId);

        private static string NormalizeUserId(string id)
            => Guid.TryParse(id, out var g) ? g.ToString("N") : id?.Trim().ToLowerInvariant();

        private static DateTimeOffset MinNow(DateTimeOffset d)
        {
            var now = DateTimeOffset.UtcNow;
            return d > now ? now : d;
        }

        private void FireAndForget(Func<CancellationToken, Task> work)
        {
            Task.Run(async () =>
            {
                try
                {
                    await work(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    this.RecordError(ex);
                }
            });
        }

        private void RecordError(Exception ex)
        {
            lock (this.stateLock)
            {
                this.state.LastError = ex.Message;
                this.state.LastErrorUtc = DateTimeOffset.UtcNow;
            }

            if (ex is BingersAuthException || ex is BingersRateLimitedException)
            {
                this.Warn(ex.Message);
            }
            else
            {
                this.logger.ErrorException("Bingers sync failed", ex);
            }

            try { this.SaveState(); } catch (IOException) { }
        }

        private void SaveState()
        {
            lock (this.stateLock)
            {
                try
                {
                    this.state.Save(this.statePath);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    this.logger.ErrorException("Bingers: could not save sync state", ex);
                }
            }
        }

        // Emby's logger treats the message as a format string; always pass text as an argument.
        private void Info(string message) => this.logger.Info("{0}", message);

        private void Warn(string message) => this.logger.Warn("{0}", message);

        private void Debug(string message) => this.logger.Debug("{0}", message);
    }

    /// <summary>Per-run lookup of Emby movies and episodes by external ids, with caching.</summary>
    internal sealed class EmbyIndex
    {
        private readonly ILibraryManager library;
        private readonly User user;
        private readonly Dictionary<string, List<BaseItem>> movies = new Dictionary<string, List<BaseItem>>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<long>> seriesByShowKey = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        private readonly Dictionary<long, Dictionary<(int, int), List<BaseItem>>> episodesBySeries = new Dictionary<long, Dictionary<(int, int), List<BaseItem>>>();

        public EmbyIndex(ILibraryManager library, User user)
        {
            this.library = library;
            this.user = user;
        }

        public List<BaseItem> Find(List<string> keys)
        {
            var found = new Dictionary<long, BaseItem>();
            foreach (var key in keys)
            {
                var parts = key.Split(':');
                if (parts.Length == 3 && parts[0] == "m")
                {
                    foreach (var m in this.Movies(parts[1], parts[2]))
                    {
                        found[m.InternalId] = m;
                    }
                }
                else if (parts.Length == 5 && parts[0] == "e"
                    && int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var season)
                    && int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                {
                    foreach (var seriesId in this.Series(parts[1], parts[2]))
                    {
                        if (this.Episodes(seriesId).TryGetValue((season, number), out var eps))
                        {
                            foreach (var e in eps)
                            {
                                found[e.InternalId] = e;
                            }
                        }
                    }
                }

                if (found.Count > 0)
                {
                    break; // The first id that matches is enough; the others name the same title.
                }
            }

            return found.Values.ToList();
        }

        private List<BaseItem> Movies(string provider, string id)
        {
            var cacheKey = provider + ":" + id;
            if (!this.movies.TryGetValue(cacheKey, out var list))
            {
                list = this.library.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Movie" },
                    Recursive = true,
                    IsVirtualItem = false,
                    AnyProviderIdEquals = ProviderPairs(provider, id),
                }).ToList();
                this.movies[cacheKey] = list;
            }

            return list;
        }

        private List<long> Series(string provider, string id)
        {
            var cacheKey = provider + ":" + id;
            if (!this.seriesByShowKey.TryGetValue(cacheKey, out var list))
            {
                list = this.library.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Series" },
                    Recursive = true,
                    AnyProviderIdEquals = ProviderPairs(provider, id),
                }).Select(s => s.InternalId).ToList();
                this.seriesByShowKey[cacheKey] = list;
            }

            return list;
        }

        private Dictionary<(int, int), List<BaseItem>> Episodes(long seriesId)
        {
            if (!this.episodesBySeries.TryGetValue(seriesId, out var map))
            {
                map = new Dictionary<(int, int), List<BaseItem>>();
                var eps = this.library.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Episode" },
                    Recursive = true,
                    IsVirtualItem = false,
                    SeriesIds = new[] { seriesId },
                });
                foreach (var ep in eps.OfType<Episode>())
                {
                    if (!ep.ParentIndexNumber.HasValue || !ep.IndexNumber.HasValue)
                    {
                        continue;
                    }

                    var last = ep.IndexNumberEnd.HasValue && ep.IndexNumberEnd.Value >= ep.IndexNumber.Value ? ep.IndexNumberEnd.Value : ep.IndexNumber.Value;
                    for (var n = ep.IndexNumber.Value; n <= last && n - ep.IndexNumber.Value < 10; n++)
                    {
                        var k = (ep.ParentIndexNumber.Value, n);
                        if (!map.TryGetValue(k, out var l))
                        {
                            map[k] = l = new List<BaseItem>();
                        }

                        l.Add(ep);
                    }
                }

                this.episodesBySeries[seriesId] = map;
            }

            return map;
        }

        private static List<KeyValuePair<string, string>> ProviderPairs(string provider, string id)
        {
            var name = provider == "imdb" ? MetadataProviders.Imdb.ToString()
                : provider == "tmdb" ? MetadataProviders.Tmdb.ToString()
                : MetadataProviders.Tvdb.ToString();

            return new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>(name, id) };
        }
    }
}
