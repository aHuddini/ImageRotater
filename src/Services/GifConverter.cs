using System;
using System.Diagnostics;
using System.IO;
using Playnite.SDK;

namespace ImageRotater.Services
{
    // Converts animated GIFs to MP4 using ffmpeg, when the user has ffmpeg.
    //
    // Worth doing for two reasons, both measured against a real 4.7 MB library
    // GIF: the MP4 came out at 1.0 MB, a 78% saving, and it plays through
    // MediaElement - hardware-assisted H.264 - rather than decoding frames on
    // the UI thread the way XamlAnimatedGif must. In a 32-bit process hosting a
    // grid of tiles, that difference is the one that decides whether Playnite
    // stays up.
    //
    // ffmpeg is NOT bundled and never will be: it is GPL and this plugin is
    // MIT, so shipping the binary would force a licence change on the whole
    // project. The plugin uses it when the user already has it and says so
    // plainly when they do not, rather than silently doing nothing.
    public static class GifConverter
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        // Resolved once per session. Probing PATH costs a process spawn, and
        // ffmpeg does not appear or vanish mid-session in any way worth
        // handling.
        private static bool _probed;
        private static string _ffmpegPath;

        public static bool IsAvailable
        {
            get { return FindFfmpeg() != null; }
        }

        // Full path to ffmpeg, or null when the user does not have it.
        //
        // PATH only. Hunting through Program Files for someone else's copy is
        // guesswork that produces confusing failures when it finds the wrong
        // build; if it is not on PATH the honest answer is that it is not
        // installed.
        public static string FindFfmpeg()
        {
            if (_probed)
            {
                return _ffmpegPath;
            }

            _probed = true;

            try
            {
                string pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

                foreach (string dir in pathVar.Split(Path.PathSeparator))
                {
                    if (string.IsNullOrWhiteSpace(dir))
                    {
                        continue;
                    }

                    try
                    {
                        string candidate = Path.Combine(dir.Trim(), "ffmpeg.exe");

                        if (File.Exists(candidate))
                        {
                            _ffmpegPath = candidate;
                            return _ffmpegPath;
                        }
                    }
                    catch (Exception)
                    {
                        // A malformed PATH entry must not stop the search.
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not probe for ffmpeg");
            }

            return _ffmpegPath;
        }

        // Converts one GIF, returning the MP4 path or null on failure.
        //
        // The source is left alone. Callers that want it gone delete it after
        // confirming the result, so a failed conversion can never lose the only
        // copy of a user's artwork.
        public static string Convert(string gifPath, string outputPath = null)
        {
            string ffmpeg = FindFfmpeg();

            if (ffmpeg == null || string.IsNullOrEmpty(gifPath) || !File.Exists(gifPath))
            {
                return null;
            }

            string target = outputPath ?? Path.ChangeExtension(gifPath, ".mp4");
            string temp = target + ".tmp.mp4";

            try
            {
                // yuv420p and the even-dimension scale are both required, not
                // stylistic. H.264 in an MP4 container will not play in
                // MediaElement without 4:2:0 chroma, and libx264 refuses odd
                // dimensions outright - GIFs are routinely odd-sized.
                //
                // faststart moves the index to the front so playback can begin
                // before the whole file is read. crf 23 is ffmpeg's default
                // quality: visually transparent for artwork at this size.
                var start = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments =
                        $"-y -i \"{gifPath}\" -movflags faststart -pix_fmt yuv420p "
                        + "-vf \"scale=trunc(iw/2)*2:trunc(ih/2)*2\" "
                        + $"-c:v libx264 -crf 23 -an \"{temp}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };

                using (Process process = Process.Start(start))
                {
                    if (process == null)
                    {
                        return null;
                    }

                    // Drained rather than ignored: ffmpeg writes progress to
                    // stderr continuously, and a full pipe buffer deadlocks the
                    // process we are waiting on.
                    process.StandardError.ReadToEnd();
                    process.StandardOutput.ReadToEnd();

                    process.WaitForExit();

                    if (process.ExitCode != 0)
                    {
                        Logger.Warn(
                            $"ImageRotater: ffmpeg exited {process.ExitCode} converting {Path.GetFileName(gifPath)}");
                        TryDelete(temp);
                        return null;
                    }
                }

                if (!File.Exists(temp) || new FileInfo(temp).Length == 0)
                {
                    TryDelete(temp);
                    return null;
                }

                if (File.Exists(target))
                {
                    File.Delete(target);
                }

                File.Move(temp, target);
                return target;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"ImageRotater: could not convert {gifPath} to MP4");
                TryDelete(temp);
                return null;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
