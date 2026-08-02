using System;
using NUnit.Framework;
using Playnite.SDK.Models;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    [TestFixture]
    public class PlayniteImageSourceTests
    {
        [Test]
        public void GetImagePaths_NullGame_ReturnsEmpty()
        {
            var source = new PlayniteImageSource(id => @"C:\resolved\" + id);
            Assert.AreEqual(0, source.GetImagePaths(null).Count);
        }

        // The common case: most libraries have games with no background set.
        // This must be empty, not an error.
        [Test]
        public void GetImagePaths_NoBackgroundImage_ReturnsEmpty()
        {
            var source = new PlayniteImageSource(id => @"C:\resolved\" + id);
            var game = new Game { BackgroundImage = null };

            Assert.AreEqual(0, source.GetImagePaths(game).Count);
        }

        [Test]
        public void GetImagePaths_EmptyBackgroundImage_ReturnsEmpty()
        {
            var source = new PlayniteImageSource(id => @"C:\resolved\" + id);
            var game = new Game { BackgroundImage = string.Empty };

            Assert.AreEqual(0, source.GetImagePaths(game).Count);
        }

        // BackgroundImage is a database ID, not a path. It must be resolved -
        // and the resolved file must actually exist.
        [Test]
        public void GetImagePaths_ResolvesIdToFullPath()
        {
            string temp = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "ImageRotaterSrc_" + Guid.NewGuid().ToString("N") + ".png");
            System.IO.File.WriteAllBytes(temp, new byte[] { 1, 2, 3 });

            try
            {
                var source = new PlayniteImageSource(id => temp);
                var game = new Game { BackgroundImage = "abc123" };

                var paths = source.GetImagePaths(game);

                Assert.AreEqual(1, paths.Count);
                Assert.AreEqual(temp, paths[0]);
            }
            finally
            {
                try { System.IO.File.Delete(temp); } catch { }
            }
        }

        // Write mode replaces game.BackgroundImage and deletes the file it
        // replaced, so the old id keeps resolving to a path that no longer
        // exists. Without this check that dangling entry stays in the rotation
        // pool and gets picked, showing a missing image.
        [Test]
        public void GetImagePaths_ResolvedFileDoesNotExist_ReturnsEmpty()
        {
            string gone = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "ImageRotaterGone_" + Guid.NewGuid().ToString("N") + ".png");

            var source = new PlayniteImageSource(id => gone);
            var game = new Game { BackgroundImage = "abc123" };

            Assert.AreEqual(0, source.GetImagePaths(game).Count);
        }

        [Test]
        public void GetImagePaths_ResolverReturnsNull_ReturnsEmpty()
        {
            var source = new PlayniteImageSource(id => null);
            var game = new Game { BackgroundImage = "abc123" };

            Assert.AreEqual(0, source.GetImagePaths(game).Count);
        }

        // A throwing resolver must not take the control down.
        [Test]
        public void GetImagePaths_ResolverThrows_ReturnsEmpty()
        {
            var source = new PlayniteImageSource(id => { throw new InvalidOperationException("boom"); });
            var game = new Game { BackgroundImage = "abc123" };

            Assert.AreEqual(0, source.GetImagePaths(game).Count);
        }

        [Test]
        public void GetImagePaths_NullResolver_ReturnsEmpty()
        {
            var source = new PlayniteImageSource(null);
            var game = new Game { BackgroundImage = "abc123" };

            Assert.AreEqual(0, source.GetImagePaths(game).Count);
        }
    }
}
