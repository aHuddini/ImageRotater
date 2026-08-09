using System.Collections.Generic;
using NUnit.Framework;
using ImageRotater.Models;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    // Prefer PNG over JPEG among comparable sizes.
    //
    // Ranking backgrounds purely on pixel count picked heavily compressed
    // JPEGs over clean PNGs of nearly the same size. A Fullscreen theme then
    // scales that up to fill a TV, and the compression artefacts are what the
    // user actually sees. PNG is lossless and survives the scaling; the file is
    // bigger, which is the trade being made on purpose.
    //
    // Comparable sizes, not absolutely: a 640x360 PNG beating a 4K JPEG would
    // be worse than the problem being fixed.
    [TestFixture]
    public class LosslessPreferenceTests
    {
        private static SteamGridDbArtwork Art(int w, int h, string mime, string url = null)
        {
            return new SteamGridDbArtwork
            {
                Width = w,
                Height = h,
                Mime = mime,
                Url = url ?? "http://example.com/a" + (mime == "image/png" ? ".png" : ".jpg")
            };
        }

        [Test]
        public void PngIsRecognisedAsLossless()
        {
            Assert.IsTrue(CoverAspect.IsLossless(Art(100, 100, "image/png")));
            Assert.IsTrue(CoverAspect.IsLossless(Art(100, 100, "image/webp")));
        }

        [Test]
        public void JpegIsNotLossless()
        {
            Assert.IsFalse(CoverAspect.IsLossless(Art(100, 100, "image/jpeg")));
            Assert.IsFalse(CoverAspect.IsLossless(Art(100, 100, "image/jpg")));
        }

        // Web search results often carry no usable MIME, so the file name is
        // the only signal available.
        [Test]
        public void TheUrlIsUsedWhenTheMimeSaysNothing()
        {
            Assert.IsTrue(CoverAspect.IsLossless(
                Art(100, 100, null, "http://example.com/art.png")));

            Assert.IsFalse(CoverAspect.IsLossless(
                Art(100, 100, null, "http://example.com/art.jpg")));
        }

        [Test]
        public void NullIsNotLossless()
        {
            Assert.IsFalse(CoverAspect.IsLossless(null));
        }

        // The cover ranking applies the preference only as a tie-break, so
        // shape is never traded away for format.
        [Test]
        public void ShapeStillWinsOverFormatForCovers()
        {
            var candidates = new List<SteamGridDbArtwork>
            {
                // Right shape for a 2:3 cover, but lossy.
                Art(600, 900, "image/jpeg"),

                // Lossless, and clearly the wrong shape.
                Art(1920, 1080, "image/png")
            };

            SteamGridDbArtwork best = CoverAspect.BestCover(candidates, 600.0 / 900.0);

            Assert.AreEqual(600, best.Width,
                "a lossless image of the wrong shape is still the wrong shape");
        }

        // Same shape, so quality decides.
        [Test]
        public void AmongTheSameShapeTheLosslessOneWins()
        {
            var candidates = new List<SteamGridDbArtwork>
            {
                Art(600, 900, "image/jpeg"),
                Art(600, 900, "image/png")
            };

            SteamGridDbArtwork best = CoverAspect.BestCover(candidates, 600.0 / 900.0);

            Assert.IsTrue(CoverAspect.IsLossless(best),
                "identical shape and size - the lossless one is strictly better");
        }

        // But a much larger lossy image still wins: the point is to avoid
        // trading away real resolution.
        [Test]
        public void AMuchLargerLossyCoverStillWins()
        {
            var candidates = new List<SteamGridDbArtwork>
            {
                Art(300, 450, "image/png"),
                Art(1200, 1800, "image/jpeg")
            };

            SteamGridDbArtwork best = CoverAspect.BestCover(candidates, 600.0 / 900.0);

            Assert.AreEqual(1200, best.Width,
                "16x the pixels beats the format preference");
        }
    }
}
