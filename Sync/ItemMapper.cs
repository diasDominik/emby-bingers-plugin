using System;
using System.Collections.Generic;
using System.Globalization;
using Emby.Bingers.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Emby.Bingers.Sync
{
    /// <summary>One Bingers watch target derived from an Emby item (a multi-episode file yields several).</summary>
    public sealed class BingersTarget
    {
        public string Label { get; set; }

        /// <summary>Keys that also come out of <see cref="ItemMapper.KeysForBingers"/>, to match the two sides.</summary>
        public List<string> Keys { get; set; } = new List<string>();

        /// <summary>title (+ season + episode) addressing.</summary>
        public Dictionary<string, object> Primary { get; set; }

        /// <summary>tvdb_episode addressing, tried when <see cref="Primary"/> comes back unresolved.</summary>
        public Dictionary<string, object> Fallback { get; set; }
    }

    public static class ItemMapper
    {
        public static bool IsSyncable(BaseItem item)
            => (item is Movie || item is Episode) && !item.IsVirtualItem;

        /// <summary>Bingers targets for an Emby movie or episode; empty when it has no usable ids.</summary>
        public static List<BingersTarget> TargetsFor(BaseItem item)
        {
            var targets = new List<BingersTarget>();

            if (item is Movie movie)
            {
                var ids = Ids(movie);
                var title = ids.Imdb != null ? "imdb:" + ids.Imdb
                    : ids.Tmdb != null ? "tmdb:movie:" + ids.Tmdb
                    : ids.Tvdb != null ? "tvdb:" + ids.Tvdb
                    : null;
                if (title != null)
                {
                    targets.Add(new BingersTarget
                    {
                        Label = movie.Name + (movie.ProductionYear.HasValue ? " (" + movie.ProductionYear.Value.ToString(CultureInfo.InvariantCulture) + ")" : string.Empty),
                        Keys = MovieKeys(ids.Imdb, ids.Tmdb, ids.Tvdb),
                        Primary = new Dictionary<string, object> { ["title"] = title },
                    });
                }

                return targets;
            }

            if (!(item is Episode episode) || !episode.ParentIndexNumber.HasValue || !episode.IndexNumber.HasValue)
            {
                return targets;
            }

            var series = episode.Series;
            var sids = series != null ? Ids(series) : default(ExternalIds);
            var showRef = sids.Tvdb != null ? "tvdb:" + sids.Tvdb
                : sids.Tmdb != null ? "tmdb:show:" + sids.Tmdb
                : sids.Imdb != null ? "imdb:" + sids.Imdb
                : null;
            var tvdbEpisode = Clean(episode.GetProviderId(MetadataProviders.Tvdb));

            var season = episode.ParentIndexNumber.Value;
            var first = episode.IndexNumber.Value;
            var last = episode.IndexNumberEnd.HasValue && episode.IndexNumberEnd.Value >= first && episode.IndexNumberEnd.Value - first < 10
                ? episode.IndexNumberEnd.Value
                : first;

            for (var number = first; number <= last; number++)
            {
                var target = new BingersTarget
                {
                    Label = (episode.SeriesName ?? series?.Name ?? "?") + " S" + season.ToString("00", CultureInfo.InvariantCulture) + "E" + number.ToString("00", CultureInfo.InvariantCulture),
                    Keys = EpisodeKeys(sids.Imdb, sids.Tmdb, sids.Tvdb, season, number),
                };

                if (showRef != null)
                {
                    target.Primary = new Dictionary<string, object> { ["title"] = showRef, ["season"] = season, ["episode"] = number };
                }

                // A TVDB episode id names exactly one episode, so only use it for single-episode files.
                if (tvdbEpisode != null && first == last)
                {
                    var byTvdb = new Dictionary<string, object> { ["tvdb_episode"] = tvdbEpisode };
                    if (target.Primary == null)
                    {
                        target.Primary = byTvdb;
                    }
                    else
                    {
                        target.Fallback = byTvdb;
                    }
                }

                if (target.Primary != null)
                {
                    targets.Add(target);
                }
            }

            return targets;
        }

        /// <summary>Match keys for a Bingers title, given its external ids.</summary>
        public static List<string> KeysForBingers(string kind, Dictionary<string, object> ids, int? season, int? number)
        {
            var imdb = Clean(Json.Str(ids, "imdb"));
            var tmdb = Clean(Json.Str(ids, "tmdb"));
            var tvdb = Clean(Json.Str(ids, "tvdb"));
            if (kind == "movie")
            {
                return MovieKeys(imdb, tmdb, tvdb);
            }

            return season.HasValue && number.HasValue
                ? EpisodeKeys(imdb, tmdb, tvdb, season.Value, number.Value)
                : new List<string>();
        }

        public static List<string> MovieKeys(string imdb, string tmdb, string tvdb)
        {
            var keys = new List<string>();
            if (imdb != null) keys.Add("m:imdb:" + imdb);
            if (tmdb != null) keys.Add("m:tmdb:" + tmdb);
            if (tvdb != null) keys.Add("m:tvdb:" + tvdb);
            return keys;
        }

        public static List<string> EpisodeKeys(string imdb, string tmdb, string tvdb, int season, int number)
        {
            var suffix = ":" + season.ToString(CultureInfo.InvariantCulture) + ":" + number.ToString(CultureInfo.InvariantCulture);
            var keys = new List<string>();
            if (tvdb != null) keys.Add("e:tvdb:" + tvdb + suffix);
            if (tmdb != null) keys.Add("e:tmdb:" + tmdb + suffix);
            if (imdb != null) keys.Add("e:imdb:" + imdb + suffix);
            return keys;
        }

        public struct ExternalIds
        {
            public string Imdb;
            public string Tmdb;
            public string Tvdb;
        }

        public static ExternalIds Ids(BaseItem item) => new ExternalIds
        {
            Imdb = Clean(item.GetProviderId(MetadataProviders.Imdb)),
            Tmdb = Clean(item.GetProviderId(MetadataProviders.Tmdb)),
            Tvdb = Clean(item.GetProviderId(MetadataProviders.Tvdb)),
        };

        public static string Clean(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            id = id.Trim().ToLowerInvariant();
            return id == "0" ? null : id;
        }
    }
}
