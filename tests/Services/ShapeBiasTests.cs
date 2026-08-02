using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using NUnit.Framework;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    // ShapeBias supplies the shape arithmetic the letterboxer decides with.
    // Its candidate FILTER was removed - it dominated rotation and hid
    // odd-shaped artwork entirely - so what remains under test is the header
    // reading, whose failure mode is a silent wrong decision about whether an
    // image needs letterboxing at all.
    [TestFixture]
    public class ShapeBiasTests
    {
        private string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ImageRotaterShape_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private string Png(string name, int width, int height)
        {
            string path = Path.Combine(_dir, name + ".png");
            using (var bmp = new Bitmap(width, height))
            {
                bmp.Save(path, ImageFormat.Png);
            }

            return path;
        }

        [Test]
        public void AspectOfReportsTheHeaderRatio()
        {
            Assert.AreEqual(3.1, ShapeBias.AspectOf(Png("u", 310, 100)), 0.01);
            Assert.AreEqual(1.78, ShapeBias.AspectOf(Png("w", 192, 108)), 0.01);
        }

        [Test]
        public void UnreadableAndMissingFilesReadAsNoAspect()
        {
            string junk = Path.Combine(_dir, "junk.png");
            File.WriteAllBytes(junk, new byte[] { 1, 2, 3 });

            Assert.AreEqual(0, ShapeBias.AspectOf(junk));
            Assert.AreEqual(0, ShapeBias.AspectOf(Path.Combine(_dir, "missing.png")));
            Assert.AreEqual(0, ShapeBias.AspectOf(null));
        }

        // Downloads overwrite files under the same name; a cached ratio for
        // replaced content would mis-judge letterboxing until restart.
        [Test]
        public void ReplacedFileReReadsItsAspect()
        {
            string path = Png("swap", 310, 100);
            Assert.AreEqual(3.1, ShapeBias.AspectOf(path), 0.01);

            using (var bmp = new Bitmap(192, 108))
            {
                bmp.Save(path, ImageFormat.Png);
            }

            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

            Assert.AreEqual(1.78, ShapeBias.AspectOf(path), 0.01,
                "a newer file must be re-read, not served from the cache");
        }
    }
}
