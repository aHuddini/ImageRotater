using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using NUnit.Framework;
using ImageRotater.Models;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    // The letterboxer's job: hand rotation a screen-shaped path for a pick
    // that is not, without ever modifying the source file. Failures must fall
    // back to the original path - a missing background is worse than the
    // re-fit flash this exists to prevent.
    [TestFixture]
    public class LetterboxerTests
    {
        private string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ImageRotaterLbx_" + Guid.NewGuid().ToString("N"));
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

        private static Size SizeOf(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (Image image = Image.FromStream(stream, false, false))
            {
                return new Size(image.Width, image.Height);
            }
        }

        private const double Screen16x9 = 16.0 / 9.0;

        [Test]
        public void OddShapedPick_GetsAScreenShapedComposite()
        {
            string ultra = Png("ultra", 310, 100);

            string result = Letterboxer.For(ultra, Screen16x9);

            Assert.AreNotEqual(ultra, result, "an ultrawide pick should be composited");
            Assert.IsTrue(File.Exists(result));

            Size size = SizeOf(result);
            Assert.AreEqual(Screen16x9, (double)size.Width / size.Height, 0.02,
                "the composite must be screen-shaped - that is its entire purpose");

            // The source fits inside at native scale: nothing was downscaled.
            Assert.GreaterOrEqual(size.Width, 310);
            Assert.GreaterOrEqual(size.Height, 100);
        }

        [Test]
        public void ScreenShapedPick_PassesThroughUntouched()
        {
            string wide = Png("wide", 192, 108);

            Assert.AreEqual(wide, Letterboxer.For(wide, Screen16x9),
                "already screen-shaped - bars would be pure loss");
        }

        [Test]
        public void SourceFileIsNeverModified()
        {
            string ultra = Png("ultra", 310, 100);
            byte[] before = File.ReadAllBytes(ultra);

            Letterboxer.For(ultra, Screen16x9);

            CollectionAssert.AreEqual(before, File.ReadAllBytes(ultra),
                "letterboxing writes a composite; the user's file must stay byte-identical");
        }

        // The composite lives where GetImagePaths cannot list it, so it can
        // never become a rotation candidate and be composited again.
        [Test]
        public void CompositeIsInvisibleToTheCandidateList()
        {
            var store = new GameImageStore(Path.Combine(_dir, "store"));
            Guid gameId = Guid.NewGuid();

            string added = store.AddImage(gameId, Png("ultra", 310, 100), ArtworkKind.Background);
            string composite = Letterboxer.For(added, Screen16x9);

            Assert.AreNotEqual(added, composite, "precondition: a composite was created");
            CollectionAssert.DoesNotContain(
                store.GetImagePaths(gameId, ArtworkKind.Background), composite,
                "a composite offered back as a candidate would be letterboxed again");
        }

        [Test]
        public void SecondCallReusesTheCachedComposite()
        {
            string ultra = Png("ultra", 310, 100);

            string first = Letterboxer.For(ultra, Screen16x9);
            DateTime stamp = File.GetLastWriteTimeUtc(first);

            string second = Letterboxer.For(ultra, Screen16x9);

            Assert.AreEqual(first, second);
            Assert.AreEqual(stamp, File.GetLastWriteTimeUtc(first),
                "an unchanged source must not trigger a re-compose");
        }

        // Downloads overwrite files under the same name; a stale composite
        // would keep resurrecting the replaced artwork.
        [Test]
        public void NewerSource_ForcesARecompose()
        {
            string ultra = Png("ultra", 310, 100);
            string first = Letterboxer.For(ultra, Screen16x9);
            long firstLength = new FileInfo(first).Length;

            // Replace the source with different content, stamped newer.
            using (var bmp = new Bitmap(620, 200))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Red);
                bmp.Save(ultra, ImageFormat.Png);
            }

            File.SetLastWriteTimeUtc(ultra, DateTime.UtcNow.AddMinutes(1));

            string second = Letterboxer.For(ultra, Screen16x9);

            Assert.AreEqual(first, second, "same cache slot");
            Assert.AreNotEqual(firstLength, new FileInfo(second).Length,
                "the composite must be rebuilt from the new content");
        }

        [Test]
        public void UnreadableOrMissingSources_FallBackToTheOriginalPath()
        {
            string junk = Path.Combine(_dir, "junk.png");
            File.WriteAllBytes(junk, new byte[] { 1, 2, 3 });

            Assert.AreEqual(junk, Letterboxer.For(junk, Screen16x9),
                "an unreadable pick rotates as-is rather than vanishing");

            string missing = Path.Combine(_dir, "missing.png");
            Assert.AreEqual(missing, Letterboxer.For(missing, Screen16x9));
            Assert.IsNull(Letterboxer.For(null, Screen16x9));
        }
    }
}
