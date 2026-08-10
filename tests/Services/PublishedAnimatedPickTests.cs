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
    // What a theme receives when the rotation picks an animated file.
    //
    // Two consumers, two different needs, from the same pick:
    //
    //   Playnite's database gets a STILL. Game.CoverImage decodes to a single
    //   bitmap, so an animated file would show frame one anyway while importing
    //   megabytes per rotation. The poster frame is correct there.
    //
    //   A theme gets the REAL FILE. current.tile is a verbatim copy that the
    //   theme renders itself, and the plugin's own controls animate it.
    //
    // Publishing was added to the method that already performed the poster
    // substitution and simply inherited the substituted path, so the animated
    // original never reached a theme. That also made a two-candidate game look
    // like it had stopped rotating: the tile alternated between the other
    // still and a poster OF THE ANIMATION, which are visually identical.
    [TestFixture]
    public class PublishedAnimatedPickTests
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

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "ImageRotaterPubAnim_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);

            _store = new GameImageStore(_root);
            _game = new Game { Id = Guid.NewGuid(), Name = "Game" };
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        // A real GIF, so PosterFrame can actually decode a first frame from it.
        private string AddGif(string name)
        {
            string path = Path.Combine(_root, name + ".gif");
            using (var bmp = new Bitmap(64, 48))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Firebrick);
                bmp.Save(path, ImageFormat.Gif);
            }

            _store.AddImage(_game.Id, path, ArtworkKind.Cover);
            return path;
        }

        private string PublishedPath()
        {
            return Path.Combine(
                _store.GetPublishedFolder(_game.Id, ArtworkKind.Cover),
                GameImageStore.PublishedFileName);
        }

        private static ImageRotaterSettings Settings()
        {
            return new ImageRotaterSettings
            {
                EnableRotation = true,
                RotateCovers = true,
                UseCoverControl = true,
                SelectionMode = SelectionMode.EverySelection,
                CoverSelectionMode = SelectionMode.EverySelection
            };
        }

        private void Rotate(ImageRotaterSettings settings)
        {
            IReadOnlyList<string> covers = _store.GetImagePaths(_game.Id, ArtworkKind.Cover);

            var service = new BackgroundRotationService(
                new StubSource(new string[0]),
                new StubSource(covers),
                new ImageSelector(new ImagePicker(), new SessionSelectionCache()),
                new PlayniteBackgroundWriter(null, _root),
                null,
                _store,
                () => settings,
                null,
                new ArtworkPublisher(_store));

            service.ApplyTo(_game, ArtworkKind.Cover);
        }

        // The published file must BE the GIF, byte for byte. A theme that can
        // animate has nothing to animate otherwise.
        [Test]
        public void AnimatedPick_PublishesTheAnimationItselfNotThePoster()
        {
            string gif = AddGif("animated");

            Rotate(Settings());

            string published = PublishedPath();
            Assert.IsTrue(File.Exists(published), "nothing was published for the theme to bind");

            // Compare against the file in the store, not the temp source: the
            // store keeps its own copy.
            IReadOnlyList<string> stored = _store.GetImagePaths(_game.Id, ArtworkKind.Cover);
            Assert.AreEqual(1, stored.Count, "expected exactly the one cover added");

            CollectionAssert.AreEqual(
                File.ReadAllBytes(stored[0]), File.ReadAllBytes(published),
                "the published tile must be the animated file itself - a poster frame "
                + "cannot animate, and the theme renders this file directly");

            Assert.Greater(new FileInfo(published).Length, 0);
        }

        // The settings path themes bind for the selected game has to name the
        // animation too, for the same reason.
        [Test]
        public void AnimatedPick_PublishedPathKeepsTheAnimatedExtension()
        {
            AddGif("animated");

            var settings = Settings();
            Rotate(settings);

            Assert.IsNotNull(settings.CurrentCoverPath);
            StringAssert.EndsWith(".gif", settings.CurrentCoverPath,
                "a theme binding this path directly would otherwise get a still frame");
        }

        // The poster still exists and is still what a static consumer should
        // use - this change redirects the THEME, it does not remove the poster.
        [Test]
        public void PosterIsStillProducedForConsumersThatCannotAnimate()
        {
            string gif = AddGif("animated");
            IReadOnlyList<string> stored = _store.GetImagePaths(_game.Id, ArtworkKind.Cover);

            string poster = PosterFrame.For(stored[0]);

            Assert.IsNotNull(poster, "the still stand-in must still be available");
            StringAssert.EndsWith(".jpg", poster);
            Assert.IsFalse(PosterFrame.IsAnimated(poster));
        }
    }
}
