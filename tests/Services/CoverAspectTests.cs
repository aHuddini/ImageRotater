using System.Collections.Generic;
using NUnit.Framework;
using ImageRotater.Models;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    // Covers are drawn into a fixed-aspect card, so shape has to beat pixel
    // count. These tests pin that a correctly-shaped small image wins over a
    // sharper one of the wrong shape - the whole reason covers cannot reuse the
    // background "biggest wins" rule.
    [TestFixture]
    public class CoverAspectTests
    {
        private const double TwoToThree = 2.0 / 3.0;

        private static SteamGridDbArtwork Art(int id, int width, int height)
        {
            return new SteamGridDbArtwork
            {
                Id = id,
                Url = "https://example.invalid/" + id,
                Width = width,
                Height = height,
                Mime = "image/png"
            };
        }

        [Test]
        public void FromGridRatio_ComputesWidthOverHeight()
        {
            Assert.AreEqual(TwoToThree, CoverAspect.FromGridRatio(2, 3), 0.0001);
            Assert.AreEqual(1.0, CoverAspect.FromGridRatio(1, 1), 0.0001);
        }

        // A zero or negative ratio would produce a NaN or infinite target that
        // matches nothing, so it must fall back rather than propagate.
        [TestCase(0, 3)]
        [TestCase(2, 0)]
        [TestCase(-2, 3)]
        public void FromGridRatio_InvalidInputFallsBackToDefault(int w, int h)
        {
            Assert.AreEqual(CoverAspect.DefaultAspect, CoverAspect.FromGridRatio(w, h), 0.0001);
        }

        [Test]
        public void AspectDistance_ExactMatchIsZero()
        {
            Assert.AreEqual(0.0, CoverAspect.AspectDistance(600, 900, TwoToThree), 0.0001);
        }

        [Test]
        public void AspectDistance_WrongShapeIsLarge()
        {
            double portrait = CoverAspect.AspectDistance(600, 900, TwoToThree);
            double landscape = CoverAspect.AspectDistance(1920, 1080, TwoToThree);

            Assert.Less(portrait, landscape);
        }

        [TestCase(0, 900)]
        [TestCase(600, 0)]
        public void AspectDistance_InvalidSizeIsWorstPossible(int w, int h)
        {
            Assert.AreEqual(double.MaxValue, CoverAspect.AspectDistance(w, h, TwoToThree));
        }

        // The core rule: correct shape beats more pixels.
        [Test]
        public void BestCover_PrefersCorrectShapeOverHigherResolution()
        {
            var artwork = new List<SteamGridDbArtwork>
            {
                Art(1, 3840, 2160),  // 16:9, far more pixels, wrong shape
                Art(2, 600, 900)     // 2:3, correct shape
            };

            SteamGridDbArtwork best = CoverAspect.BestCover(artwork, TwoToThree);

            Assert.AreEqual(2, best.Id);
        }

        // Among images of the same shape, the biggest should win.
        [Test]
        public void BestCover_AmongSameShapePrefersLargest()
        {
            var artwork = new List<SteamGridDbArtwork>
            {
                Art(1, 600, 900),
                Art(2, 1000, 1500),
                Art(3, 400, 600)
            };

            SteamGridDbArtwork best = CoverAspect.BestCover(artwork, TwoToThree);

            Assert.AreEqual(2, best.Id);
        }

        [Test]
        public void BestCover_IgnoresEntriesWithNoDimensions()
        {
            var artwork = new List<SteamGridDbArtwork>
            {
                Art(1, 0, 0),
                Art(2, 600, 900)
            };

            Assert.AreEqual(2, CoverAspect.BestCover(artwork, TwoToThree).Id);
        }

        [Test]
        public void BestCover_EmptyOrNullReturnsNull()
        {
            Assert.IsNull(CoverAspect.BestCover(null, TwoToThree));
            Assert.IsNull(CoverAspect.BestCover(new List<SteamGridDbArtwork>(), TwoToThree));
        }

        // SteamGridDB's grid category also carries Steam's old wide capsule
        // banners, which are crops of larger key art. Those are never box art,
        // so a landscape image must not win the cover slot even when it is the
        // only candidate - showing a stretched crop is worse than nothing.
        [Test]
        public void BestCover_RejectsLandscapeBannersEntirely()
        {
            var artwork = new List<SteamGridDbArtwork>
            {
                Art(1, 920, 430),
                Art(2, 460, 215),
                Art(3, 1920, 620)
            };

            Assert.IsNull(CoverAspect.BestCover(artwork, TwoToThree));
        }

        [Test]
        public void BestCover_PicksThePortraitOneAmongMixedShapes()
        {
            var artwork = new List<SteamGridDbArtwork>
            {
                Art(1, 1920, 620),   // wide banner, far more pixels
                Art(2, 600, 900)     // real box art
            };

            Assert.AreEqual(2, CoverAspect.BestCover(artwork, TwoToThree).Id);
        }

        // The portrait guard is tied to the target being portrait, so a theme
        // with square or landscape cover cards is unaffected by it.
        [Test]
        public void BestCover_LandscapeTargetStillAcceptsLandscapeArt()
        {
            var artwork = new List<SteamGridDbArtwork> { Art(1, 1920, 1080) };

            Assert.AreEqual(1, CoverAspect.BestCover(artwork, 16.0 / 9.0).Id);
        }

        // A theme using square cover cards should get square art chosen, not
        // the 2:3 default.
        [Test]
        public void BestCover_RespectsANonDefaultTargetAspect()
        {
            var artwork = new List<SteamGridDbArtwork>
            {
                Art(1, 600, 900),   // 2:3
                Art(2, 800, 800)    // 1:1
            };

            Assert.AreEqual(2, CoverAspect.BestCover(artwork, 1.0).Id);
            Assert.AreEqual(1, CoverAspect.BestCover(artwork, TwoToThree).Id);
        }

        [Test]
        public void OrderCoversByFit_PutsBestShapeFirst()
        {
            var paths = new List<string> { "wide.png", "tall.png" };

            var ordered = CoverAspect.OrderCoversByFit(
                paths,
                p => p == "tall.png"
                    ? System.Tuple.Create(600, 900)
                    : System.Tuple.Create(1920, 1080),
                TwoToThree);

            Assert.AreEqual("tall.png", ordered[0]);
        }

        [Test]
        public void OrderCoversByFit_NullInputsAreTolerated()
        {
            Assert.AreEqual(0, CoverAspect.OrderCoversByFit(null, p => null, TwoToThree).Count);

            var paths = new List<string> { "a.png" };
            CollectionAssert.AreEqual(paths, CoverAspect.OrderCoversByFit(paths, null, TwoToThree));
        }
    }
}
