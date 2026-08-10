using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using NUnit.Framework;
using Playnite.SDK.Models;
using ImageRotater.Models;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    // Video artwork: playable by the controls, invisible to the database write.
    //
    // Video differs from a GIF in one way that matters everywhere: no still
    // frame can be pulled out of it. PosterFrame.Extract is GDI+, which cannot
    // decode a container at all, so "animated" and "video" cannot be the same
    // predicate. Game.BackgroundImage / Game.CoverImage decode to a single
    // bitmap, so a video must never be written there - while a theme, which
    // renders the published file itself, must still receive it.
    [TestFixture]
    public class VideoArtworkTests
    {
        private string _root;
        private GameImageStore _store;
        private Game _game;

        private class StubSource : IBackgroundImageSource
        {
            private readonly IReadOnlyList<string> _paths;
            public StubSource(IReadOnlyList<string> paths) { _paths = paths; }
            public IReadOnlyList<string> GetImagePaths(Game game) { return _paths; }
        }

        private class RecordingWriter : PlayniteBackgroundWriter
        {
            public readonly List<string> Written = new List<string>();

            public RecordingWriter(string dir) : base(null, dir) { }

            public override bool SetArtwork(Game game, string imagePath, ArtworkKind kind)
            {
                Written.Add(imagePath);
                return true;
            }
        }

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "ImageRotaterVideo_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);

            _store = new GameImageStore(_root);
            _game = new Game { Id = Guid.NewGuid(), Name = "Game" };
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        // Content is irrelevant to the routing under test - that is by
        // extension - but each file must have DISTINCT bytes, or the store's
        // content deduplication correctly collapses them into one candidate.
        private int _videoSeed;

        private string AddVideo(string name, ArtworkKind kind = ArtworkKind.Background)
        {
            string path = Path.Combine(_root, name);
            File.WriteAllBytes(path, new byte[] { 0, 0, 0, 24, 102, 116, 121, 112, (byte)(++_videoSeed) });
            _store.AddImage(_game.Id, path, kind);
            return path;
        }

        private string AddStill(string name, ArtworkKind kind = ArtworkKind.Background)
        {
            string path = Path.Combine(_root, name);
            using (var bmp = new Bitmap(32, 24))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.SeaGreen);
                bmp.Save(path, ImageFormat.Jpeg);
            }

            _store.AddImage(_game.Id, path, kind);
            return path;
        }

        private static ImageRotaterSettings Settings()
        {
            return new ImageRotaterSettings
            {
                EnableRotation = true,
                RotateCovers = true,
                SelectionMode = SelectionMode.Fixed,
                CoverSelectionMode = SelectionMode.Fixed,
                LetterboxBackgrounds = false
            };
        }

        private RecordingWriter Rotate(ArtworkKind kind, ImageRotaterSettings settings = null)
        {
            IReadOnlyList<string> paths = _store.GetImagePaths(_game.Id, kind);
            var writer = new RecordingWriter(_root);

            var service = new BackgroundRotationService(
                kind == ArtworkKind.Background ? new StubSource(paths) : new StubSource(new string[0]),
                kind == ArtworkKind.Cover ? new StubSource(paths) : new StubSource(new string[0]),
                new ImageSelector(new ImagePicker(), new SessionSelectionCache()),
                writer, null, _store, () => settings ?? Settings(), null,
                new ArtworkPublisher(_store));

            service.ApplyTo(_game, kind);
            return writer;
        }

        private string PublishedFolder(ArtworkKind kind)
        {
            return _store.GetPublishedFolder(_game.Id, kind);
        }

        // Video publishes under a real video extension rather than the .tile
        // name stills use, because MediaElement picks its decoder by extension.
        // See PublishedVideoNameTests.
        private string PublishedVideoPath(ArtworkKind kind, string ext = ".mp4")
        {
            return Path.Combine(PublishedFolder(kind), GameImageStore.PublishedVideoBaseName + ext);
        }

        [Test]
        public void VideoIsMotionButNotAnimated()
        {
            foreach (string v in new[] { @"C:\a\clip.mp4", @"C:\a\clip.webm", @"C:\a\CLIP.MP4" })
            {
                Assert.IsTrue(PosterFrame.IsVideo(v), $"{v} should be video");
                Assert.IsTrue(PosterFrame.IsMotion(v), $"{v} should count as motion");

                Assert.IsFalse(PosterFrame.IsAnimated(v),
                    $"{v} must not be 'animated' - that predicate promises an extractable "
                    + "still frame, and GDI+ cannot decode a video container");
            }

            Assert.IsTrue(PosterFrame.IsMotion(@"C:\a\clip.gif"), "a GIF is still motion");
            Assert.IsFalse(PosterFrame.IsVideo(@"C:\a\clip.gif"));
            Assert.IsFalse(PosterFrame.IsMotion(@"C:\a\clip.jpg"));
        }

        // Asking for a poster must fail cleanly rather than throwing out of
        // GDI+, because every caller treats null as "pick something else".
        [Test]
        public void VideoHasNoPoster()
        {
            string video = AddVideo("clip.mp4");

            Assert.IsNull(PosterFrame.For(video),
                "a video has no extractable still, and callers rely on null to fall back");
        }

        // Video has to reach the store's listing, or nothing can ever pick it.
        [Test]
        public void VideoIsListedAsACandidate()
        {
            AddVideo("clip.mp4");
            AddVideo("clip2.webm");

            IReadOnlyList<string> listed = _store.GetImagePaths(_game.Id, ArtworkKind.Background);

            Assert.AreEqual(2, listed.Count, "video must be offered to rotation like any other artwork");
        }

        // The core rule: Playnite's database decodes to one bitmap, so it must
        // receive the still - never the video.
        [Test]
        public void VideoPickIsNeverWrittenToPlaynite()
        {
            AddVideo("aaa_clip.mp4");   // sorts first, so Fixed mode picks it
            AddStill("zzz_still.jpg");

            RecordingWriter writer = Rotate(ArtworkKind.Background);

            foreach (string written in writer.Written)
            {
                Assert.IsFalse(PosterFrame.IsMotion(written),
                    $"wrote {Path.GetFileName(written)} to Playnite, which decodes it to a "
                    + "single bitmap and can only show a blank");
            }
        }

        // ...but a theme still gets it, because a theme renders the file itself.
        [Test]
        public void VideoPickIsStillPublishedForThemes()
        {
            AddVideo("aaa_clip.mp4");
            AddStill("zzz_still.jpg");

            Rotate(ArtworkKind.Background);

            string published = PublishedVideoPath(ArtworkKind.Background);
            Assert.IsTrue(File.Exists(published), "the theme-facing file must exist");

            // Published bytes are the video's, not the still's.
            IReadOnlyList<string> listed = _store.GetImagePaths(_game.Id, ArtworkKind.Background);
            string storedVideo = null;
            foreach (string p in listed)
            {
                if (PosterFrame.IsVideo(p)) { storedVideo = p; }
            }

            Assert.IsNotNull(storedVideo);
            CollectionAssert.AreEqual(
                File.ReadAllBytes(storedVideo), File.ReadAllBytes(published),
                "the theme renders this file itself, so it must be the video");
        }

        // A game whose ONLY artwork is a video must not silently rotate to
        // nothing: there is no still to write, but the theme path still works.
        [Test]
        public void VideoOnlyGameStillPublishes()
        {
            AddVideo("only.mp4");

            RecordingWriter writer = Rotate(ArtworkKind.Background);

            Assert.IsEmpty(writer.Written,
                "there is no still to write, so the database must be left alone");

            Assert.IsTrue(File.Exists(PublishedVideoPath(ArtworkKind.Background)),
                "the theme-facing file is the whole point for a video-only game - "
                + "returning early without publishing would strand it");
        }

        [Test]
        public void CoversFollowTheSameRule()
        {
            AddVideo("aaa_clip.webm", ArtworkKind.Cover);
            AddStill("zzz_still.jpg", ArtworkKind.Cover);

            RecordingWriter writer = Rotate(ArtworkKind.Cover);

            foreach (string written in writer.Written)
            {
                Assert.IsFalse(PosterFrame.IsMotion(written), "covers decode to one bitmap too");
            }

            Assert.IsTrue(File.Exists(PublishedVideoPath(ArtworkKind.Cover, ".webm")));
        }
    }
}
