using System;
using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;
using Playnite.SDK;

namespace ImageRotater.Services
{
    // What Steam's public CDN serves for an appid, and nothing else.
    //
    // Knows URLs, asset names and their sizes. Knows nothing about Playnite,
    // about search results, or about what the plugin does with any of it -
    // SteamArtworkSource is where those meet. Split out because this is the
    // part that goes stale on Valve's schedule rather than ours: when an asset
    // name changes or a new one appears, this file is the whole diff.
    //
    // No API key, no rate limit published, no authentication. The store front
    // end fetches these same URLs.
    public static class SteamCdn
    {
        private const string Root = "https://cdn.cloudflare.steamstatic.com/steam/apps/";

        // One asset Steam publishes per game.
        public class Asset
        {
            public string FileName { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }

            // Shown on the result tile, so it says what the thing is rather
            // than repeating a file name at the user.
            public string Label { get; set; }

            public string MimeType
            {
                get
                {
                    return FileName != null && FileName.EndsWith(
                        ".png", StringComparison.OrdinalIgnoreCase)
                        ? "image/png"
                        : "image/jpeg";
                }
            }
        }

        // Portrait and near-square art, for covers.
        //
        // Sizes verified against the live CDN rather than taken from a wiki:
        // all of these return 200 with these dimensions for current titles.
        public static readonly IReadOnlyList<Asset> CoverAssets = new[]
        {
            new Asset
            {
                FileName = "library_600x900_2x.jpg",
                Width = 600,
                Height = 900,
                Label = "Library capsule"
            },
            new Asset
            {
                FileName = "header.jpg",
                Width = 460,
                Height = 215,
                Label = "Header"
            }
        };

        // Wide art, for backgrounds.
        //
        // library_hero is the good one - it is what Steam paints behind a
        // game's library page. page_bg_generated is a fallback that older or
        // smaller titles are more likely to have.
        public static readonly IReadOnlyList<Asset> BackgroundAssets = new[]
        {
            new Asset
            {
                FileName = "library_hero.jpg",
                Width = 1920,
                Height = 620,
                Label = "Library hero"
            },
            new Asset
            {
                FileName = "page_bg_generated_v6b.jpg",
                Width = 1438,
                Height = 810,
                Label = "Store page background"
            }
        };

        public static string BuildUrl(string appId, string fileName)
        {
            return Root + appId + "/" + fileName;
        }

        // Steam's trailers, which the store page plays behind a game.
        //
        // Reachable despite appdetails reporting otherwise. That endpoint still
        // lists a game's movies but returns NULL for their "mp4" and "webm"
        // keys - which reads as "Valve stopped serving these". They are still
        // on the CDN; only the JSON stopped naming them. The files are found by
        // taking the movie id out of the thumbnail URL, which appdetails does
        // still give, and building the path.
        //
        // MP4 rather than the WebM the store page prefers, because MediaElement
        // plays MP4 natively and cannot open VP9 WebM at all. Both are served.
        private const string TrailerRoot = "https://video.akamai.steamstatic.com/store_trailers/";

        private const string AppDetailsUrl =
            "https://store.steampowered.com/api/appdetails?appids=";

        public class Trailer
        {
            public string Name { get; set; }
            public string Url { get; set; }
            public string ThumbnailUrl { get; set; }

            // 480p, not the max. A background is scaled to fit and then sits
            // behind a UI, so the extra detail is invisible - and the max
            // version is five times the size: 33 MB against 6.6 MB for the
            // same trailer, measured.
            public const string PreferredFile = "movie480.mp4";

            // True for the short looping clips out of the store description,
            // false for full trailers. Worth distinguishing on the tile: a
            // trailer is a film, a clip is wallpaper.
            public bool IsLoopingClip { get; set; }
        }

