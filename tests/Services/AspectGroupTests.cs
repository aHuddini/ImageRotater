using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ImageRotater.Models;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    [TestFixture]
    public class AspectGroupTests
    {
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
        public void Build_GroupsDimensionsSharingAnAspect()
        {
            var artwork = new List<SteamGridDbArtwork>
            {
                Art(1, 600, 900),
                Art(2, 600, 900),
                Art(3, 1024, 1024)
            };

            var groups = AspectGroup.Build(artwork);

            Assert.AreEqual(2, groups.Count);
        }

        [Test]
        public void Build_CountsEachDimension()
        {
            var artwork = new List<SteamGridDbArtwork>
            {
                Art(1, 600, 900),
                Art(2, 600, 900),
                Art(3, 600, 900)
            };

            var group = AspectGroup.Build(artwork).Single();

            Assert.AreEqual(1, group.Dimensions.Count);
            Assert.AreEqual(3, group.Dimensions[0].Count);
            Assert.AreEqual(3, group.TotalCount);
        }

        // The names come from SteamGridDB's own vocabulary so a user who knows
        // the site recognises them.
        [Test]
        public void Build_NamesKnownFormats()
        {
            Assert.AreEqual("1:1 - Square",
                AspectGroup.Build(new[] { Art(1, 1024, 1024) }).Single().Label);

            Assert.AreEqual("2:3 - Steam Vertical",
                AspectGroup.Build(new[] { Art(2, 600, 900) }).Single().Label);

            Assert.AreEqual("92:43 - Steam Horizontal",
                AspectGroup.Build(new[] { Art(3, 920, 430) }).Single().Label);

            Assert.AreEqual("96:31 - Steam Hero",
                AspectGroup.Build(new[] { Art(4, 1920, 620) }).Single().Label);
        }

        // 342x482 and 660x930 are both Galaxy 2.0 but not exactly equal ratios,
        // so the tolerance has to absorb the rounding.
        [Test]
        public void Build_MergesNearIdenticalRatiosIntoOneGroup()
        {
            var groups = AspectGroup.Build(new[] { Art(1, 342, 482), Art(2, 660, 930) });

            Assert.AreEqual(1, groups.Count);
            Assert.AreEqual(2, groups[0].Dimensions.Count);
        }

        // An unrecognised shape must still group rather than disappear.
        [Test]
        public void Build_UnknownShapeGetsAReducedRatioLabel()
        {
            var group = AspectGroup.Build(new[] { Art(1, 400, 500) }).Single();

            Assert.AreEqual("4:5", group.Label);
        }

        [Test]
        public void Build_LargestDimensionFirstWithinAGroup()
        {
            var group = AspectGroup.Build(new[] { Art(1, 600, 900), Art(2, 1000, 1500) }).Single();

            Assert.AreEqual("1000x1500", group.Dimensions[0].Dimensions);
        }

        [Test]
        public void Build_BiggestGroupFirst()
        {
            var artwork = new List<SteamGridDbArtwork>
            {
                Art(1, 1024, 1024),
                Art(2, 600, 900),
                Art(3, 600, 900),
                Art(4, 600, 900)
            };

            Assert.AreEqual("2:3 - Steam Vertical", AspectGroup.Build(artwork)[0].Label);
        }

        [Test]
        public void Build_IgnoresEntriesWithNoDimensions()
        {
            var groups = AspectGroup.Build(new[] { Art(1, 0, 0), Art(2, 600, 900) });

            Assert.AreEqual(1, groups.Count);
        }

        [Test]
        public void Build_EmptyOrNullReturnsNoGroups()
        {
            Assert.AreEqual(0, AspectGroup.Build(null).Count);
            Assert.AreEqual(0, AspectGroup.Build(new SteamGridDbArtwork[0]).Count);
        }
    }
}
