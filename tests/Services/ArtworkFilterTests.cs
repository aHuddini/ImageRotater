using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ImageRotater.Models;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    [TestFixture]
    public class ArtworkFilterTests
    {
        private static SteamGridDbArtwork Art(
            int id,
            int width = 1920,
            int height = 1080,
            string style = "alternate",
            string mime = "image/jpeg",
            bool nsfw = false,
            bool humor = false,
            bool epilepsy = false)
        {
            return new SteamGridDbArtwork
            {
                Id = id,
                Url = "https://example.invalid/" + id,
                Width = width,
                Height = height,
                Style = style,
                Mime = mime,
                Nsfw = nsfw,
                Humor = humor,
                Epilepsy = epilepsy
            };
        }

        // The reported bug: filter options appeared twice. Deriving them from
        // the results makes duplication impossible, so this pins that property.
        [Test]
        public void AvailableDimensions_AreDistinct()
        {
            var artwork = new List<SteamGridDbArtwork>
            {
                Art(1, 1920, 1080),
                Art(2, 1920, 1080),
                Art(3, 3840, 2160),
                Art(4, 1920, 1080)
            };

            var dimensions = ArtworkFilter.AvailableDimensions(artwork);

            Assert.AreEqual(2, dimensions.Count);
            CollectionAssert.AllItemsAreUnique(dimensions);
        }

        [Test]
        public void AvailableDimensions_LargestFirst()
        {
            var artwork = new List<SteamGridDbArtwork>
            {
                Art(1, 1280, 720),
                Art(2, 3840, 2160),
                Art(3, 1920, 1080)
            };

            var dimensions = ArtworkFilter.AvailableDimensions(artwork);

            CollectionAssert.AreEqual(new[] { "3840x2160", "1920x1080", "1280x720" }, dimensions);
        }

        [Test]
        public void AvailableStyles_AreDistinctAndSorted()
        {
            var artwork = new List<SteamGridDbArtwork>
            {
                Art(1, style: "material"),
                Art(2, style: "alternate"),
                Art(3, style: "material"),
                Art(4, style: null)
            };

            var styles = ArtworkFilter.AvailableStyles(artwork);

            CollectionAssert.AreEqual(new[] { "alternate", "material" }, styles);
        }

        [Test]
        public void AvailableOptions_HandleNullInput()
        {
            Assert.AreEqual(0, ArtworkFilter.AvailableDimensions(null).Count);
            Assert.AreEqual(0, ArtworkFilter.AvailableStyles(null).Count);
        }

        // An untouched filter must not hide everything - that would look like a
        // failed search.
        [Test]
        public void Apply_EmptySelections_ImposeNoConstraint()
        {
            var artwork = new List<SteamGridDbArtwork> { Art(1), Art(2, 3840, 2160, "material") };

            var result = ArtworkFilter.Apply(artwork, new ArtworkFilterState());

            Assert.AreEqual(2, result.Count);
        }

        [Test]
        public void Apply_FiltersByDimension()
        {
            var artwork = new List<SteamGridDbArtwork>
            {
                Art(1, 1920, 1080),
                Art(2, 3840, 2160)
            };

            var filter = new ArtworkFilterState();
            filter.Dimensions.Add("3840x2160");

            var result = ArtworkFilter.Apply(artwork, filter);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(2, result[0].Id);
        }

        [Test]
        public void Apply_FiltersByStyle()
        {
            var artwork = new List<SteamGridDbArtwork>
            {
                Art(1, style: "alternate"),
                Art(2, style: "material")
            };

            var filter = new ArtworkFilterState();
            filter.Styles.Add("material");

            var result = ArtworkFilter.Apply(artwork, filter);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(2, result[0].Id);
        }

        // Content flags are opt-in: hidden by default, shown only on request.
        [Test]
        public void Apply_HidesFlaggedContentByDefault()
        {
            var artwork = new List<SteamGridDbArtwork>
            {
                Art(1),
                Art(2, nsfw: true),
                Art(3, humor: true),
                Art(4, epilepsy: true)
            };

            var result = ArtworkFilter.Apply(artwork, new ArtworkFilterState());

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(1, result[0].Id);
        }

        [Test]
        public void Apply_ShowsFlaggedContentWhenRequested()
        {
            var artwork = new List<SteamGridDbArtwork> { Art(1, nsfw: true) };

            var filter = new ArtworkFilterState { ShowNsfw = true };

            Assert.AreEqual(1, ArtworkFilter.Apply(artwork, filter).Count);
        }

        // Animated formats cannot be rendered yet, so downloading one would
        // produce a broken-image placeholder.
        [Test]
        public void Apply_HidesAnimatedByDefault()
        {
            var artwork = new List<SteamGridDbArtwork>
            {
                Art(1, mime: "image/jpeg"),
                Art(2, mime: "image/webp"),
                Art(3, mime: "image/apng")
            };

            var result = ArtworkFilter.Apply(artwork, new ArtworkFilterState());

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(1, result[0].Id);
        }

        [Test]
        public void Apply_CombinesFiltersAsAnd()
        {
            var artwork = new List<SteamGridDbArtwork>
            {
                Art(1, 3840, 2160, "material"),
                Art(2, 3840, 2160, "alternate"),
                Art(3, 1920, 1080, "material")
            };

            var filter = new ArtworkFilterState();
            filter.Dimensions.Add("3840x2160");
            filter.Styles.Add("material");

            var result = ArtworkFilter.Apply(artwork, filter);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(1, result[0].Id);
        }

        [Test]
        public void Apply_NullInputsAreTolerated()
        {
            Assert.AreEqual(0, ArtworkFilter.Apply(null, new ArtworkFilterState()).Count);

            var artwork = new List<SteamGridDbArtwork> { Art(1) };
            Assert.AreEqual(1, ArtworkFilter.Apply(artwork, null).Count);
        }

        [Test]
        public void Dimensions_FormatAsWidthByHeight()
        {
            Assert.AreEqual("1920x1080", Art(1, 1920, 1080).Dimensions);
        }
    }
}
