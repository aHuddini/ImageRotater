using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using NUnit.Framework;
using ImageRotater.Models;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    // Artwork the user chose beats artwork that merely came with the game.
    //
    // A preserved original is whatever Playnite already had - a Steam grid, a
    // metadata provider's cover - copied into the plugin's folder the first
    // time it rotated that game. The copy has to exist: once Game.CoverImage
    // points at a plugin file the original is unreferenced, Playnite's library
    // cleanup can reclaim it, and "Restore original backgrounds" then holds an
    // id resolving to nothing.
    //
    // Existing for safety is not the same as competing for screen time. A game
    // given a deliberate animated cover rotated it against the still that came
    // with it, every six seconds, which read as the animation breaking - and
    // the still was not even the same picture.
    [TestFixture]
    public class PreservedArtworkPriorityTests
    {
        private string _root;
        private GameImageStore _store;
        private Guid _gameId;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "ImageRotaterPreserved_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _store = new GameImageStore(_root);
            _gameId = Guid.NewGuid();
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        // Distinct content per file, or the store's content deduplication
        // collapses them and the test measures the wrong thing.
        private int _seed;

        private string Add(string name)
        {
            string path = Path.Combine(_root, name);

            using (var bmp = new Bitmap(16 + (++_seed), 16))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(10 + _seed * 20, 40, 60));
                bmp.Save(path, ImageFormat.Jpeg);
            }

            return _store.AddImage(_gameId, path, ArtworkKind.Cover);
        }

        private IReadOnlyList<string> Candidates()
        {
            return _store.GetImagePaths(_gameId, ArtworkKind.Cover);
        }

        [Test]
        public void PreservedArtIsExcludedWhenTheGameHasOtherArtwork()
        {
            Add("original_steamgrid.jpg");
            Add("web_chosen.jpg");

            var listed = Candidates();

            Assert.AreEqual(1, listed.Count,
                "the preserved original must step aside for artwork the user added");

            Assert.IsFalse(GameImageStore.IsPreservedOriginal(listed[0]));
        }

        // The safety guarantee: the file is still there, still restorable. Only
        // its place in the ROTATION changed.
        [Test]
        public void PreservedArtIsStillOnDisk()
        {
            string preserved = Add("original_steamgrid.jpg");
            Add("web_chosen.jpg");

            Assert.IsTrue(File.Exists(preserved),
                "excluding it from rotation must not delete it - Playnite's cleanup can "
                + "reclaim the original, and Restore needs this copy to resolve");
        }

        // A game the user has not set up has nothing else, so the preserved
        // original IS the rotation - otherwise the plugin would show nothing
        // for a game whose only artwork it had just copied in.
        [Test]
        public void PreservedArtRotatesWhenItIsAllTheGameHas()
        {
            Add("original_steamgrid.jpg");

            var listed = Candidates();

            Assert.AreEqual(1, listed.Count);
            Assert.IsTrue(GameImageStore.IsPreservedOriginal(listed[0]),
                "with nothing else, the preserved original must still be shown");
        }

        [Test]
        public void SeveralPreservedOriginalsAllRotateWhenThereIsNothingElse()
        {
            Add("original_a.jpg");
            Add("original_b.jpg");

            Assert.AreEqual(2, Candidates().Count);
        }

        [Test]
        public void ThePrefixIsMatchedCaseInsensitively()
        {
            Assert.IsTrue(GameImageStore.IsPreservedOriginal(@"C:\a\original_x.jpg"));
            Assert.IsTrue(GameImageStore.IsPreservedOriginal(@"C:\a\ORIGINAL_x.jpg"));
            Assert.IsFalse(GameImageStore.IsPreservedOriginal(@"C:\a\web_original.jpg"),
                "the prefix anchors at the start - a file merely containing the word is not preserved art");
            Assert.IsFalse(GameImageStore.IsPreservedOriginal(@"C:\a\sgdb_1.jpg"));
        }
    }
}
