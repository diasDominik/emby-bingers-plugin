using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.Bingers.Api
{
    /// <summary>The token is missing, invalid or lacks a scope (401/403). Retrying will not help.</summary>
    public sealed class BingersAuthException : Exception
    {
        public BingersAuthException(string message) : base(message) { }
    }

    /// <summary>Bingers asked us to wait longer than we are willing to block for.</summary>
    public sealed class BingersRateLimitedException : Exception
    {
        public BingersRateLimitedException(TimeSpan retryAfter)
            : base("Bingers rate limit reached; retry after " + retryAfter.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + "s.")
        {
            this.RetryAfter = retryAfter;
        }

        public TimeSpan RetryAfter { get; }
    }

    public sealed class BingersApiException : Exception
    {
        public BingersApiException(int status, string code, string message)
            : base("Bingers API " + status.ToString(CultureInfo.InvariantCulture) + " " + code + ": " + message)
        {
            this.Status = status;
            this.Code = code;
        }

        public int Status { get; }

        public string Code { get; }
    }

    public sealed class FeedPage
    {
        public List<Dictionary<string, object>> Items { get; set; } = new List<Dictionary<string, object>>();

        public string NextCursor { get; set; }

        public bool HasMore { get; set; }
    }

    /// <summary>
    /// Thin client for https://api.bingers.app/v1. All calls share one pacing gate so the
    /// plugin as a whole stays under 60 requests/minute and 1,000/hour for the user.
    /// </summary>
    public sealed class BingersClient
    {
        public const int MaxBatch = 100;

        /// <summary>API root. Only tests change it.</summary>
        internal static string BaseUrl = "https://api.bingers.app/v1";

        private static readonly HttpClient Http = CreateHttpClient();
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        private static readonly Queue<DateTime> HourWindow = new Queue<DateTime>();
        private static DateTime lastRequestUtc = DateTime.MinValue;

        // ~54 per minute and 950 per hour: a margin under the documented limits, which also
        // count requests the user makes from other tools with other tokens.
        internal static TimeSpan MinSpacing = TimeSpan.FromMilliseconds(1100);
        private const int HourlyBudget = 950;
        private static readonly TimeSpan MaxBlockingWait = TimeSpan.FromMinutes(5);

        private readonly Func<string> tokenProvider;
        private readonly Action<string> logDebug;

        public BingersClient(Func<string> tokenProvider, Action<string> logDebug = null)
        {
            this.tokenProvider = tokenProvider;
            this.logDebug = logDebug ?? (_ => { });
        }

        public bool HasToken => !string.IsNullOrWhiteSpace(this.tokenProvider());

        // ---------------- Endpoints ----------------

        public async Task<Dictionary<string, object>> GetMeAsync(CancellationToken ct)
            => await this.SendAsync(HttpMethod.Get, "/me", null, ct).ConfigureAwait(false) as Dictionary<string, object>;

        /// <summary>POST /scrobble. Returns one result per item, in order.</summary>
        public async Task<List<Dictionary<string, object>>> ScrobbleAsync(IList<Dictionary<string, object>> items, bool dryRun, CancellationToken ct)
        {
            var body = new Dictionary<string, object> { ["items"] = items };
            if (dryRun)
            {
                body["dry_run"] = true;
            }

            var res = await this.SendAsync(HttpMethod.Post, "/scrobble", body, ct).ConfigureAwait(false);
            return Results(res);
        }

        /// <summary>POST /library/actions. Returns one result per action, in order.</summary>
        public async Task<List<Dictionary<string, object>>> LibraryActionsAsync(IList<Dictionary<string, object>> actions, CancellationToken ct)
        {
            var body = new Dictionary<string, object> { ["actions"] = actions };
            var res = await this.SendAsync(HttpMethod.Post, "/library/actions", body, ct).ConfigureAwait(false);
            return Results(res);
        }

        /// <summary>GET /entries: the watch-history change feed.</summary>
        public async Task<FeedPage> GetEntriesAsync(string cursor, int limit, CancellationToken ct)
        {
            var path = "/entries?limit=" + limit.ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(cursor))
            {
                path += "&cursor=" + Uri.EscapeDataString(cursor);
            }

            var res = await this.SendAsync(HttpMethod.Get, path, null, ct).ConfigureAwait(false);
            return new FeedPage
            {
                Items = (Json.Arr(res, "items") ?? new List<object>()).OfType<Dictionary<string, object>>().ToList(),
                NextCursor = Json.Str(res, "next_cursor"),
                HasMore = Json.Bool(res, "has_more"),
            };
        }

        /// <summary>GET /titles?ids=…: catalog facts (incl. external ids) for up to 100 titles.</summary>
        public async Task<List<Dictionary<string, object>>> GetTitlesAsync(IEnumerable<string> refs, CancellationToken ct)
        {
            var ids = string.Join(",", refs.Select(Uri.EscapeDataString));
            var res = await this.SendAsync(HttpMethod.Get, "/titles?ids=" + ids, null, ct).ConfigureAwait(false);
            return (Json.Arr(res, "items") ?? new List<object>()).OfType<Dictionary<string, object>>().ToList();
        }

        /// <summary>
        /// GET /titles/{ref}/episodes. The spec gives no response schema, so this accepts
        /// a bare array or an object holding it under "items"/"episodes", and season or
        /// episode lists nested under "seasons".
        /// </summary>
        public async Task<List<Dictionary<string, object>>> GetEpisodesAsync(string titleRef, CancellationToken ct)
        {
            var res = await this.SendAsync(HttpMethod.Get, "/titles/" + Uri.EscapeDataString(titleRef) + "/episodes", null, ct).ConfigureAwait(false);
            var flat = new List<Dictionary<string, object>>();
            CollectEpisodes(res, null, flat, 0);
            return flat;
        }

        // ---------------- Plumbing ----------------

        private static List<Dictionary<string, object>> Results(object res)
            => (Json.Arr(res, "results") ?? new List<object>()).OfType<Dictionary<string, object>>().ToList();

        private static void CollectEpisodes(object node, int? seasonHint, List<Dictionary<string, object>> into, int depth)
        {
            if (depth > 4 || node == null)
            {
                return;
            }

            if (node is List<object> list)
            {
                foreach (var child in list)
                {
                    CollectEpisodes(child, seasonHint, into, depth + 1);
                }

                return;
            }

            if (!(node is Dictionary<string, object> obj))
            {
                return;
            }

            // A season wrapper: { season|number: 2, episodes: [...] }
            var nested = Json.Arr(obj, "episodes") ?? Json.Arr(obj, "items") ?? Json.Arr(obj, "seasons");
            if (nested != null)
            {
                var season = Json.Int(obj, "season") ?? Json.Int(obj, "season_number") ?? (Json.Arr(obj, "episodes") != null ? Json.Int(obj, "number") : null) ?? seasonHint;
                CollectEpisodes(nested, season, into, depth + 1);
                return;
            }

            var number = Json.Int(obj, "number") ?? Json.Int(obj, "episode") ?? Json.Int(obj, "episode_number");
            if (number.HasValue && Json.Str(obj, "id") != null)
            {
                if (!obj.ContainsKey("season") && seasonHint.HasValue)
                {
                    obj["season"] = (double)seasonHint.Value;
                }

                into.Add(obj);
            }
        }

        private static HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Emby.Bingers/" + typeof(BingersClient).Assembly.GetName().Version);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return client;
        }

        private async Task<object> SendAsync(HttpMethod method, string path, object body, CancellationToken ct)
        {
            var token = this.tokenProvider()?.Trim();
            if (string.IsNullOrEmpty(token))
            {
                throw new BingersAuthException("No Bingers access token is configured.");
            }

            var payload = body == null ? null : Json.Serialize(body);
            var transientAttempts = 0;

            while (true)
            {
                await WaitForTurnAsync(ct).ConfigureAwait(false);

                HttpResponseMessage response;
                string text;
                try
                {
                    using (var request = new HttpRequestMessage(method, BaseUrl + path))
                    {
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                        if (payload != null)
                        {
                            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                        }

                        response = await Http.SendAsync(request, ct).ConfigureAwait(false);
                        text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && (ex is HttpRequestException || ex is TaskCanceledException))
                {
                    if (++transientAttempts > 3)
                    {
                        throw;
                    }

                    this.logDebug("Bingers " + method + " " + path + " failed (" + ex.Message + "), retrying.");
                    await Task.Delay(TimeSpan.FromSeconds(5 * transientAttempts), ct).ConfigureAwait(false);
                    continue;
                }

                var status = (int)response.StatusCode;
                using (response)
                {
                    if (status >= 200 && status < 300)
                    {
                        return string.IsNullOrWhiteSpace(text) ? null : Json.Parse(text);
                    }

                    object parsed = null;
                    try { parsed = string.IsNullOrWhiteSpace(text) ? null : Json.Parse(text); } catch (FormatException) { }
                    var err = Json.Obj(parsed, "error");
                    var code = Json.Str(err, "code") ?? response.ReasonPhrase;
                    var message = Json.Str(err, "message") ?? text;

                    if (status == 401 || status == 403)
                    {
                        throw new BingersAuthException(status == 403 && code == "insufficient_scope"
                            ? "The Bingers token is read-only. Create a 'read + write' token at bingers.app/integrations."
                            : status == 403
                                ? "Bingers refused the token (403 " + code + "): " + message + " The API needs the Legend or Icon plan."
                                : "Bingers rejected the token (401): " + message);
                    }

                    if (status == 429)
                    {
                        var wait = RetryAfter(response, err);
                        if (wait > MaxBlockingWait)
                        {
                            throw new BingersRateLimitedException(wait);
                        }

                        this.logDebug("Bingers rate limit hit; waiting " + wait.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + "s.");
                        await Task.Delay(wait, ct).ConfigureAwait(false);
                        continue;
                    }

                    if (status >= 500 && ++transientAttempts <= 3)
                    {
                        this.logDebug("Bingers " + status.ToString(CultureInfo.InvariantCulture) + " on " + path + ", retrying.");
                        await Task.Delay(TimeSpan.FromSeconds(5 * transientAttempts), ct).ConfigureAwait(false);
                        continue;
                    }

                    throw new BingersApiException(status, code, message);
                }
            }
        }

        private static TimeSpan RetryAfter(HttpResponseMessage response, Dictionary<string, object> err)
        {
            var ra = response.Headers.RetryAfter;
            if (ra?.Delta != null)
            {
                return ra.Delta.Value + TimeSpan.FromSeconds(1);
            }

            if (ra?.Date != null)
            {
                var d = ra.Date.Value - DateTimeOffset.UtcNow;
                return d > TimeSpan.Zero ? d + TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(5);
            }

            var seconds = Json.Int(err, "retryAfterSeconds");
            return TimeSpan.FromSeconds(seconds.HasValue && seconds.Value > 0 ? seconds.Value + 1 : 60);
        }

        private static async Task WaitForTurnAsync(CancellationToken ct)
        {
            await Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var now = DateTime.UtcNow;
                while (HourWindow.Count > 0 && now - HourWindow.Peek() > TimeSpan.FromHours(1))
                {
                    HourWindow.Dequeue();
                }

                if (HourWindow.Count >= HourlyBudget)
                {
                    var wait = HourWindow.Peek() + TimeSpan.FromHours(1) - now;
                    if (wait > MaxBlockingWait)
                    {
                        throw new BingersRateLimitedException(wait);
                    }

                    await Task.Delay(wait, ct).ConfigureAwait(false);
                    HourWindow.Dequeue();
                }

                var sinceLast = DateTime.UtcNow - lastRequestUtc;
                if (sinceLast < MinSpacing)
                {
                    await Task.Delay(MinSpacing - sinceLast, ct).ConfigureAwait(false);
                }

                lastRequestUtc = DateTime.UtcNow;
                HourWindow.Enqueue(lastRequestUtc);
            }
            finally
            {
                Gate.Release();
            }
        }
    }
}
