using System;
using System.IO;
using NUnit.Framework;
using Playnite.SDK.Models;
using ImageRotater.Models;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    // The mechanism that makes Fullscreen grid tiles rotate.
    //
    // A tile builds this path in XAML from its OWN game's id, so every tile
    // reads its own artwork with no per-tile coordination from the plugin, no
    // converter, and no plugin control. The published settings values describe
    // only the selected game, which is why they cannot drive a grid alone.
    [TestFixture]
    public class PublishCurrentTests
    {
        private string _root;
        private GameImageStore _store;
        private Guid _gameId;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "ImageRotaterPub_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _store = new GameImageStore(_root);
            _gameId = Guid.NewGuid();
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        private string MakeFile(string name, byte[] content)
        {
            string path = Path.Combine(_root, name);
            File.WriteAllBytes(path, content);
            return path;
        }

        // Asks the store where published files live rather than rebuilding the
        // path. Rebuilding it here is what made a dozen tests fail as a block
        // when that location moved, none of them for a reason a reader could
        // see from the assertion.
        private string PublishedPath(ArtworkKind kind)
        {
            return Path.Combine(
                _store.GetPublishedFolder(_gameId, kind),
                GameImageStore.PublishedFileName);
        }

        [Test]
        public void Publish_WritesTheFixedNameThemesBindTo()
        {
            string source = MakeFile("pick.png", new byte[] { 1, 2, 3, 4 });

            Assert.IsTrue(_store.PublishCurrent(_gameId, source, ArtworkKind.Cover));
            Assert.IsTrue(File.Exists(PublishedPath(ArtworkKind.Cover)));
            CollectionAssert.AreEqual(
                new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(PublishedPath(ArtworkKind.Cover)));
        }

        // Rotation overwrites this on every pick, so replacing must work and
        // must leave the NEW content behind.
        [Test]
        public void Publish_OverwritesWithTheNewPick()
        {
            _store.PublishCurrent(_gameId, MakeFile("a.png", new byte[] { 1 }), ArtworkKind.Cover);
            _store.PublishCurrent(_gameId, MakeFile("b.png", new byte[] { 9, 9 }), ArtworkKind.Cover);

            CollectionAssert.AreEqual(
                new byte[] { 9, 9 }, File.ReadAllBytes(PublishedPath(ArtworkKind.Cover)),
                "a second rotation must leave the newer image in place");
        }

        // Covers and backgrounds live in separate folders, so publishing one
        // must not disturb the other.
        [Test]
        public void Publish_KeepsKindsSeparate()
        {
            _store.PublishCurrent(_gameId, MakeFile("c.png", new byte[] { 1 }), ArtworkKind.Cover);
            _store.PublishCurrent(_gameId, MakeFile("b.png", new byte[] { 2 }), ArtworkKind.Background);

            CollectionAssert.AreEqual(new byte[] { 1 }, File.ReadAllBytes(PublishedPath(ArtworkKind.Cover)));
            CollectionAssert.AreEqual(new byte[] { 2 }, File.ReadAllBytes(PublishedPath(ArtworkKind.Background)));
        }

        // The published file is a COPY of a real candidate living in the same
        // folder. If it were ever listed as a candidate itself, a game's cover
        // could rotate onto itself and the rotation would appear to stop.
        // Guaranteed today by its extension being absent from the store's
        // supported-extension set - this test fails if that set ever grows to
        // include it.
        [Test]
        public void PublishedFile_IsNeverARotationCandidate()
        {
            _store.AddImage(_gameId, MakeFile("real.png", new byte[] { 1 }), ArtworkKind.Cover);
            _store.PublishCurrent(_gameId, MakeFile("pick.png", new byte[] { 2 }), ArtworkKind.Cover);

            foreach (string candidate in _store.GetImagePaths(_gameId, ArtworkKind.Cover))
            {
                Assert.AreNotEqual(
                    GameImageStore.PublishedFileName,
                    Path.GetFileName(candidate),
                    "the published copy must never be offered back as a candidate");
            }
        }

        // Leaves no temp file behind: the folder is also the candidate folder,
        // and stray files there are at best clutter and at worst candidates.
        [Test]
        public void Publish_LeavesNoTempFile()
        {
            _store.PublishCurrent(_gameId, MakeFile("x.png", new byte[] { 1 }), ArtworkKind.Cover);
            _store.PublishCurrent(_gameId, MakeFile("y.png", new byte[] { 2 }), ArtworkKind.Cover);

            string folder = Path.GetDirectoryName(PublishedPath(ArtworkKind.Cover));
            Assert.IsEmpty(Directory.GetFiles(folder, "*.tmp"),
                "a temp file was left in the candidate folder");
        }

        // Republishing has to keep working while something else holds the file
        // open for reading. Themes bind with CacheOption=OnLoad, which closes
        // WPF's handle at load - but a reader can still overlap a write, and a
        // publish that fails there stops rotation dead with no error on screen.
        [Test]
        public void Publish_SucceedsWhileTheFileIsOpenForReading()
        {
            _store.PublishCurrent(_gameId, MakeFile("a.png", new byte[] { 1 }), ArtworkKind.Cover);

            using (var reader = new FileStream(
                PublishedPath(ArtworkKind.Cover), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                Assert.IsTrue(
                    _store.PublishCurrent(_gameId, MakeFile("b.png", new byte[] { 7, 7 }), ArtworkKind.Cover),
                    "a rotation must still publish while a tile is reading the file");
            }

            CollectionAssert.AreEqual(
                new byte[] { 7, 7 }, File.ReadAllBytes(PublishedPath(ArtworkKind.Cover)));
        }

        // Seeding exists to make a fatal case impossible.
        //
        // Themes bind these with CacheOption=OnLoad, which is required so the
        // plugin can republish - the default binding never releases its file
        // handle. But OnLoad throws FileNotFoundException on a missing file,
        // and that throw lands in FullscreenTilePanel.MeasureOverride, taking
        // Playnite down. Rotation only publishes for the selected game, so
        // without seeding every other tile references a file that does not
        // exist yet.
        [Test]
        public void Seed_PublishesForEveryGameThatHasArtwork()
        {
            var withArt = new Game { Id = Guid.NewGuid(), Name = "Has art" };
            var withoutArt = new Game { Id = Guid.NewGuid(), Name = "No art" };

            _store.AddImage(withArt.Id, MakeFile("cover.png", new byte[] { 1 }), ArtworkKind.Cover);

            // Seeding needs only the store - no picker, writer, or preserver.
            // That it can be tested without them is the point of the split.
            var publisher = new ArtworkPublisher(_store);

            publisher.SeedEveryGame(new[] { withArt, withoutArt });

            Assert.IsTrue(
                File.Exists(publisher.PublishedPathFor(withArt.Id, ArtworkKind.Cover)),
                "a game with artwork must have a published file before any tile renders");

            // A game the user never set up still gets a file - a 70-byte
            // transparent placeholder, not a copy of its artwork.
            //
            // The file must exist for EVERY game: a theme tile builds this path
            // from its own game id and cannot ask whether the plugin has
            // artwork, because a WPF trigger compares a binding to a literal
            // and the tile's id is not one. A missing file throws
            // FileNotFoundException inside Playnite's layout pass, which is
            // fatal.
            string placeholder = publisher.PublishedPathFor(withoutArt.Id, ArtworkKind.Cover);
            Assert.IsTrue(File.Exists(placeholder),
                "every game needs a published file or its tile throws during layout");
            Assert.Less(new FileInfo(placeholder).Length, 1024,
                "the placeholder must be a tiny transparent pixel, not a copy of the artwork");
        }

        // The placeholder exists only so OnLoad has a file to open. Real
        // artwork must be able to replace it, or a game would stay blank
        // forever once seeded.
        [Test]
        public void Placeholder_IsReplacedByRealArtwork()
        {
            Assert.IsTrue(_store.EnsurePublishedPlaceholder(_gameId, ArtworkKind.Cover));

            string path = PublishedPath(ArtworkKind.Cover);
            long placeholderSize = new FileInfo(path).Length;

            _store.PublishCurrent(_gameId, MakeFile("real.png", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }), ArtworkKind.Cover);

            Assert.AreNotEqual(placeholderSize, new FileInfo(path).Length,
                "a rotation must overwrite the placeholder with the real pick");
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, File.ReadAllBytes(path));
        }

        // Seeding runs on every startup, so it must never clobber artwork a
        // previous session published.
        [Test]
        public void Placeholder_NeverOverwritesRealArtwork()
        {
            _store.PublishCurrent(_gameId, MakeFile("real.png", new byte[] { 9, 9, 9, 9 }), ArtworkKind.Cover);

            Assert.IsFalse(_store.EnsurePublishedPlaceholder(_gameId, ArtworkKind.Cover),
                "an existing published file must be left alone");
            CollectionAssert.AreEqual(
                new byte[] { 9, 9, 9, 9 }, File.ReadAllBytes(PublishedPath(ArtworkKind.Cover)));
        }

        // Cheap enough to write for an entire library on every startup.
        [Test]
        public void Placeholder_IsTiny()
        {
            _store.EnsurePublishedPlaceholder(_gameId, ArtworkKind.Cover);

            Assert.Less(new FileInfo(PublishedPath(ArtworkKind.Cover)).Length, 200,
                "seeding a 300-game library must not cost meaningful disk");
        }

        [Test]
        public void Publish_MissingSource_FailsQuietly()
        {
            Assert.IsFalse(_store.PublishCurrent(_gameId, Path.Combine(_root, "nope.png"), ArtworkKind.Cover));
            Assert.IsFalse(_store.PublishCurrent(_gameId, null, ArtworkKind.Cover));
            Assert.IsFalse(File.Exists(PublishedPath(ArtworkKind.Cover)));
        }

        // Themes join this root with a game id, so it has to be the real
        // folder the store actually writes into.
        [Test]
        public void ImagesRoot_IsWhereTheGameFoldersLive()
        {
            _store.PublishCurrent(_gameId, MakeFile("z.png", new byte[] { 1 }), ArtworkKind.Cover);

            Assert.IsTrue(Directory.Exists(Path.Combine(_store.ImagesRoot, _gameId.ToString())),
                "a theme joining ImagesRoot with a game id must reach that game's folder");
        }
    }
}
