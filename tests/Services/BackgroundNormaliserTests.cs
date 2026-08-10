using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using NUnit.Framework;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    // Levelling background widths.
    //
    // Worth testing because the reason is invisible from the code alone:
    // Playnite blurs the window background with a FIXED radius applied after
    // the image is scaled to fit, and decodes every background to the screen's
    // working width. Two sources of different resolutions therefore end up
    // blurred by visibly different amounts, and rotating between them makes the
    // blur jump. Same resolution, no jump - which is exactly how the bug was
    // spotted.
    [TestFixture]
    public class BackgroundNormaliserTests
    {
        private string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ir-norm-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private string MakeImage(string name, int width, int height)
        {
            string path = Path.Combine(_dir, name);

            using (var bitmap = new Bitmap(width, height))
            {
                bitmap.Save(path, ImageFormat.Png);
            }

            return path;
        }

        private static int WidthOf(string path)
        {
            using (var image = Image.FromFile(path))
            {
                return image.Width;
            }
        }

        [Test]
        public void EveryCandidateEndsUpTheSameWidth()
        {
            // THE point of the whole class. Mixed resolutions in, one width out.
            var files = new List<string>
            {
                MakeImage("a.png", 3840, 1240),
                MakeImage("b.png", 1920, 620),
                MakeImage("c.png", 1438, 810)
            };

            int target = BackgroundNormaliser.TargetWidthFor(files, 1440);

            var widths = new HashSet<int>();

            foreach (string file in files)
            {
                string output = BackgroundNormaliser.NormaliseTo(
                    file, target, Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".png"));

                widths.Add(WidthOf(output));
            }

            Assert.AreEqual(1, widths.Count, "mixed widths are what makes the blur jump");
        }

        [Test]
        public void AspectRatioIsKept()
        {
            // Playnite stretches to fill, so changing the ratio here would crop
            // differently from the original.
            string source = MakeImage("wide.png", 3840, 1240);

            string output = BackgroundNormaliser.NormaliseTo(
                source, 1440, Path.Combine(_dir, "out.png"));

            using (var image = Image.FromFile(output))
            {
                Assert.AreEqual(1440, image.Width);
                Assert.AreEqual(465, image.Height, 1, "3840x1240 scaled to 1440 wide");
            }
        }

        [Test]
        public void TheTargetIsCappedAtTheScreenWidth()
        {
            // Decoding wider than the screen is wasted work: Playnite decodes
            // to the working width regardless.
            var files = new List<string> { MakeImage("huge.png", 3840, 1240) };

            Assert.AreEqual(1440, BackgroundNormaliser.TargetWidthFor(files, 1440));
        }

        [Test]
        public void SmallCandidatesAreNotUpscaledPastTheBest()
        {
            // The widest candidate sets the target, so nothing is invented -
            // upscaling to the screen width would just soften the image.
            var files = new List<string>
            {
                MakeImage("small.png", 1280, 720),
                MakeImage("mid.png", 1600, 900)
            };

            Assert.AreEqual(1600, BackgroundNormaliser.TargetWidthFor(files, 3840));
        }

        [Test]
        public void AnAlreadyCorrectImageIsNotReEncoded()
        {
            // Re-encoding costs quality for nothing. The source path coming
            // back is how the caller knows to skip the copy.
            string source = MakeImage("right.png", 1440, 810);

            Assert.AreEqual(
                source,
                BackgroundNormaliser.NormaliseTo(source, 1440, Path.Combine(_dir, "out.png")));
        }

        [Test]
        public void AnUnreadableFileIsPublishedUnchanged()
        {
            // A background that cannot be resized is still worth showing.
            string junk = Path.Combine(_dir, "notanimage.png");
            File.WriteAllText(junk, "definitely not a picture");

            Assert.AreEqual(
                junk,
                BackgroundNormaliser.NormaliseTo(junk, 1440, Path.Combine(_dir, "out.png")));
        }

        [Test]
        public void NoCandidatesMeansNoTarget()
        {
            Assert.AreEqual(0, BackgroundNormaliser.TargetWidthFor(new List<string>(), 1440));
        }
    }
}
