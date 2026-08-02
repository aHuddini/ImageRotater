using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using Playnite.SDK;

namespace ImageRotater.Services
{
    // Converts an odd-shaped background into a screen-shaped composite:
    // the image fit-centered, over a blurred, darkened fill of itself.
    //
    // This is the rescue for games ShapeBias cannot help - ones whose pool has
    // no screen-shaped images at all. Rotation letterboxes their pick at write
    // time, so every stored background ends up screen-shaped and Playnite's
    // shape-change re-fit (the crop/blur flash on game switches, present with
    // no plugins installed) has nothing left to trigger on. No artwork is
    // dropped and the source files are never modified.
    //
    // The "blur" is the classic cheap trick: scale the image down to a few
    // dozen pixels and stretch it back up. GDI+ has no gaussian blur, and at
    // backdrop size the difference is invisible.
    public static class Letterboxer
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        // Matches ShapeBias: within this of the target, the image is already
        // screen-shaped and letterboxing it would add bars for no reason.
        private const double Tolerance = 0.35;

        // How strongly the fill is pushed back so the real image reads as the
        // subject rather than blending into its own backdrop. Kept light: a
        // heavy darken made the bars read as black mush and the whole
        // composite look cropped.
        private const int FillDarkenAlpha = 64;

        private const long JpegQuality = 90L;

        // Composites live in a dot-subfolder of the kind folder, which
        // GetImagePaths never lists (it enumerates files, not subfolders) - so
        // a composite can never become a rotation candidate and be composited
        // again.
        public const string CacheFolderName = ".lbx";

