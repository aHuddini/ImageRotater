using System.Collections.Generic;
using NUnit.Framework;
using Playnite.SDK.Models;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    [TestFixture]
    public class MergedImageSourceTests
    {
        // Minimal stand-in so the merge logic can be tested without disk or a
        // running Playnite.
        private class StubSource : IBackgroundImageSource
        {
            private readonly IReadOnlyList<string> _paths;

            public StubSource(params string[] paths)
            {
                _paths = paths;
            }

            public IReadOnlyList<string> GetImagePaths(Game game)
            {
                return _paths;
            }
        }

        private static Game AnyGame()
        {
            return new Game { Name = "Test" };
        }

        [Test]
        public void Merges_InSourceOrder()
        {
            var merged = new MergedImageSource(
                new StubSource("a.jpg", "b.jpg"),
                new StubSource("c.jpg"));

            CollectionAssert.AreEqual(
                new[] { "a.jpg", "b.jpg", "c.jpg" },
                merged.GetImagePaths(AnyGame()));
        }

        // A duplicate would otherwise be offered twice and skew the random pick
        // toward it.
        [Test]
        public void Deduplicates_AcrossSources()
        {
            var merged = new MergedImageSource(
                new StubSource("a.jpg", "b.jpg"),
                new StubSource("b.jpg", "c.jpg"));

            CollectionAssert.AreEqual(
                new[] { "a.jpg", "b.jpg", "c.jpg" },
                merged.GetImagePaths(AnyGame()));
        }

        [Test]
        public void Deduplicates_CaseInsensitively()
        {
            var merged = new MergedImageSource(
                new StubSource(@"C:\Art\A.jpg"),
                new StubSource(@"c:\art\a.jpg"));

            Assert.AreEqual(1, merged.GetImagePaths(AnyGame()).Count);
        }

        [Test]
        public void EmptyAndNullSources_AreTolerated()
        {
            var merged = new MergedImageSource(
                null,
                new StubSource(),
                new StubSource("a.jpg"));

            CollectionAssert.AreEqual(new[] { "a.jpg" }, merged.GetImagePaths(AnyGame()));
        }

        [Test]
        public void NoSources_ReturnsEmpty()
        {
            Assert.AreEqual(0, new MergedImageSource().GetImagePaths(AnyGame()).Count);
        }

        [Test]
        public void NullGame_ReturnsEmpty()
        {
            var merged = new MergedImageSource(new StubSource("a.jpg"));
            Assert.AreEqual(0, merged.GetImagePaths(null).Count);
        }
    }
}
