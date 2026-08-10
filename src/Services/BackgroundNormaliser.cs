using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using Playnite.SDK;

namespace ImageRotater.Services
{
    // Resizes a background so every candidate for a game is the same width.
    //
    // Why this exists, and it is not about file size:
    //
    // Playnite blurs the window background with a WPF BlurEffect at a FIXED
    // radius (the user's BackgroundImageBlurAmount, commonly 40-60), applied to
    // the container AFTER the image inside has been scaled to fit. It also
    // decodes every background to the screen's working width - so a 3840px
    // source is downscaled 2.7x while a 1440px source is untouched.
    //
    // A fixed-radius blur over those two therefore covers a very different
    // fraction of the actual picture, and rotating between them makes the blur
    // visibly jump even though both fill the same rectangle. Users see it as
    // "the background pops when it changes" - and it disappears entirely when
    // two images happen to share a resolution, which is the clue that gave this
    // away.
    //
    // Normalising the width makes consecutive picks blur identically. Only the
    // published copy is touched; the candidates keep their original resolution.
    public static class BackgroundNormaliser
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        // Below this, upscaling would visibly soften the image for no gain -
        // better a slightly different blur than a mushy background.
        private const int MinimumWidth = 1280;

        // Above this there is nothing to gain: Playnite decodes to the screen's
        // working width anyway, and on a 4K display that is 3840.
        private const int MaximumWidth = 3840;

        // Resizes into targetPath when the source is not already the right
        // width. Returns the path actually written - which is sourcePath itself
        // when no work was needed.
        //
        // Never throws: a background that cannot be resized is published as it
        // is, which is the pre-existing behaviour.
        public static string NormaliseTo(string sourcePath, int targetWidth, string targetPath)
        {
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
            {
                return sourcePath;
            }

            if (targetWidth < MinimumWidth || targetWidth > MaximumWidth)
            {
                return sourcePath;
            }

            try
            {
                using (var source = new Bitmap(sourcePath))
                {
                    // Already right, so publishing a re-encoded copy would only
                    // lose quality.
                    if (source.Width == targetWidth)
                    {
                        return sourcePath;
                    }

                    // Aspect ratio preserved - Playnite stretches to fill, and
                    // changing the ratio here would crop differently from the
                    // original.
                    int height = (int)Math.Round(
                        source.Height * (targetWidth / (double)source.Width));

                    if (height < 1)
                    {
                        return sourcePath;
                    }

                    using (var resized = new Bitmap(targetWidth, height))
                    using (var graphics = Graphics.FromImage(resized))
                    {
                        // HighQualityBicubic because this image is then blurred
                        // and stretched across a whole window - resampling
                        // artefacts that would vanish on a thumbnail are
                        // plainly visible at that size.
                        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        graphics.SmoothingMode = SmoothingMode.HighQuality;

                        graphics.DrawImage(source, new Rectangle(0, 0, targetWidth, height));

                        resized.Save(targetPath, ImageFormat.Png);
                    }
                }

                return targetPath;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not normalise " + Path.GetFileName(sourcePath));
                return sourcePath;
            }
        }

        // The width every background for this game should be published at.
        //
        // The LARGEST candidate wins, capped at the screen width. Upscaling a
        // small image to match a large one would soften it, so the target is
        // whatever the best source can supply - and everything bigger comes
        // down to meet it.
        public static int TargetWidthFor(System.Collections.Generic.IEnumerable<string> candidates, int screenWidth)
        {
            int widest = 0;

            foreach (string path in candidates)
            {
                try
                {
                    using (var image = Image.FromFile(path))
                    {
                        if (image.Width > widest)
                        {
                            widest = image.Width;
                        }
                    }
                }
                catch (Exception)
                {
                    // Not an image, or unreadable - it cannot be a candidate
                    // for the target either.
                }
            }

            if (widest == 0)
            {
                return 0;
            }

            int cap = screenWidth > 0 ? Math.Min(screenWidth, MaximumWidth) : MaximumWidth;

            return Math.Min(widest, cap);
        }
    }
}
