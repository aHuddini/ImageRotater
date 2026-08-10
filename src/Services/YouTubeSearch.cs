using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using ImageRotater.Models;

namespace ImageRotater.Services
{
    // Finds videos on YouTube through yt-dlp, as artwork candidates.
    //
    // Why this exists at all: a live wallpaper is a video, and the good ones
    // are on YouTube rather than in any artwork database. SteamGridDB has
    // animated entries but they are WebP and WebM that WPF cannot decode;
    // Steam has no motion art. This is the source that actually has it.
    //
    // Searching only lists results - nothing is downloaded until the user picks
    // one. --flat-playlist is what keeps that cheap: it returns the search page
    // metadata without visiting each video.
    public class YouTubeSearch
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        // Default search wording. "live wallpaper" rather than "trailer" or
        // "gameplay" on purpose: it selects for looping, mostly wordless video
        // shot to sit behind something else, which is exactly what a background
        // is. A trailer has cuts, captions and a voiceover.
        public const string DefaultSearchSuffix = "live wallpaper";

        public static string DefaultQueryFor(string gameName)
        {
            return ((gameName ?? string.Empty).Trim() + " " + DefaultSearchSuffix).Trim();
        }

        private readonly string _ytDlpPath;
        private readonly string _denoPath;

        public YouTubeSearch(ImageRotaterSettings settings)
        {
            _ytDlpPath = ExternalTool.Resolve(
                settings?.YtDlpPath, ExternalTool.YtDlpExe);

            // Not resolved to be RUN - only to have its folder put on yt-dlp's
            // PATH, which is the only way yt-dlp accepts a JS runtime.
            _denoPath = ExternalTool.Resolve(
                settings?.DenoPath, ExternalTool.DenoExe);
        }

        public bool IsAvailable
        {
            get { return _ytDlpPath != null; }
        }

        // Whether yt-dlp has the JavaScript runtime it needs to read YouTube.
        //
        // Worth reporting separately because without it yt-dlp exits 0 with no
        // results, which is indistinguishable from a search that genuinely
        // found nothing.
        public bool HasJsRuntime
        {
            get { return _denoPath != null; }
        }

        public async Task<List<YouTubeVideo>> SearchAsync(
            string query, int count, CancellationToken cancellationToken)
        {
            if (_ytDlpPath == null)
            {
                throw new InvalidOperationException(
                    "yt-dlp is not set up. Add it on the Setup tab in settings.");
            }

            if (count < 1)
            {
                count = 1;
            }

            // Quotes are stripped rather than escaped: the whole term is
            // wrapped in quotes below, and a stray one would end the argument
            // early and hand the rest to yt-dlp as flags.
            string safeQuery = (query ?? string.Empty).Replace("\"", string.Empty);

            var psi = new ProcessStartInfo
            {
                FileName = _ytDlpPath,

                // --flat-playlist returns the search listing without visiting
                // each video, which is the difference between a search taking
                // a second and taking a minute.
                Arguments =
                    $"\"ytsearch{count}:{safeQuery}\" --dump-json --flat-playlist --no-warnings",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            ExternalTool.PrependToolDirToPath(psi, _denoPath);

            return await Task.Run(() => Run(psi, cancellationToken)).ConfigureAwait(true);
        }

        private List<YouTubeVideo> Run(ProcessStartInfo psi, CancellationToken cancellationToken)
        {
            var results = new List<YouTubeVideo>();

            try
            {
                using (Process process = Process.Start(psi))
                {
                    if (process == null)
                    {
                        return results;
                    }

                    // Both streams drained on their own tasks. Waiting on
                    // stdout while stderr fills its pipe deadlocks the child -
                    // yt-dlp is chatty enough on stderr to hit that.
                    Task<string> readOutput = Task.Run(() => process.StandardOutput.ReadToEnd());
                    Task<string> readError = Task.Run(() => process.StandardError.ReadToEnd());

                    while (!process.WaitForExit(100))
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            try { process.Kill(); } catch { }
                            return results;
                        }
                    }

                    string output = readOutput.Result;
                    string error = readError.Result;

                    if (process.ExitCode != 0)
                    {
                        Logger.Warn("ImageRotater: yt-dlp search failed - " + error);
                        return results;
                    }

                    foreach (string line in output.Split(
                        new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        YouTubeVideo video = Parse(line);

                        if (video != null)
                        {
                            results.Add(video);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "ImageRotater: YouTube search failed");
            }

            return results;
        }

        // One JSON object per line, which is what --dump-json emits.
        private static YouTubeVideo Parse(string line)
        {
            try
            {
                JObject o = JObject.Parse(line);

                string id = (string)o["id"];

                if (string.IsNullOrWhiteSpace(id))
                {
                    return null;
                }

                return new YouTubeVideo
                {
                    Id = id,
                    Title = (string)o["title"] ?? "(untitled)",
                    Channel = (string)o["channel"] ?? (string)o["uploader"] ?? string.Empty,
                    DurationSeconds = ReadInt(o["duration"]),
                    ViewCount = ReadInt(o["view_count"]),

                    // Built rather than read from the JSON: --flat-playlist
                    // returns a thumbnail array whose contents vary, while this
                    // URL form is stable for every video.
                    ThumbnailUrl = $"https://i.ytimg.com/vi/{id}/hqdefault.jpg",

                    Url = (string)o["url"] ?? $"https://www.youtube.com/watch?v={id}"
                };
            }
            catch (Exception)
            {
                // One malformed line must not lose the rest of the results.
                return null;
            }
        }

        private static long ReadInt(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return 0;
            }

            try { return (long)token; } catch (Exception) { return 0; }
        }
    }
}
