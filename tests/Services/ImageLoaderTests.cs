using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    [TestFixture]
    public class ImageLoaderTests
    {
        private string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ImageRotaterTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        // System.Drawing is used ONLY here, to author test fixtures. It must not
        // appear in src/ — see the plan's Global Constraints.
        private string WriteJpeg(string name, int width, int height)
        {
            string path = Path.Combine(_dir, name);
            using (var bmp = new Bitmap(width, height))
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.CornflowerBlue);
                bmp.Save(path, ImageFormat.Jpeg);
            }
            return path;
        }

        [Test]
        public async Task LoadAsync_DecodesToRequestedBucketWidth()
        {
            string path = WriteJpeg("big.jpg", 3840, 2160);
            var loader = new ImageLoader(new ImageCache(64 * 1024 * 1024));

            var bmp = await loader.LoadAsync(path, 960);

            Assert.IsNotNull(bmp);
            // This is the whole point of the design: a 3840px source must not
            // occupy 3840px of memory when displayed at 960.
            Assert.AreEqual(960, bmp.PixelWidth);
        }

        [Test]
        public async Task LoadAsync_ResultIsFrozen()
        {
            string path = WriteJpeg("a.jpg", 200, 100);
            var loader = new ImageLoader(new ImageCache(64 * 1024 * 1024));

            var bmp = await loader.LoadAsync(path, 480);

            Assert.IsNotNull(bmp);
            Assert.IsTrue(bmp.IsFrozen, "bitmap must be frozen to cross threads safely");
        }

        [Test]
        public async Task LoadAsync_MissingFile_ReturnsNullWithoutThrowing()
        {
            var loader = new ImageLoader(new ImageCache(64 * 1024 * 1024));

            var bmp = await loader.LoadAsync(Path.Combine(_dir, "does-not-exist.jpg"), 480);

            Assert.IsNull(bmp);
        }

        [Test]
        public async Task LoadAsync_CorruptFile_ReturnsNullWithoutThrowing()
        {
            string path = Path.Combine(_dir, "corrupt.jpg");
            File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            var loader = new ImageLoader(new ImageCache(64 * 1024 * 1024));

            var bmp = await loader.LoadAsync(path, 480);

            Assert.IsNull(bmp);
        }

        [Test]
        public async Task LoadAsync_NullOrEmptyPath_ReturnsNull()
        {
            var loader = new ImageLoader(new ImageCache(64 * 1024 * 1024));

            Assert.IsNull(await loader.LoadAsync(null, 480));
            Assert.IsNull(await loader.LoadAsync(string.Empty, 480));
        }

        [Test]
        public async Task LoadAsync_SecondCallForSameKey_ReturnsCachedInstance()
        {
            string path = WriteJpeg("a.jpg", 800, 600);
            var loader = new ImageLoader(new ImageCache(64 * 1024 * 1024));

            var first = await loader.LoadAsync(path, 960);
            var second = await loader.LoadAsync(path, 960);

            Assert.AreSame(first, second, "second load should come from cache");
        }

        [Test]
        public async Task LoadAsync_DifferentBuckets_ProduceDifferentBitmaps()
        {
            string path = WriteJpeg("a.jpg", 2000, 1000);
            var loader = new ImageLoader(new ImageCache(64 * 1024 * 1024));

            var small = await loader.LoadAsync(path, 480);
            var large = await loader.LoadAsync(path, 1920);

            Assert.AreNotSame(small, large);
            Assert.AreEqual(480, small.PixelWidth);
            Assert.AreEqual(1920, large.PixelWidth);
        }

        // Decoding must not hold the file open, or the user cannot delete or
        // replace their own images while Playnite runs.
        [Test]
        public async Task LoadAsync_DoesNotHoldFileHandle()
        {
            string path = WriteJpeg("a.jpg", 400, 300);
            var loader = new ImageLoader(new ImageCache(64 * 1024 * 1024));

            await loader.LoadAsync(path, 480);

            Assert.DoesNotThrow(() => File.Delete(path), "file was still locked after decode");
        }
    }
}
