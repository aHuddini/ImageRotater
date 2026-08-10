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

        // Where the user said ffmpeg lives, empty meaning "search PATH".
        //
        // Set from settings rather than read from them, so this class stays
        // usable without the whole plugin standing behind it - the bulk
        // conversion, the download path and the tests all reach it.
        public static string ConfiguredPath { get; set; }

        public static bool IsAvailable
        {
            get { return FindFfmpeg() != null; }
        }

        // Full path to ffmpeg, or null when it cannot be found.
        public static string FindFfmpeg()
        {
            return ExternalTool.Resolve(ConfiguredPath, ExternalTool.FfmpegExe);
        }

        // Formats worth converting to MP4.
        //
        // GIF is the obvious one, but an animated WebP or APNG downloads and
        // renders as a still first frame with nothing saying why - the plugin
        // treats only .gif as animated, so the motion is silently lost.
        // BackgroundChanger converts the same four, which is the better list.
        //
        // WebM is here because it is video Windows often cannot decode: the
        // plugin plays MP4 everywhere and WebM only where the user has a codec,
        // so converting it turns a maybe into a yes.
        private static readonly string[] ConvertibleExtensions =
            { ".gif", ".webp", ".apng", ".png", ".webm" };

        // True for a file worth handing to ffmpeg.
        //
        // .png is in the list above only because APNG files are routinely named
        // .png - so a still PNG would match too. ffmpeg produces a
        // single-frame MP4 from one, which is worse than leaving it alone, so
        // the frame count decides rather than the extension.
        public static bool IsConvertible(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            string ext = Path.GetExtension(path);

            foreach (string candidate in ConvertibleExtensions)
            {
                if (string.Equals(ext, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return HasMultipleFrames(path);
                }
            }

            return false;
        }

        // Whether a file actually moves.
        //
        // GDI+ reports frame counts for GIF and APNG-in-PNG. WebP and WebM it
        // cannot open at all, and those are taken on trust: a still WebP
        // converted to MP4 is a wasted step rather than a broken one, and a
        // WebM is video by definition.
        private static bool HasMultipleFrames(string path)
        {
            string ext = Path.GetExtension(path) ?? string.Empty;

            if (ext.Equals(".webm", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".webp", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            try
            {
                using (var image = System.Drawing.Image.FromFile(path))
                {
                    var dimension = new System.Drawing.Imaging.FrameDimension(
                        image.FrameDimensionsList[0]);

                    return image.GetFrameCount(dimension) > 1;
                }
            }
            catch (Exception)
            {
                // Unreadable by GDI+ - leave it alone rather than guess.
                return false;
            }
        }

        // Converts one GIF, returning the MP4 path or null on failure.
        //
        // The source is left alone. Callers that want it gone delete it after
        // confirming the result, so a failed conversion can never lose the only
        // copy of a user's artwork.
        // True for a FRAGMENTED mp4 - one built from moof/mdat fragments rather
        // than a single progressive stream.
        //
        // YouTube serves DASH, so a download taken straight from it is exactly
        // this. ffmpeg reads them without complaint, so a decode check passes
        // and the file looks healthy - but Windows Media Foundation does not,
        // and neither do most desktop players. MediaElement reports such a file
        // as open and playing, with the position advancing, while rendering
        // solid black.
        //
        // Detected from the ftyp brand, which is the first atom in the file.
        public static bool IsFragmented(string path)
        {
            try
            {
                using (var stream = File.OpenRead(path))
                {
                    var header = new byte[12];

                    if (stream.Read(header, 0, header.Length) < header.Length)
                    {
                        return false;
                    }

                    if (header[4] != 'f' || header[5] != 't' ||
                        header[6] != 'y' || header[7] != 'p')
                    {
                        return false;
                    }

                    string brand = System.Text.Encoding.ASCII.GetString(header, 8, 4);

                    return brand == "dash" || brand == "iso5" || brand == "iso6";
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Rewrites a fragmented mp4 as a normal progressive one.
        //
        // A stream copy, not a re-encode: the H.264 inside is fine, it is only
        // the container that nothing but ffmpeg will read. Costs a second and
        // loses no quality.
        public static bool Remux(string path)
        {
            string ffmpeg = FindFfmpeg();

            if (ffmpeg == null || !File.Exists(path))
            {
                return false;
            }

            string temp = path + ".remux.mp4";

            try
            {
                var start = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments =
                        $"-y -i \"{path}\" -c copy -movflags +faststart -an \"{temp}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };

                using (Process process = Process.Start(start))
                {
                    if (process == null)
                    {
                        return false;
                    }

                    process.StandardError.ReadToEnd();
                    process.StandardOutput.ReadToEnd();
                    process.WaitForExit();

                    if (process.ExitCode != 0 || !File.Exists(temp))
                    {
                        TryDelete(temp);
                        return false;
                    }
                }

                File.Delete(path);
                File.Move(temp, path);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"ImageRotater: could not remux {Path.GetFileName(path)}");
                TryDelete(temp);
                return false;
            }
        }

        // Pulls a single still frame out of a video.
        //
        // Needed because a theme's tile binding is a still image path, so a
        // game whose cover is a video has nothing for it to show. Publishing
        // only the video left the tile on its 1x1 transparent placeholder,
        // which Playnite stretches across the tile and renders as solid black -
        // a downloaded video looked like broken artwork.
        //
        // PosterFrame cannot do this: it is GDI+, which decodes no video
        // container at all. ffmpeg is already here for conversion.
        //
        // Returns null when ffmpeg is missing or the frame cannot be read.
        public static string ExtractPoster(string videoPath, string outputPath)
        {
            string ffmpeg = FindFfmpeg();

            if (ffmpeg == null || string.IsNullOrEmpty(videoPath) || !File.Exists(videoPath))
            {
                return null;
            }

            string temp = outputPath + ".tmp.png";

            try
            {
                var start = new ProcessStartInfo
                {
                    FileName = ffmpeg,

                    // One second in rather than frame zero: videos routinely
                    // open on a black or a fade-in frame, which is exactly the
                    // useless poster this is meant to avoid.
                    //
                    // PNG so the tile keeps the lossless quality the plugin
                    // prefers everywhere else.
                    Arguments =
                        $"-y -ss 00:00:01 -i \"{videoPath}\" -frames:v 1 -q:v 2 \"{temp}\"",
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

                    // Drained rather than ignored: a full pipe deadlocks the
                    // process being waited on.
                    process.StandardError.ReadToEnd();
                    process.StandardOutput.ReadToEnd();

                    process.WaitForExit();

                    if (process.ExitCode != 0 || !File.Exists(temp))
                    {
                        // A video shorter than the seek point lands here.
                        TryDelete(temp);
                        return null;
                    }
                }

                if (File.Exists(outputPath))
                {
                    File.Delete(outputPath);
                }

                File.Move(temp, outputPath);
                return outputPath;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"ImageRotater: could not take a poster frame from {Path.GetFileName(videoPath)}");
                TryDelete(temp);
                return null;
            }
        }

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
