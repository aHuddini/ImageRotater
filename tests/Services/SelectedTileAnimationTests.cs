using System;
using NUnit.Framework;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    // Only the selected tile animates.
    //
    // A Fullscreen grid realises a screenful of cover controls at once, and
    // every one holding animated artwork decodes frames continuously on the UI
    // thread - in a 32-bit process shared with Chromium. Dozens of simultaneous
    // decoders is the same pressure that took Playnite down when a theme put
    // its own MediaElement in each tile, and a wall of moving thumbnails reads
    // worse than a single one anyway.
    //
    // The control itself needs a live WPF tree, so what is covered here is the
    // decision it makes: given a pick and whether this tile is selected, does
    // it animate or render a still?
    [TestFixture]
    public class SelectedTileAnimationTests
    {
        // Mirrors the branch in CoverImageControl.Refresh.
        private static bool WouldAnimate(string path, bool isSelectedTile)
        {
            if (PosterFrame.IsMotion(path) && !isSelectedTile)
            {
                return false;
            }

            return PosterFrame.IsMotion(path);
        }

        [Test]
        public void TheSelectedTileAnimatesMotionArtwork()
        {
            Assert.IsTrue(WouldAnimate(@"C:\a\cover.gif", true));
            Assert.IsTrue(WouldAnimate(@"C:\a\cover.mp4", true));
            Assert.IsTrue(WouldAnimate(@"C:\a\cover.webm", true));
        }

        [Test]
        public void UnselectedTilesDoNotAnimate()
        {
            Assert.IsFalse(WouldAnimate(@"C:\a\cover.gif", false),
                "a grid of animating tiles is dozens of decoders on the UI thread");

            Assert.IsFalse(WouldAnimate(@"C:\a\cover.mp4", false));
        }

        // Stills are unaffected either way - there is nothing to stand down.
        [Test]
        public void StillArtworkIsUnaffectedBySelection()
        {
            Assert.IsFalse(WouldAnimate(@"C:\a\cover.jpg", true));
            Assert.IsFalse(WouldAnimate(@"C:\a\cover.jpg", false));
            Assert.IsFalse(WouldAnimate(@"C:\a\cover.png", false));
        }

        // An unselected tile falls back to the motion file's own still frame,
        // so it shows the right artwork rather than nothing. GDI+ can extract
        // one from a GIF; it cannot open a video container, which is why the
        // video branch has to tolerate null.
        [Test]
        public void AGifHasAStillToFallBackTo()
        {
            Assert.IsTrue(PosterFrame.IsAnimated(@"C:\a\cover.gif"),
                "IsAnimated means a still frame can be extracted - that is what the "
                + "unselected tile renders");
        }

        [Test]
        public void VideoHasNoExtractableStill()
        {
            Assert.IsFalse(PosterFrame.IsAnimated(@"C:\a\cover.mp4"),
                "GDI+ cannot open a container, so an unselected tile showing video "
                + "renders nothing rather than starting playback");

            Assert.IsTrue(PosterFrame.IsVideo(@"C:\a\cover.mp4"));
        }
    }
}
