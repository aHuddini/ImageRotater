using System;
using System.IO;
using NUnit.Framework;
using ImageRotater.Models;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    [TestFixture]
    public class GameImageStoreTests
    {
        private string _root;
        private GameImageStore _store;
        private Guid _gameId;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "ImageRotaterStore_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _store = new GameImageStore(_root);
            _gameId = Guid.NewGuid();
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        private string MakeSourceFile(string name)
        {
            string path = Path.Combine(_root, name);

            // Content derived from the name: every distinct file is distinct
            // CONTENT. The store deduplicates byte-identical candidates, so a
            // fixture writing the same four bytes everywhere would silently
            // collapse its own pool.
            File.WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes("img:" + name));
            return path;
        }

        [Test]
        public void GetImagePaths_NoFolder_ReturnsEmpty()
        {
            Assert.AreEqual(0, _store.GetImagePaths(_gameId, ArtworkKind.Background).Count);
        }

        [Test]
        public void AddImage_CopiesFileIntoGameFolder()
        {
            string src = MakeSourceFile("art.jpg");

            string added = _store.AddImage(_gameId, src, ArtworkKind.Background);

            Assert.IsNotNull(added);
            Assert.IsTrue(File.Exists(added));
            Assert.AreEqual(_store.GetGameFolder(_gameId, ArtworkKind.Background), Path.GetDirectoryName(added));
            // The original must survive - we copy, never move the user's file.
            Assert.IsTrue(File.Exists(src));
        }

        // The suffix exists for a NAME collision between different images -
        // "art.jpg" from two different sources. Both files must survive and
        // both must rotate.
        [Test]
        public void AddImage_SameNameDifferentContent_KeepsBothWithSuffix()
        {
            string first = _store.AddImage(_gameId, MakeSourceFile("art.jpg"), ArtworkKind.Background);

            // Same name, different bytes - a genuinely different image.
            string src2 = Path.Combine(_root, "elsewhere");
            Directory.CreateDirectory(src2);
            string other = Path.Combine(src2, "art.jpg");
            File.WriteAllBytes(other, System.Text.Encoding.UTF8.GetBytes("different picture"));

            string second = _store.AddImage(_gameId, other, ArtworkKind.Background);

            Assert.AreNotEqual(first, second, "second add overwrote the first");
            Assert.AreEqual(2, _store.GetImagePaths(_gameId, ArtworkKind.Background).Count);
        }

        // Adding the SAME image twice keeps both files on disk but yields one
        // candidate: byte-identical content is deduplicated at listing time,
        // because rotating between two names for one picture is a visible
        // "change" to the identical image.
        [Test]
        public void AddImage_SameContentTwice_IsOneCandidate()
        {
            string src = MakeSourceFile("art.jpg");

            _store.AddImage(_gameId, src, ArtworkKind.Background);
            _store.AddImage(_gameId, src, ArtworkKind.Background);

            Assert.AreEqual(1, _store.GetImagePaths(_gameId, ArtworkKind.Background).Count);
        }

        [Test]
        public void AddImage_MissingSource_ReturnsNull()
        {
            Assert.IsNull(_store.AddImage(_gameId, Path.Combine(_root, "nope.jpg"), ArtworkKind.Background));
            Assert.IsNull(_store.AddImage(_gameId, null, ArtworkKind.Background));
        }

        [Test]
        public void GetImagePaths_IgnoresUnsupportedExtensions()
        {
            _store.AddImage(_gameId, MakeSourceFile("good.png"), ArtworkKind.Background);
            _store.AddImage(_gameId, MakeSourceFile("notes.txt"), ArtworkKind.Background);
            _store.AddImage(_gameId, MakeSourceFile("readme.md"), ArtworkKind.Background);

            var paths = _store.GetImagePaths(_gameId, ArtworkKind.Background);

            Assert.AreEqual(1, paths.Count, "only displayable files should be listed");
            Assert.IsTrue(paths[0].EndsWith("good.png", StringComparison.OrdinalIgnoreCase));
        }

        // Video used to be excluded here on the grounds that listing what we
        // could not show would surface as a broken-image placeholder. Both
        // controls now render it with a MediaElement, so it is a candidate like
        // any other - the rotation service is what keeps it out of Playnite's
        // database, which decodes to a single bitmap. See VideoArtworkTests.
        [Test]
        public void GetImagePaths_ListsVideoNowThatControlsRenderIt()
        {
            _store.AddImage(_gameId, MakeSourceFile("clip.mp4"), ArtworkKind.Background);
            _store.AddImage(_gameId, MakeSourceFile("other.webm"), ArtworkKind.Background);

            var paths = _store.GetImagePaths(_gameId, ArtworkKind.Background);

            Assert.AreEqual(2, paths.Count, "video must be offered to rotation");
        }

        // A stable order matters: the session cache remembers a path, and Fixed
        // mode returns "the first one". Both need enumeration order not to drift.
        [Test]
        public void GetImagePaths_IsSortedStably()
        {
            _store.AddImage(_gameId, MakeSourceFile("c.jpg"), ArtworkKind.Background);
            _store.AddImage(_gameId, MakeSourceFile("a.jpg"), ArtworkKind.Background);
            _store.AddImage(_gameId, MakeSourceFile("b.jpg"), ArtworkKind.Background);

            var first = _store.GetImagePaths(_gameId, ArtworkKind.Background);
            var second = _store.GetImagePaths(_gameId, ArtworkKind.Background);

            CollectionAssert.AreEqual(first, second);
            Assert.IsTrue(first[0].EndsWith("a.jpg", StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(first[2].EndsWith("c.jpg", StringComparison.OrdinalIgnoreCase));
        }

        [Test]
        public void RemoveImage_DeletesTheFile()
        {
            string added = _store.AddImage(_gameId, MakeSourceFile("art.jpg"), ArtworkKind.Background);

            Assert.IsTrue(_store.RemoveImage(added));
            Assert.AreEqual(0, _store.GetImagePaths(_gameId, ArtworkKind.Background).Count);
        }

        [Test]
        public void RemoveImage_MissingFile_ReturnsFalseWithoutThrowing()
        {
            Assert.IsFalse(_store.RemoveImage(Path.Combine(_root, "gone.jpg")));
            Assert.IsFalse(_store.RemoveImage(null));
        }

        [Test]
        public void GamesAreIsolatedFromEachOther()
        {
            var other = Guid.NewGuid();
            _store.AddImage(_gameId, MakeSourceFile("mine.jpg"), ArtworkKind.Background);

            Assert.AreEqual(1, _store.GetImagePaths(_gameId, ArtworkKind.Background).Count);
            Assert.AreEqual(0, _store.GetImagePaths(other, ArtworkKind.Background).Count);
        }
    }
}
