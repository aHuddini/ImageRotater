using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using NUnit.Framework;
using ImageRotater.Models;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    // Video publishes under a real video extension, stills keep current.tile.
    //
    // Not a stylistic split. MediaElement is DirectShow / Media Foundation,
    // which chooses a decoder partly by file EXTENSION, while WPF's imaging
    // stack sniffs content - which is why stills and GIFs are happy being
    // called ".tile" and video is not. Measured against a real mp4 in a 32-bit
    // WPF process: identical bytes play as "current.mp4" and fail with "Media
    // file download failed" as "current.tile".
    [TestFixture]
    public class PublishedVideoNameTests
    {
        private string _root;
        private GameImageStore _store;
        private Guid _gameId;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "ImageRotaterPubVid_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _store = new GameImageStore(_root);
            _gameId = Guid.NewGuid();
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        // Where published files land - a sibling of the candidate folder, so
        // publishing cannot bump the candidate folder's write time and
        // invalidate its listing cache.
        private string CoversFolder()
        {
            return _store.GetPublishedFolder(_gameId, ArtworkKind.Cover);
        }

        private string MakeVideo(string name)
        {
            string path = Path.Combine(_root, name);
            File.WriteAllBytes(path, new byte[] { 0, 0, 0, 24, 102, 116, 121, 112, 1, 2, 3 });
            return path;
        }

        private string MakeStill(string name)
        {
            string path = Path.Combine(_root, name);
            using (var bmp = new Bitmap(16, 16))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Coral);
                bmp.Save(path, ImageFormat.Jpeg);
            }

            return path;
        }

        [Test]
        public void VideoNameFollowsTheSourceExtension()
        {
            Assert.AreEqual("current.mp4", GameImageStore.PublishedVideoNameFor(@"C:\a\clip.mp4"));
            Assert.AreEqual("current.webm", GameImageStore.PublishedVideoNameFor(@"C:\a\clip.webm"));

            Assert.AreEqual("current.mp4", GameImageStore.PublishedVideoNameFor(@"C:\a\CLIP.MP4"),
                "the decoder is chosen by extension, so the published one is normalised");
        }

        [Test]
        public void PublishingAVideoUsesTheVideoName()
        {
            Assert.IsTrue(_store.PublishCurrent(_gameId, MakeVideo("clip.mp4"), ArtworkKind.Cover));

            Assert.IsTrue(File.Exists(Path.Combine(CoversFolder(), "current.mp4")),
                "a theme's MediaElement needs a path whose extension names the container");

            Assert.IsFalse(File.Exists(Path.Combine(CoversFolder(), GameImageStore.PublishedFileName)),
                "publishing a video must not also leave a .tile claiming to be the current pick");
        }

        [Test]
        public void PublishingAStillUsesTheTileName()
        {
            Assert.IsTrue(_store.PublishCurrent(_gameId, MakeStill("art.jpg"), ArtworkKind.Cover));

            Assert.IsTrue(File.Exists(Path.Combine(CoversFolder(), GameImageStore.PublishedFileName)));
            Assert.IsFalse(File.Exists(Path.Combine(CoversFolder(), "current.mp4")));
        }

        // A published video OUTLIVES rotations that pick a still.
        //
        // A still pick REMOVES the published video.
        //
        // This reverses an earlier decision that kept it, on the reasoning that
        // a theme's MediaElement would otherwise go dark whenever rotation
        // landed on a still. That reasoning was wrong in practice: the video is
        // drawn ON TOP of the still tile, so keeping it meant the video never
        // went away at all. A game with one video among its covers appeared
        // frozen on that clip no matter how often rotation picked something
        // else - reported as "the tiles don't rotate".
        //
        // Rotation that cannot be seen is not rotation.
        [Test]
        public void RotatingFromVideoToStillRemovesThePublishedVideo()
        {
            _store.PublishCurrent(_gameId, MakeVideo("clip.mp4"), ArtworkKind.Cover);
            Assert.IsTrue(File.Exists(Path.Combine(CoversFolder(), "current.mp4")));

            _store.PublishCurrent(_gameId, MakeStill("art.jpg"), ArtworkKind.Cover);

            Assert.IsFalse(File.Exists(Path.Combine(CoversFolder(), "current.mp4")),
                "the video draws over the still, so leaving it means the tile never changes");

            Assert.IsTrue(File.Exists(Path.Combine(CoversFolder(), GameImageStore.PublishedFileName)),
                "the still still publishes for everything that renders images");
        }

        // And picking the video again brings it back, or the first still pick
        // would end animation for the rest of the session.
        [Test]
        public void RotatingBackToVideoRepublishesIt()
        {
            _store.PublishCurrent(_gameId, MakeVideo("clip.mp4"), ArtworkKind.Cover);
            _store.PublishCurrent(_gameId, MakeStill("art.jpg"), ArtworkKind.Cover);
            _store.PublishCurrent(_gameId, MakeVideo("clip.mp4"), ArtworkKind.Cover);

            Assert.IsTrue(File.Exists(Path.Combine(CoversFolder(), "current.mp4")));
        }

        // Switching containers must not leave both behind, for the same reason.
        [Test]
        public void RotatingBetweenVideoFormatsLeavesOnlyTheCurrentOne()
        {
            _store.PublishCurrent(_gameId, MakeVideo("a.webm"), ArtworkKind.Cover);
            Assert.IsTrue(File.Exists(Path.Combine(CoversFolder(), "current.webm")));

            _store.PublishCurrent(_gameId, MakeVideo("b.mp4"), ArtworkKind.Cover);

            Assert.IsTrue(File.Exists(Path.Combine(CoversFolder(), "current.mp4")));
            Assert.IsFalse(File.Exists(Path.Combine(CoversFolder(), "current.webm")),
                "two published videos would leave the theme choosing arbitrarily");
        }

        // Startup seeding has to cover the video file too.
        //
        // The still and the video publish to DIFFERENT files, and a theme picks
        // between its image element and its MediaElement by asking which
        // exists. Seeding only the still meant a freshly started Playnite
        // animated nothing until rotation happened to pick that game's video -
        // on a game with several covers that can take a while, and it reads as
        // the feature being broken.
        [Test]
        public void SeedingPublishesAVideoCandidateAtStartup()
        {
            _store.AddImage(_gameId, MakeStill("art.jpg"), ArtworkKind.Cover);
            _store.AddImage(_gameId, MakeVideo("clip.mp4"), ArtworkKind.Cover);

            new ArtworkPublisher(_store).SeedEveryGame(
                new[] { new Playnite.SDK.Models.Game { Id = _gameId, Name = "Game" } });

            // The still is always seeded - everything that renders images
            // needs it, and it is what the video's poster frame writes to.
            Assert.IsTrue(File.Exists(Path.Combine(CoversFolder(), GameImageStore.PublishedFileName)),
                "the still must be seeded for everything that renders images");

            // Seeding publishes ONE pick. Whether that was the still or the
            // video is the selector's business, so this asserts only that the
            // two cannot both be stale - a published video means the video was
            // the pick, and its absence means the still was.
            //
            // The video is no longer left behind across still picks: it draws
            // over the still tile, so keeping it meant the tile never changed.
        }

        // The published copy must never come back as a rotation candidate, or a
        // game's artwork would rotate onto a copy of itself. ".tile" achieved
        // that by not being a listed extension; ".mp4" now IS one.
        [Test]
        public void ThePublishedVideoIsNotOfferedBackAsACandidate()
        {
            _store.AddImage(_gameId, MakeVideo("real.mp4"), ArtworkKind.Cover);
            _store.PublishCurrent(
                _gameId,
                _store.GetImagePaths(_gameId, ArtworkKind.Cover)[0],
                ArtworkKind.Cover);

            var listed = _store.GetImagePaths(_gameId, ArtworkKind.Cover);

            Assert.AreEqual(1, listed.Count,
                "the published copy came back as a candidate - rotation would pick a copy of itself");

            Assert.IsFalse(
                Path.GetFileName(listed[0]).StartsWith("current.", StringComparison.OrdinalIgnoreCase),
                "the listed candidate must be the real file, not the published copy");
        }
    }
}