        // Every video Steam has for a game: its trailers, and the short looping
        // clips embedded in the store description.
        //
        // One fetch for both. appdetails is a large document and both live in
        // it, so asking twice would double the wait for no reason.
        public static IReadOnlyList<Trailer> GetVideos(string appId)
        {
            var found = new List<Trailer>();

            if (string.IsNullOrWhiteSpace(appId))
            {
                return found;
            }

            string json;

            try
            {
                using (var client = new WebClient())
                {
                    client.Encoding = System.Text.Encoding.UTF8;
                    json = client.DownloadString(AppDetailsUrl + appId);
                }
            }
            catch (Exception ex)
            {
                LogManager.GetLogger().Warn(
                    ex, "ImageRotater: could not reach Steam for " + appId);

                return found;
            }

            // Clips first. They are the better wallpaper by some distance -
            // authored as seamless loops (the store page tags them
            // autoplay/muted/loop) and around 2.7 MB against a trailer's 12 MB.
            // A trailer has cuts, captions and an ending; a clip just moves.
            AddDescriptionClips(json, found);
            AddTrailers(json, found);

            return found;
        }

        // Short looping video embedded in the store description.
        //
        // Not in any structured field - these are <video> tags inside the
        // description HTML, so they have to be read out of it. Each carries a
        // VP9 WebM and an MP4 of the same clip; only the MP4 is any use, since
        // WPF cannot decode VP9.
        //
        // Not every game has them: verified present for Hades, Baldur's Gate 3
        // and Prodeus, absent for Halo Infinite and Spider-Man Remastered.
        private const string PosterSuffix = ".poster.avif";

