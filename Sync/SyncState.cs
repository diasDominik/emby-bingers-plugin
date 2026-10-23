using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Emby.Bingers.Api;

namespace Emby.Bingers.Sync
{
    /// <summary>A watch or unwatch that still has to reach Bingers.</summary>
    public sealed class PendingPush
    {
        public long ItemId { get; set; }

        public bool Watched { get; set; }

        /// <summary>True when Emby played it to the end (a real rewatch), false for a manual mark.</summary>
        public bool FromPlayback { get; set; }

        public DateTimeOffset? WatchedAt { get; set; }

        public int Attempts { get; set; }
    }

    /// <summary>
    /// Everything the plugin remembers between runs, stored as JSON next to the plugin's
    /// configuration. Reset automatically when the token (Bingers account) or Emby user changes.
    /// </summary>
    public sealed class SyncState
    {
        public string AccountKey { get; set; }

        public string EntriesCursor { get; set; }

        public bool InitialPullDone { get; set; }

        public bool BackfillDone { get; set; }

        /// <summary>Bingers entity id (episode id or movie title id) → match keys of what is watched there.</summary>
        public Dictionary<string, List<string>> BingersWatched { get; set; } = new Dictionary<string, List<string>>();

        public List<PendingPush> Pending { get; set; } = new List<PendingPush>();

        public DateTimeOffset? LastPullUtc { get; set; }

        public DateTimeOffset? LastPushUtc { get; set; }

        public string LastSummary { get; set; }

        public string LastError { get; set; }

        public DateTimeOffset? LastErrorUtc { get; set; }

        public static string MakeAccountKey(string token, string embyUserId)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes((token ?? string.Empty).Trim() + "|" + (embyUserId ?? string.Empty)));
                return string.Concat(hash.Take(12).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
            }
        }

        public HashSet<string> WatchedKeySet()
            => new HashSet<string>(this.BingersWatched.Values.SelectMany(v => v), StringComparer.Ordinal);

        // ---------------- Persistence ----------------

        public static SyncState Load(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return new SyncState();
                }

                var root = Json.Parse(File.ReadAllText(path, Encoding.UTF8));
                var state = new SyncState
                {
                    AccountKey = Json.Str(root, "account_key"),
                    EntriesCursor = Json.Str(root, "entries_cursor"),
                    InitialPullDone = Json.Bool(root, "initial_pull_done"),
                    BackfillDone = Json.Bool(root, "backfill_done"),
                    LastPullUtc = Json.Date(root, "last_pull_utc"),
                    LastPushUtc = Json.Date(root, "last_push_utc"),
                    LastSummary = Json.Str(root, "last_summary"),
                    LastError = Json.Str(root, "last_error"),
                    LastErrorUtc = Json.Date(root, "last_error_utc"),
                };

                var watched = Json.Obj(root, "bingers_watched");
                if (watched != null)
                {
                    foreach (var kv in watched)
                    {
                        if (kv.Value is List<object> keys)
                        {
                            state.BingersWatched[kv.Key] = keys.OfType<string>().ToList();
                        }
                    }
                }

                foreach (var p in (Json.Arr(root, "pending") ?? new List<object>()).OfType<Dictionary<string, object>>())
                {
                    var id = Json.Str(p, "item_id");
                    if (long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var itemId))
                    {
                        state.Pending.Add(new PendingPush
                        {
                            ItemId = itemId,
                            Watched = Json.Bool(p, "watched"),
                            FromPlayback = Json.Bool(p, "from_playback"),
                            WatchedAt = Json.Date(p, "watched_at"),
                            Attempts = Json.Int(p, "attempts") ?? 0,
                        });
                    }
                }

                return state;
            }
            catch (Exception)
            {
                // A corrupt state file only costs a fresh full sync; never block the plugin over it.
                return new SyncState();
            }
        }

        public void Save(string path)
        {
            var root = new Dictionary<string, object>
            {
                ["account_key"] = this.AccountKey,
                ["entries_cursor"] = this.EntriesCursor,
                ["initial_pull_done"] = this.InitialPullDone,
                ["backfill_done"] = this.BackfillDone,
                ["last_pull_utc"] = this.LastPullUtc,
                ["last_push_utc"] = this.LastPushUtc,
                ["last_summary"] = this.LastSummary,
                ["last_error"] = this.LastError,
                ["last_error_utc"] = this.LastErrorUtc,
                ["bingers_watched"] = this.BingersWatched.ToDictionary(kv => kv.Key, kv => (object)kv.Value),
                ["pending"] = this.Pending.Select(p => (object)new Dictionary<string, object>
                {
                    // Stored as a string: JSON numbers are doubles and Emby ids are 64-bit.
                    ["item_id"] = p.ItemId.ToString(CultureInfo.InvariantCulture),
                    ["watched"] = p.Watched,
                    ["from_playback"] = p.FromPlayback,
                    ["watched_at"] = p.WatchedAt,
                    ["attempts"] = p.Attempts,
                }).ToList(),
            };

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, Json.Serialize(root), new UTF8Encoding(false));
            if (File.Exists(path))
            {
                File.Replace(tmp, path, null);
            }
            else
            {
                File.Move(tmp, path);
            }
        }
    }
}