        // The letterboxed stand-in for a pick, or the original path when it is
        // already screen-shaped or cannot be composed. Never throws: a failed
        // compose costs the flash, not the rotation.
        public static string For(string sourcePath, double targetAspect)
        {
            try
            {
                if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
                {
                    return sourcePath;
                }

                double aspect = ShapeBias.AspectOf(sourcePath);
                if (aspect <= 0 || Math.Abs(aspect - targetAspect) <= Tolerance)
                {
                    return sourcePath;
                }

                string cacheDir = Path.Combine(Path.GetDirectoryName(sourcePath), CacheFolderName);

                // The style version is part of the name, so changing how the
                // composite LOOKS invalidates every cache even though the
                // sources are unchanged - mtime alone cannot catch that.
                // v1: cover-cropped, heavily darkened fill - read as "oddly
                //     cropped". v2: blur stretched edge-to-edge, but the image
                //     sat centred with a hard bottom edge, so the top bar
                //     stood out. v3: image pinned to the top, its bottom edge
                //     alpha-fading into the fill.
                string cached = Path.Combine(
                    cacheDir, Path.GetFileNameWithoutExtension(sourcePath) + ".v3.jpg");

                // Reclaim previous styles' composites while we are here.
                TryDeleteQuiet(Path.Combine(
                    cacheDir, Path.GetFileNameWithoutExtension(sourcePath) + ".jpg"));
                TryDeleteQuiet(Path.Combine(
                    cacheDir, Path.GetFileNameWithoutExtension(sourcePath) + ".v2.jpg"));

                // Rebuilt when the source is newer - downloads overwrite files
                // under the same name, and a stale composite would resurrect
                // the old artwork.
                if (File.Exists(cached) &&
                    File.GetLastWriteTimeUtc(cached) >= File.GetLastWriteTimeUtc(sourcePath))
                {
                    return cached;
                }

                Directory.CreateDirectory(cacheDir);

                return Compose(sourcePath, cached, targetAspect) ? cached : sourcePath;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"ImageRotater: could not letterbox {sourcePath}");
                return sourcePath;
            }
        }

        private static void TryDeleteQuiet(string path)
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
                // A leftover cache file is clutter, not a failure.
            }
        }

        // Writes the composite. Public and static so tests can drive it against
        // real files without a store or settings.
        public static bool Compose(string sourcePath, string outputPath, double targetAspect)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(sourcePath);

                using (var input = new MemoryStream(bytes))
                using (Image source = Image.FromStream(input))
                {
                    // Canvas: big enough that the source fits entirely at its
                    // native size, shaped like the target. A wider-than-target
                    // source sets the width; a taller one sets the height.
                    double sourceAspect = (double)source.Width / source.Height;

                    int canvasW;
                    int canvasH;

                    if (sourceAspect > targetAspect)
                    {
                        canvasW = source.Width;
                        canvasH = Math.Max(1, (int)Math.Round(canvasW / targetAspect));
                    }
                    else
                    {
                        canvasH = source.Height;
                        canvasW = Math.Max(1, (int)Math.Round(canvasH * targetAspect));
                    }

                    using (var canvas = new Bitmap(canvasW, canvasH))
                    using (Graphics g = Graphics.FromImage(canvas))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                        DrawBlurredFill(g, source, canvasW, canvasH);

                        using (var darken = new SolidBrush(Color.FromArgb(FillDarkenAlpha, 0, 0, 0)))
                        {
                            g.FillRectangle(darken, 0, 0, canvasW, canvasH);
                        }

                        // The image itself: pinned to the TOP, horizontally
                        // centred, its bottom edge alpha-faded into the fill.
                        //
                        // Top-aligned so all the fill sits below the image -
                        // two bars framing a centred strip made the top bar
                        // conspicuous, and content in game art overwhelmingly
                        // reads top-down. The fade removes the hard seam that
                        // made the composite look like a crop boundary.
                        int x = (canvasW - source.Width) / 2;

                        using (Bitmap faded = WithBottomFade(source))
                        {
                            g.DrawImage(faded, x, 0, source.Width, source.Height);
                        }

                        ImageCodecInfo jpeg = ImageCodecInfo.GetImageEncoders()
                            .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);

                        if (jpeg == null)
                        {
                            return false;
                        }

                        string temp = outputPath + ".tmp";

                        using (var parameters = new EncoderParameters(1))
                        using (var quality = new EncoderParameter(Encoder.Quality, JpegQuality))
                        {
                            parameters.Param[0] = quality;
                            canvas.Save(temp, jpeg, parameters);
                        }

                        if (File.Exists(outputPath))
                        {
                            File.Delete(outputPath);
                        }

                        File.Move(temp, outputPath);
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"ImageRotater: letterbox compose failed for {sourcePath}");
                return false;
            }
        }

        // How much of the image's height fades out at its bottom edge.
        private const double FadeFraction = 0.22;

        // A copy of the source whose bottom rows ramp to transparent, so the
        // fill underneath shows through gradually instead of meeting a hard
        // seam - the seam is what made v2 read like a crop boundary.
        //
        // Per-pixel alpha via LockBits rather than slices of decreasing
        // opacity: sliced fades band visibly on smooth skies, which game art
        // is full of.
        private static Bitmap WithBottomFade(Image source)
        {
            var faded = new Bitmap(source.Width, source.Height,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            using (Graphics g = Graphics.FromImage(faded))
            {
                g.DrawImage(source, 0, 0, source.Width, source.Height);
            }

            int fadeRows = Math.Max(16, (int)(source.Height * FadeFraction));
            int fadeStart = Math.Max(0, source.Height - fadeRows);

            var zone = new Rectangle(0, fadeStart, faded.Width, faded.Height - fadeStart);
            BitmapData data = faded.LockBits(
                zone, ImageLockMode.ReadWrite,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            try
            {
                // Marshal round-trip rather than unsafe pointers: one method
                // does not justify enabling unsafe code project-wide, and the
                // zone is a fraction of one image.
                int bytes = data.Stride * zone.Height;
                byte[] buffer = new byte[bytes];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buffer, 0, bytes);

                for (int row = 0; row < zone.Height; row++)
                {
                    // Linear ramp from opaque at the zone's top to fully
                    // transparent at the image's last row.
                    double keep = 1.0 - ((row + 1) / (double)zone.Height);
                    int lineStart = row * data.Stride;

                    for (int col = 0; col < zone.Width; col++)
                    {
                        int alphaIndex = lineStart + col * 4 + 3;
                        buffer[alphaIndex] = (byte)(buffer[alphaIndex] * keep);
                    }
                }

                System.Runtime.InteropServices.Marshal.Copy(buffer, 0, data.Scan0, bytes);
            }
            finally
            {
                faded.UnlockBits(data);
            }

            return faded;
        }

        // Fill the whole canvas with a heavy blur of the image, stretched
        // edge-to-edge WITHOUT preserving its aspect.
        //
        // Deliberately not cover-cropped: a cover-scaled fill shows a zoomed
        // middle band of the image, which reads as "oddly cropped" behind the
        // real thing. Stretching the blur ignores geometry entirely and gives
        // a soft colour wash of the whole image - at this blur strength the
        // distortion is imperceptible, and every part of the image
        // contributes to the backdrop.
        //
        // The blur itself is the cheap classic: shrink to a few pixels, then
        // bicubic back up. GDI+ has no gaussian, and at backdrop scale this is
        // indistinguishable from one.
        private static void DrawBlurredFill(Graphics g, Image source, int canvasW, int canvasH)
        {
            const int tinyWidth = 16;

            int tinyHeight = Math.Max(1, tinyWidth * source.Height / Math.Max(1, source.Width));

            using (var tiny = new Bitmap(tinyWidth, tinyHeight))
            using (Graphics tg = Graphics.FromImage(tiny))
            {
                tg.InterpolationMode = InterpolationMode.Bilinear;
                tg.DrawImage(source, 0, 0, tinyWidth, tinyHeight);

                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(tiny, 0, 0, canvasW, canvasH);
            }
        }
    }
}