        private static void AddDescriptionClips(string json, List<Trailer> found)
        {
            try
            {
                // Matched on the POSTER attribute, and the video URL derived
                // from it by swapping the extension.
                //
                // Matching the <source> tag directly looks more obvious and
                // does not work: inside this JSON the tag reads
                // type=\"video\/mp4\" - the slash is escaped too - and the URL
                // is full of \/ pairs, so the character classes an obvious
                // pattern uses exclude the very characters that are there.
                // The poster attribute is a single clean capture, and the two
                // files differ only by extension.
                var posters = Regex.Matches(json, @"poster=\\""(.*?)\\""");

                foreach (Match m in posters)
                {
                    string poster = Clean(m.Groups[1].Value);

                    if (!poster.EndsWith(PosterSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string url =
                        poster.Substring(0, poster.Length - PosterSuffix.Length) + ".mp4";

                    // The same clips appear again under about_the_game, which
                    // repeats the description almost verbatim.
                    if (found.Exists(t => t.Url == url))
                    {
                        continue;
                    }

                    found.Add(new Trailer
                    {
                        Name = "Store clip " + (found.Count + 1),
                        Url = url,

                        // Deliberately no thumbnail. The poster is AVIF, which
                        // WPF cannot decode - binding it would render a blank
                        // tile. The tile falls back to its placeholder.
                        ThumbnailUrl = null,
                        IsLoopingClip = true
                    });
                }
            }
            catch (Exception ex)
            {
                LogManager.GetLogger().Warn(
                    ex, "ImageRotater: could not read Steam's store clips");
            }
        }

        private static void AddTrailers(string json, List<Trailer> found)
        {
            try
            {
                // Anchored on "id", which is the first key of each movie entry,
                // rather than searching for a name near a thumbnail. The game's
                // own description contains both words thousands of characters
                // apart, and a loose pattern happily matched across all of it -
                // producing a "trailer" whose name was the entire store page.
                var entries = Regex.Matches(
                    json,
                    @"\{""id""\s*:\s*(\d+)\s*,\s*""name""\s*:\s*""(.*?)(?<!\\)""\s*,\s*""thumbnail""\s*:\s*""(.*?)(?<!\\)""");

                foreach (Match m in entries)
                {
                    string movieId = m.Groups[1].Value;

                    found.Add(new Trailer
                    {
                        Name = Regex.Unescape(m.Groups[2].Value),
                        ThumbnailUrl = Clean(m.Groups[3].Value),
                        Url = TrailerRoot + movieId + "/" + Trailer.PreferredFile
                    });
                }
            }
            catch (Exception ex)
            {
                LogManager.GetLogger().Warn(
                    ex, "ImageRotater: could not read Steam's trailers");
            }
        }

        // JSON-escaped slashes, and the ?t= cache-buster Steam appends.
        private static string Clean(string url)
        {
            string cleaned = Regex.Unescape(url ?? string.Empty).Replace("\\/", "/");

            int query = cleaned.IndexOf('?');

            return query > 0 ? cleaned.Substring(0, query) : cleaned;
        }

        // Steam's own app search, used to find an appid from a game name.
        //
        // This is what lets the Steam tab work for a game that did NOT come
        // from Steam. The artwork is keyed by appid, and an appid exists
        // whether or not this user owns the Steam copy - a GOG or Xbox install
        // of the same game reaches the same art on the same CDN.
        //
        // Not the old ISteamApps/GetAppList endpoint: Valve has retired it and
        // every version now returns 404. This one is better suited anyway -
        // it answers with a handful of matches rather than a 10 MB dump of
        // every app on the store, and it does its own fuzzy matching, which is
        // how "Baldurs Gate 3" finds "Baldur's Gate 3".
        private const string SearchUrl = "https://steamcommunity.com/actions/SearchApps/";

        // Names looked up in this session. Steam is asked once per game.
        private static readonly Dictionary<string, string> AppIdCache =
            new Dictionary<string, string>(StringComparer.Ordinal);

        private static readonly object CacheLock = new object();

        // The appid for a game name, or null when Steam has nothing matching.
        public static string FindAppIdByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            string key = NormaliseName(name);

            if (key.Length == 0)
            {
                return null;
            }

            lock (CacheLock)
            {
                string cached;

                if (AppIdCache.TryGetValue(key, out cached))
                {
                    return cached;
                }
            }

            string found = Search(name, key);

            lock (CacheLock)
            {
                // Negative results cached too, so a game Steam does not have
                // is not looked up again every time the dialog opens.
                AppIdCache[key] = found;
            }

            return found;
        }

        private static string Search(string name, string normalisedName)
        {
            try
            {
                string json;

                using (var client = new WebClient())
                {
                    client.Encoding = System.Text.Encoding.UTF8;
                    json = client.DownloadString(SearchUrl + Uri.EscapeDataString(name));
                }

                // [{"appid":"1145360","name":"Hades", ...}, ...]
                var matches = Regex.Matches(
                    json,
                    @"""appid""\s*:\s*""(\d+)""\s*,\s*""name""\s*:\s*""(.*?)(?<!\\)""");

                string firstAppId = null;

                foreach (Match m in matches)
                {
                    string appId = m.Groups[1].Value;
                    string resultName = Regex.Unescape(m.Groups[2].Value);

                    // An exact match on the normalised name wins outright.
                    // Steam orders by relevance, not exactness, so a search for
                    // "Hades" leads with "Hades II" - taking the first result
                    // would quietly fetch the sequel's artwork.
                    if (NormaliseName(resultName) == normalisedName)
                    {
                        return appId;
                    }

                    if (firstAppId == null)
                    {
                        firstAppId = appId;
                    }
                }

                // No exact match. The best result is still usually right -
                // Playnite names carry edition suffixes Steam does not - so it
                // is offered rather than discarded. The user sees the artwork
                // before choosing it.
                return firstAppId;
            }
            catch (Exception ex)
            {
                LogManager.GetLogger().Warn(
                    ex, "ImageRotater: could not search Steam for " + name);

                return null;
            }
        }

        // Lowercased, with punctuation and spacing removed. Names differ across
        // stores in exactly these ways: curly versus straight apostrophes,
        // colons, and trademark symbols.
        private static string NormaliseName(string name)
        {
            var sb = new System.Text.StringBuilder(name.Length);

            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c))
                {
                    sb.Append(char.ToLowerInvariant(c));
                }
            }

            return sb.ToString();
        }

        // HEAD rather than GET: this only needs to know an asset exists, and
        // downloading two backgrounds to find out costs half a megabyte.
        //
        // Not every game has every asset, and a tile that 404s renders as a
        // blank the user cannot explain - so nothing is offered unchecked.
        public static bool Exists(string url)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "HEAD";
                request.Timeout = 5000;

                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    return response.StatusCode == HttpStatusCode.OK;
                }
            }
            catch (WebException)
            {
                // A 404 arrives as an exception. Genuinely absent, not an error
                // worth logging for every game without a hero image.
                return false;
            }
            catch (Exception ex)
            {
                LogManager.GetLogger().Warn(ex, $"ImageRotater: could not check {url}");
                return false;
            }
        }
    }
}
