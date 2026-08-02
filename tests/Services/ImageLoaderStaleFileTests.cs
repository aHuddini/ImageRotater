using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    // Write mode replaces a game's artwork and deletes the file it replaced, so
    // a decoded bitmap can outlive its source. Serving that cached bitmap shows
    // artwork the user has effectively removed, and makes the caller report
    // success for a path that no longer resolves.
    [TestFixture]
    public class ImageLoaderStaleFileTests
    {
        private string _dir;
        private ImageCache _cache;
        private ImageLoader _loader;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ImageRotaterStale_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _cache = new ImageCache(64 * 1024 * 1024);
            _loader = new ImageLoader(_cache);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        // System.Drawing is used only to author a real fixture image; it must
        // never appear in src/.
        private string WriteJpeg(string name, int width, int height)
        {
            string path = Path.Combine(_dir, name);
            using (var bmp = new Bitmap(width, height))
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.SlateGray);
                bmp.Save(path, ImageFormat.Jpeg);
            }
            return path;
        }

        [Test]
        public async Task LoadAsync_FileDeletedAfterCaching_ReturnsNull()
        {
            string path = WriteJpeg("art.jpg", 400, 300);

            BitmapSourceAssertNotNull(await _loader.LoadAsync(path, 480));

            File.Delete(path);

            Assert.IsNull(
                await _loader.LoadAsync(path, 480),
                "a cached bitmap was served for a file that no longer exists");
        }

        [Test]
        public async Task LoadAsync_FileDeleted_DropsTheCacheEntry()
        {
            string path = WriteJpeg("art.jpg", 400, 300);

            await _loader.LoadAsync(path, 480);
            Assert.AreEqual(1, _cache.Count);

            File.Delete(path);
            await _loader.LoadAsync(path, 480);

            Assert.AreEqual(0, _cache.Count, "the stale entry was left in the cache");
        }

        // A path can be cached at several bucket widths; deleting the file must
        // clear all of them, not just the one that was asked for.
        [Test]
        public async Task LoadAsync_FileDeleted_DropsEveryBucketForThatPath()
        {
            string path = WriteJpeg("art.jpg", 2000, 1000);

            await _loader.LoadAsync(path, 480);
            await _loader.LoadAsync(path, 1920);
            Assert.AreEqual(2, _cache.Count);

            File.Delete(path);
            await _loader.LoadAsync(path, 480);

            Assert.AreEqual(0, _cache.Count, "another bucket's stale entry survived");
        }

        // Deleting one game's artwork must not evict another's.
        [Test]
        public async Task LoadAsync_FileDeleted_LeavesOtherPathsCached()
        {
            string kept = WriteJpeg("kept.jpg", 400, 300);
            string removed = WriteJpeg("removed.jpg", 400, 300);

            await _loader.LoadAsync(kept, 480);
            await _loader.LoadAsync(removed, 480);
            Assert.AreEqual(2, _cache.Count);

            File.Delete(removed);
            await _loader.LoadAsync(removed, 480);

            Assert.AreEqual(1, _cache.Count);
            Assert.IsNotNull(await _loader.LoadAsync(kept, 480));
        }

        private static void BitmapSourceAssertNotNull(object value)
        {
            Assert.IsNotNull(value, "the fixture image failed to load");
        }
    }
}
