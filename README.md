# Bingers for Emby

An Emby Server plugin that keeps watched movies and episodes in sync, both ways, between one
Emby user and a Bingers account (https://bingers.app).

- **Emby → Bingers, live.** Finishing a movie or episode, or marking it played, is sent to
  Bingers within about 10 seconds, batched.
- **Bingers → Emby, every 15 minutes.** Anything watched on Bingers (the app, Plex, other
  tools) is marked played in Emby, read from Bingers' change feed so each run only fetches
  what changed.
- **One-time backfill.** On first setup, everything already played in Emby is sent to
  Bingers with its original date. Items Bingers already has are skipped.

Requirements: Emby Server 4.9 or newer, and a Bingers **Legend or Icon** plan (the API comes
with those plans).

## Build

You need the .NET 8 SDK (https://dotnet.microsoft.com/download).

```
dotnet build Emby.Bingers.csproj -c Release
```

The plugin is `bin/Release/netstandard2.0/Emby.Bingers.dll`. That single file is all Emby needs.

**No .NET install?** Push this folder to a GitHub repository. The included workflow
(`.github/workflows/build.yml`) builds the DLL, runs the tests, and attaches
`Emby.Bingers.dll` as a downloadable artifact on the run's page under the **Actions** tab.
Pushing a tag like `v0.1.0` also publishes a GitHub release with the DLL attached, versioned from the tag.

If the build complains about the Emby SDK, or the plugin doesn't load, change
`MediaBrowser.Server.Core`'s version in `Emby.Bingers.csproj` to your server's version
(Dashboard → About) and build again.

## Install

1. Copy `Emby.Bingers.dll` into Emby's `plugins` folder:
   - Windows: `%AppData%\Emby-Server\programdata\plugins\`
   - Linux: `/var/lib/emby/plugins/`
   - Docker: `/config/plugins/`
   - Synology: `/var/packages/EmbyServer/var/plugins/`
2. Restart Emby Server.
3. Dashboard → **Advanced** → **Bingers** in the left menu (or Plugins → Bingers → Settings).

## Configure

1. At https://bingers.app/integrations, create a personal token with **read + write**
   permission.
2. In the plugin settings, paste the token and choose the Emby user to sync.
3. Save. The status line at the top shows **Connected as …**. The first sync (pull, then
   backfill) starts in the background straight away.

| Setting | Default | What it does |
| --- | --- | --- |
| Send Emby watches to Bingers | on | Live push of finished playback and items marked played. |
| Bring Bingers watches into Emby | on | The 15-minute pull marks Bingers watches played in Emby. |
| Send 'mark unplayed' to Bingers | **off** | Unmarking in Emby deletes that watch from your Bingers history. |
| Apply Bingers unwatches in Emby | **off** | A watch removed on Bingers marks the item unplayed in Emby. |

The unwatch options are off by default because they delete history. A stray click in Emby
would otherwise erase a Bingers watch.

**Scheduled tasks** (Dashboard → Scheduled Tasks → Bingers):
- **Sync with Bingers**: every 15 minutes and at start-up. You can change the interval there.
- **Full resync with Bingers**: manual. Re-reads your whole Bingers history and re-checks
  every played item in Emby. Safe to run. Use it if things look out of step.

## How matching works

- **Movies** match by IMDb id, then TMDB, then TVDB.
- **Episodes** match by the show's TVDB, TMDB or IMDb id plus season and episode number.
  If Bingers can't match those numbers (for example, the Emby library uses DVD order), the
  plugin retries with the episode's own TVDB id.
- Items without any of these ids (home videos, unidentified files) are skipped. Use
  **Identify** in Emby to fix them.
- Multi-episode files (S01E01-E02) count as each episode.

## How it avoids duplicates

- **Pulled watches don't bounce back.** Watches pulled from Bingers are written to Emby with
  Emby's "import" reason, which the plugin never sends back.
- **No accidental rewatches on Bingers.** Bingers counts a repeat more than 12 hours after
  the last watch as a rewatch. So a manual mark in Emby, or a backfill item, is only sent if
  Bingers doesn't already have it. Playing something to the end is a real rewatch and is
  always sent.
- **Unwatches cancel queued watches.** Marking something unplayed in Emby before a queued
  watch went out cancels the queued watch.

## Limits and good behaviour

- Requests are paced under Bingers' limits (60 per minute, 1,000 per hour per user, shared
  across all your tokens). Responses with `429 Retry-After` are honoured.
- The full history download (a feed read without a cursor) happens once. Every later read
  uses the stored cursor.
- Changes that can't be sent (Bingers down, rate limited) stay queued and are retried by the
  next sync, up to 6 times.

The plugin keeps its state (feed cursor, what Bingers has, queued changes) in
`plugins/configurations/Emby.Bingers.state.json`. Deleting that file is equivalent to a full
resync. Changing the token or the Emby user also starts a fresh sync automatically.

## Data and privacy

- The plugin talks only to Bingers' official API (`https://api.bingers.app/v1`) with the token
  you create. It doesn't scrape any website and sends nothing anywhere else.
- What it sends: the ids (IMDb/TMDB/TVDB), season and episode numbers, and watch times of the
  movies and episodes the chosen Emby user watches. No file paths, user names or other library
  data leave the server.
- The token is stored in Emby's plugin configuration on your server.

## Not synced (yet)

- **Ratings.** Emby's likes and ratings don't map cleanly to Bingers' 1–5 stars.
- **Library membership** (follows, favorites, watch-later).
- **More than one Emby user.** The plugin syncs one Emby user to one Bingers account.

## Troubleshooting

- **Status says "read-only".** Create a new token with read + write.
- **"403 … plan".** The Bingers account needs the Legend or Icon plan for API access.
- **An item never syncs.** Check its ids in Emby (Edit metadata → External ids). The Emby log
  (`embyserver.txt`, search for "Bingers") names every item Bingers couldn't match.

## Tests

`tests/` holds an end-to-end test. It compiles the plugin's source against in-memory Emby
stand-ins and runs it against a fake Bingers API that follows the published spec, covering
first setup, backfill, live pushes, Bingers-side changes, restarts, rate limiting and a bad
token.

```
cd tests
python3 fake_bingers.py 8765 &
dotnet run
```
