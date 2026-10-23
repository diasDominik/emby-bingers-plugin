using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emby.Bingers;
using Emby.Bingers.Api;
using Emby.Bingers.Sync;
using Emby.Bingers.Tasks;
using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

internal static class Program
{
    private const string Server = "http://127.0.0.1:8765";
    private static readonly HttpClient Http = new HttpClient();
    private static int failures;

    private static async Task<int> Main()
    {
        BingersClient.BaseUrl = Server + "/v1";
        BingersClient.MinSpacing = TimeSpan.FromMilliseconds(10);
        await Http.GetStringAsync(Server + "/__reset");

        var root = Path.Combine(Path.GetTempPath(), "bingers-test-" + Guid.NewGuid().ToString("N"));
        var paths = new FakePaths(root);
        var log = new ConsoleLogger();
        var emby = new FakeEmby();

        // ---- Emby library ----
        var alice = new User { Name = "Alice" };
        var bob = new User { Name = "Bob" };
        emby.UserList.AddRange(new[] { alice, bob });

        var bb = Series("Breaking Bad", ("Tvdb", "81189"));
        var dark = Series("Dark", ("Tmdb", "70523")); // no TVDB id on purpose: exercises tmdb:show:
        var bb11 = Ep(bb, 1, 1, "349232");
        var bb12 = Ep(bb, 1, 2, "349235");
        var bbOdd = Ep(bb, 9, 9, "349236");        // Emby numbers 1x3 differently (DVD order): needs the tvdb_episode fallback
        var bb21 = Ep(bb, 2, 1, null);
        var dark11 = Ep(dark, 1, 1, null);
        var dark12 = Ep(dark, 1, 2, null);
        var inception = Movie("Inception", ("Imdb", "tt1375666"));
        var heat = Movie("Heat", ("Tmdb", "949"));
        var noIds = Movie("Home Video");
        var unknown = Movie("Not In Catalog", ("Imdb", "tt9999999"));

        // Alice's existing Emby history (before installing the plugin).
        foreach (var i in new BaseItem[] { bb11, bb12, bbOdd, heat, noIds, unknown })
        {
            Seed(alice, i, DateTimeOffset.UtcNow.AddDays(-100));
        }

        Seed(bob, dark11, DateTimeOffset.UtcNow.AddDays(-5));

        // ---- Install the plugin ----
        var plugin = new Plugin(null, log, emby);
        var entry = new EntryPoint(emby, emby, emby, paths, log);
        entry.Run();

        Step("Settings page before configuring");
        var page = plugin.ShowUI();
        Check(page.UserChoices.Count == 2, "user dropdown lists both Emby users");
        Check(page.Connection.Status == ItemStatus.Unavailable, "connection says 'not configured': " + page.Connection);
        Check(new PluginOptions { AccessToken = "abc" }.RunValidation().Count == 2, "validation rejects a non-bng_pat_ token and a missing user");

        Step("Save token + user (kicks off the initial sync in the background)");
        var opts = new PluginOptions { AccessToken = "bng_pat_test", EmbyUserId = alice.Id.ToString("N") };
        Check(opts.RunValidation().Count == 0, "valid options pass validation");
        plugin.Save(opts);
        await WaitUntil(() => File.Exists(StatePath(paths)) && File.ReadAllText(StatePath(paths)).Contains("\"backfill_done\":true"), "initial sync to finish");

        var srv = await Log();
        var scrobbles = srv.Where(e => e["op"] == "scrobble").ToList();
        Console.WriteLine("    Bingers received: " + string.Join(" | ", scrobbles.Select(Describe)));
        Check(Played(emby, alice, inception), "Inception (watched on Bingers) is now played in Emby");
        Check(emby.Saves.Any(s => s.item == "Inception" && s.reason == UserDataSaveReason.Import), "…written with the Import reason");
        Check(!scrobbles.Any(s => Has(s, "title", "imdb:tt1375666")), "Inception was not echoed back to Bingers");
        Check(!scrobbles.Any(s => Has(s, "title", "tvdb:81189") && Has(s, "season", "1") && Has(s, "episode", "1")), "BB 1x1 (already on Bingers) not re-sent");
        Check(scrobbles.Any(s => Has(s, "title", "tvdb:81189") && Has(s, "season", "1") && Has(s, "episode", "2") && s.ContainsKey("watched_at")), "backfill sent BB 1x2 with its Emby date");
        Check(scrobbles.Any(s => Has(s, "title", "tmdb:movie:949")), "backfill sent Heat by TMDB id");
        Check(scrobbles.Any(s => Has(s, "tvdb_episode", "349236")), "odd-numbered episode resolved via the tvdb_episode fallback");
        Check(scrobbles.Any(s => Has(s, "title", "imdb:tt9999999")), "unknown movie was tried (and reported unresolved)");
        Check(!scrobbles.Any(s => s.Values.Any(v => v.Contains("Home Video"))), "movie without ids skipped");
        Check(!Played(emby, bob, inception) && !scrobbles.Any(s => Has(s, "title", "tmdb:show:70523")), "Bob's data untouched and not synced");
        Check(await Rewatches() == 0, "no rewatches recorded on Bingers");
        Check(Pending(paths) == 0, "nothing left queued");

        page = plugin.ShowUI();
        Check(page.Connection.Status == ItemStatus.Succeeded && page.Connection.StatusText.Contains("tester"), "settings show 'Connected as tester': " + page.Connection);
        Check(page.LastSync.Status == ItemStatus.Succeeded, "settings show last sync: " + page.LastSync);

        Step("Live: Alice finishes Dark 1x1, re-marks BB 1x1, Bob watches Dark 1x2");
        var before = (await Log()).Count;
        emby.UserFinishesPlayback(alice, dark11);
        emby.UserToggles(alice, bb11, false);   // unmark (not sent: SendUnwatches is off)
        emby.UserToggles(alice, bb11, true);    // re-mark of something Bingers has: must not become a rewatch
        emby.UserFinishesPlayback(bob, dark12);
        emby.UserFinishesPlayback(alice, dark12);
        emby.UserToggles(alice, dark12, false); // stopped half-way… then unmarked: latest intent wins
        await SyncService.Instance.FlushPendingAsync(CancellationToken.None);
        var live = (await Log()).Skip(before).ToList();
        Console.WriteLine("    Bingers received: " + string.Join(" | ", live.Select(Describe)));
        Check(live.Count(s => Has(s, "title", "tmdb:show:70523") && Has(s, "episode", "1")) == 1, "Dark 1x1 scrobbled once by TMDB show id");
        Check(!live.Any(s => Has(s, "title", "tvdb:81189")), "BB 1x1 re-mark and unmark not sent");
        Check(!live.Any(s => Has(s, "title", "tmdb:show:70523") && Has(s, "episode", "2")), "Dark 1x2 not sent (Bob's play; Alice's mark withdrawn)");
        Check(await Rewatches() == 0, "still no rewatches");

        Step("Changes on Bingers: Dark 1x2 watched on the phone, Heat removed");
        plugin.Save(new PluginOptions { AccessToken = "bng_pat_test", EmbyUserId = alice.Id.ToString("N"), ApplyUnwatchesFromBingers = true });
        await Post("/__watch", "{\"title\":\"tmdb:show:70523\",\"season\":1,\"episode\":2}");
        await Post("/__unwatch", "{\"title\":\"tmdb:movie:949\"}");
        Seed(alice, bb21, DateTimeOffset.UtcNow.AddDays(-1));   // played in Emby, never synced…
        await Post("/__watch", "{\"title\":\"tvdb:81189\",\"season\":2,\"episode\":1}");
        await Post("/__unwatch", "{\"title\":\"tvdb:81189\",\"season\":2,\"episode\":1}"); // …then added and removed on Bingers between pulls
        before = (await Log()).Count;
        await new SyncWithBingersTask().Execute(CancellationToken.None, new Progress<double>());
        Check(Played(emby, alice, dark12), "Dark 1x2 marked played in Emby");
        Check(!Played(emby, alice, heat), "Heat marked unplayed in Emby (apply-unwatches on)");
        Check(!Played(emby, alice, bb21), "BB 2x1 unplayed too, though the plugin never saw it watched");
        Check((await Log()).Count == before, "and none of that was echoed back to Bingers");

        Step("Send unwatches on: Alice unmarks BB 1x2");
        plugin.Save(new PluginOptions { AccessToken = "bng_pat_test", EmbyUserId = alice.Id.ToString("N"), SendUnwatchesToBingers = true });
        before = (await Log()).Count;
        emby.UserToggles(alice, bb12, false);
        await SyncService.Instance.FlushPendingAsync(CancellationToken.None);
        var un = (await Log()).Skip(before).ToList();
        Console.WriteLine("    Bingers received: " + string.Join(" | ", un.Select(Describe)));
        Check(un.Count == 1 && Has(un[0], "action", "unwatch") && Has(un[0], "season", "1") && Has(un[0], "episode", "2"), "unwatch BB 1x2 sent via /library/actions");

        Step("Rate limited: two 429s with Retry-After: 1, then success");
        await Http.GetStringAsync(Server + "/__ratelimit?n=2");
        before = (await Log()).Count;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        emby.UserFinishesPlayback(alice, inception);
        await SyncService.Instance.FlushPendingAsync(CancellationToken.None);
        sw.Stop();
        var rl = (await Log()).Skip(before).ToList();
        Check(rl.Count == 1 && Has(rl[0], "title", "imdb:tt1375666"), "Inception rewatch delivered after the waits");
        Check(sw.Elapsed >= TimeSpan.FromSeconds(2), "honoured Retry-After twice (" + sw.Elapsed.TotalSeconds.ToString("0.0") + "s)");
        Check(await Rewatches() == 1, "a real replay to the end counts as exactly one rewatch");

        Step("Emby restart: state reloads, no duplicate pushes, no extra full loads");
        entry.Dispose();
        entry = new EntryPoint(emby, emby, emby, paths, log);
        entry.Run();
        before = (await Log()).Count;
        await new SyncWithBingersTask().Execute(CancellationToken.None, new Progress<double>());
        Check((await Log()).Count == before, "a routine sync after restart sends nothing");
        Check(await Cursorless() == 1, "only one cursor-less /entries call so far (budget is 20/day)");

        Step("Full resync task");
        before = (await Log()).Count;
        await new FullResyncTask().Execute(CancellationToken.None, new Progress<double>());
        var resync = (await Log()).Skip(before).ToList();
        Console.WriteLine("    Bingers received: " + string.Join(" | ", resync.Select(Describe)));
        Check(await Rewatches() == 1, "full resync records no further rewatches");
        Check(!resync.Any(s => Has(s, "title", "imdb:tt1375666") || Has(s, "title", "tmdb:show:70523")), "full resync skips what Bingers already has");

        Step("Bad token");
        plugin.Save(new PluginOptions { AccessToken = "bng_pat_wrong", EmbyUserId = alice.Id.ToString("N") });
        await Task.Delay(500);
        page = plugin.ShowUI();
        Check(page.Connection.Status == ItemStatus.Failed, "settings show the token error: " + page.Connection);
        try
        {
            await new SyncWithBingersTask().Execute(CancellationToken.None, new Progress<double>());
            Check(false, "task should fail with a bad token");
        }
        catch (BingersAuthException ex)
        {
            Check(ex.Message.Contains("401"), "task fails clearly: " + ex.Message);
        }

        page = plugin.ShowUI();
        Check(page.LastSync.Status == ItemStatus.Failed, "settings show the last error: " + page.LastSync);

        entry.Dispose();
        Directory.Delete(root, true);
        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : failures + " CHECK(S) FAILED");
        return failures == 0 ? 0 : 1;

