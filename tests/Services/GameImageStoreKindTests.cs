using System;
using System.IO;
using NUnit.Framework;
using ImageRotater.Models;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    // Backgrounds and covers must stay in separate pools, or a cover would
    // rotate into the background slot and vice versa.
    [TestFixture]
    public class GameImageStoreKindTests
    {
        private string _root;
        private GameImageStore _store;
        private Guid _gameId;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "ImageRotaterKind_" + Guid.NewGuid().ToString("N"));
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
            File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
            return path;
        }

        [Test]
        public void KindsUseDifferentFolders()
        {
            string bg = _store.GetGameFolder(_gameId, ArtworkKind.Background);
            string cover = _store.GetGameFolder(_gameId, ArtworkKind.Cover);

            Assert.AreNotEqual(bg, cover);
        }

        [Test]
        public void AddingACoverDoesNotAppearAmongBackgrounds()
        {
            _store.AddImage(_gameId, MakeSourceFile("boxart.png"), ArtworkKind.Cover);

            Assert.AreEqual(1, _store.GetImagePaths(_gameId, ArtworkKind.Cover).Count);
            Assert.AreEqual(0, _store.GetImagePaths(_gameId, ArtworkKind.Background).Count);
        }

        [Test]
        public void AddingABackgroundDoesNotAppearAmongCovers()
        {
            _store.AddImage(_gameId, MakeSourceFile("hero.png"), ArtworkKind.Background);

            Assert.AreEqual(1, _store.GetImagePaths(_gameId, ArtworkKind.Background).Count);
            Assert.AreEqual(0, _store.GetImagePaths(_gameId, ArtworkKind.Cover).Count);
        }

        [Test]
        public void SameFileNameCanExistInBothKinds()
        {
            string src = MakeSourceFile("art.png");

            string bg = _store.AddImage(_gameId, src, ArtworkKind.Background);
            string cover = _store.AddImage(_gameId, src, ArtworkKind.Cover);

            Assert.AreNotEqual(bg, cover);
            Assert.IsTrue(File.Exists(bg));
            Assert.IsTrue(File.Exists(cover));
        }

        // Images saved before the split live loose in Images\{gameId}\. They
        // were all backgrounds, so reading backgrounds must find them rather
        // than silently returning nothing and losing the user's art.
        [Test]
        public void LegacyLooseFilesAreMigratedIntoBackgrounds()
        {
            string legacyFolder = Path.Combine(_root, "Images", _gameId.ToString());
            Directory.CreateDirectory(legacyFolder);
            File.WriteAllBytes(Path.Combine(legacyFolder, "old.png"), new byte[] { 1, 2, 3 });

            var backgrounds = _store.GetImagePaths(_gameId, ArtworkKind.Background);

            Assert.AreEqual(1, backgrounds.Count);
            Assert.IsTrue(backgrounds[0].EndsWith("old.png", StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(
                backgrounds[0].IndexOf("backgrounds", StringComparison.OrdinalIgnoreCase) >= 0,
                "the file should have moved into the backgrounds subfolder");
        }

        [Test]
        public void MigrationDoesNotTouchCovers()
        {
            string legacyFolder = Path.Combine(_root, "Images", _gameId.ToString());
            Directory.CreateDirectory(legacyFolder);
            File.WriteAllBytes(Path.Combine(legacyFolder, "old.png"), new byte[] { 1, 2, 3 });

            _store.GetImagePaths(_gameId, ArtworkKind.Background);

            Assert.AreEqual(0, _store.GetImagePaths(_gameId, ArtworkKind.Cover).Count);
        }

        [Test]
        public void MigrationIsSafeToRunRepeatedly()
        {
            string legacyFolder = Path.Combine(_root, "Images", _gameId.ToString());
            Directory.CreateDirectory(legacyFolder);
            File.WriteAllBytes(Path.Combine(legacyFolder, "old.png"), new byte[] { 1, 2, 3 });

            _store.GetImagePaths(_gameId, ArtworkKind.Background);
            var second = _store.GetImagePaths(_gameId, ArtworkKind.Background);

            Assert.AreEqual(1, second.Count, "a repeated read duplicated the migrated file");
        }
    }
}
