using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using NUnit.Framework;
using ImageRotater.Models;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    // The poster is what lets animated files live in the pool: the write path
    // is static by nature (Playnite decodes the stored value to one bitmap),
    // so an animated pick is written as a JPEG of its first frame while the
    // original stays for renderers that can animate. A wrong answer here
    // either writes a multi-megabyte GIF into Playnite's store per rotation,
    // or silently drops animated files from rotation entirely.
    [TestFixture]
    public class PosterFrameTests
    {
        private string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ImageRotaterPoster_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private string Gif(string name, int width = 64, int height = 48)
        {
            string path = Path.Combine(_dir, name + ".gif");
            using (var bmp = new Bitmap(width, height))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.CornflowerBlue);
                bmp.Save(path, ImageFormat.Gif);
            }

            return path;
        }

        private string Png(string name)
        {
            string path = Path.Combine(_dir, name + ".png");
            using (var bmp = new Bitmap(8, 8))
            {
                bmp.Save(path, ImageFormat.Png);
            }

            return path;
        }

        [Test]
        public void OnlyGifsCountAsAnimated()
        {
            Assert.IsTrue(PosterFrame.IsAnimated(Gif("a")));
            Assert.IsFalse(PosterFrame.IsAnimated(Png("b")));
            Assert.IsFalse(PosterFrame.IsAnimated(null));
        }

        [Test]
        public void StaticFiles_PassThroughUntouched()
        {
            string png = Png("still");
            Assert.AreEqual(png, PosterFrame.For(png),
                "a static pick needs no poster - the file itself is the write");
        }

        [Test]
        public void GifYieldsAJpegPosterOfItsFrame()
        {
            string gif = Gif("anim", 64, 48);

            string poster = PosterFrame.For(gif);

            Assert.IsNotNull(poster);
            Assert.AreNotEqual(gif, poster);
            StringAssert.EndsWith(".jpg", poster, "the write path gets a static JPEG");
            Assert.IsTrue(File.Exists(poster));

            using (var stream = new FileStream(poster, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (Image image = Image.FromStream(stream, false, false))
            {
                Assert.AreEqual(64, image.Width, "the poster carries the frame's dimensions");
                Assert.AreEqual(48, image.Height);
            }
        }

        [Test]
        public void OriginalGifIsNeverModified()
        {
            string gif = Gif("keep");
            byte[] before = File.ReadAllBytes(gif);

            PosterFrame.For(gif);

            CollectionAssert.AreEqual(before, File.ReadAllBytes(gif),
                "the animated original must stay byte-identical for renderers that animate");
        }

        // The poster lives in a dot-folder the candidate listing never sees -
        // otherwise the extracted frame would itself join the rotation.
        [Test]
        public void PosterIsInvisibleToTheCandidateList()
        {
            var store = new GameImageStore(Path.Combine(_dir, "store"));
            Guid gameId = Guid.NewGuid();

            string added = store.AddImage(gameId, Gif("anim"), ArtworkKind.Cover);
            string poster = PosterFrame.For(added);

            Assert.IsNotNull(poster, "precondition: a poster was produced");
            CollectionAssert.DoesNotContain(
                store.GetImagePaths(gameId, ArtworkKind.Cover), poster);
        }

        [Test]
        public void SecondCallReusesTheCachedPoster()
        {
            string gif = Gif("cached");

            string first = PosterFrame.For(gif);
            DateTime stamp = File.GetLastWriteTimeUtc(first);

            Assert.AreEqual(first, PosterFrame.For(gif));
            Assert.AreEqual(stamp, File.GetLastWriteTimeUtc(first),
                "an unchanged source must not re-extract");
        }

        [Test]
        public void UnreadableAnimatedFile_YieldsNullNotAThrow()
        {
            string junk = Path.Combine(_dir, "junk.gif");
            File.WriteAllBytes(junk, new byte[] { 1, 2, 3 });

            Assert.IsNull(PosterFrame.For(junk),
                "the caller falls back to another candidate; writing raw junk would be worse");
            Assert.IsNull(PosterFrame.For(Path.Combine(_dir, "missing.gif")));
        }
    }
}