        // ---------- helpers ----------
        Series Series(string name, params (string p, string v)[] ids)
        {
            var s = new Series { Name = name };
            foreach (var (p, v) in ids) s.ProviderIds[p] = v;
            emby.Items.Add(s);
            return s;
        }

        Episode Ep(Series s, int season, int number, string tvdbEp)
        {
            var e = new Episode { Name = s.Name + " " + season + "x" + number, SeriesRef = s, SeriesName = s.Name, SeriesId = s.InternalId, ParentIndexNumber = season, IndexNumber = number };
            if (tvdbEp != null) e.ProviderIds["Tvdb"] = tvdbEp;
            emby.Items.Add(e);
            return e;
        }

        Movie Movie(string name, params (string p, string v)[] ids)
        {
            var m = new Movie { Name = name };
            foreach (var (p, v) in ids) m.ProviderIds[p] = v;
            emby.Items.Add(m);
            return m;
        }

        void Seed(User u, BaseItem i, DateTimeOffset at)
            => emby.Data[(u.InternalId, i.InternalId)] = new UserItemData { Played = true, PlayCount = 1, LastPlayedDate = at };
    }

    private static string StatePath(FakePaths p) => Path.Combine(p.PluginConfigurationsPath, "Emby.Bingers.state.json");

    private static int Pending(FakePaths p) => Json.Arr(Json.Parse(File.ReadAllText(StatePath(p))), "pending").Count;

    private static bool Played(FakeEmby e, User u, BaseItem i) => e.GetUserData(u, i).Played;

    private static bool Has(Dictionary<string, string> d, string k, string v) => d.TryGetValue(k, out var x) && x == v;

    private static string Describe(Dictionary<string, string> d) => string.Join(",", d.Where(kv => kv.Key != "op").Select(kv => kv.Key + "=" + kv.Value));

    private static async Task<List<Dictionary<string, string>>> Log()
    {
        var root = Json.Parse(await Http.GetStringAsync(Server + "/__log"));
        return Json.Arr(root, "log").OfType<Dictionary<string, object>>()
            .Select(d => d.ToDictionary(kv => kv.Key, kv => kv.Value is double n ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : Convert.ToString(kv.Value)))
            .ToList();
    }

    private static async Task<int> Rewatches() => Json.Int(Json.Parse(await Http.GetStringAsync(Server + "/__log")), "rewatches") ?? -1;

    private static async Task<int> Cursorless() => Json.Int(Json.Parse(await Http.GetStringAsync(Server + "/__log")), "cursorless") ?? -1;

    private static Task Post(string path, string json) => Http.PostAsync(Server + path, new StringContent(json, Encoding.UTF8, "application/json"));

    private static async Task WaitUntil(Func<bool> cond, string what)
    {
        for (var i = 0; i < 200 && !cond(); i++) await Task.Delay(100);
        Check(cond(), "waited for " + what);
    }

    private static void Step(string name)
    {
        Console.WriteLine();
        Console.WriteLine("== " + name);
    }

    private static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what);
        if (!ok) failures++;
    }
}
