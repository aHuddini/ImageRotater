using System;
using System.IO;
using NUnit.Framework;
using ImageRotater.Models;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    // Byte-identical files under different names must collapse to one
    // candidate. The preserver copies whatever artwork Playnite had stored -
    // often an earlier download of a file already in the pool - and rotation
    // then visibly "changes" to the identical picture, which is the most
    // confusing thing a slideshow can do.
    [TestFixture]
    public class CandidateDeduplicationTests
    {
        private string _root;
        private GameImageStore _store;
        private Guid _gameId;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "ImageRotaterDedup_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _store = new GameImageStore(_root);
            _gameId = Guid.NewGuid();
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        private void AddRaw(string name, byte[] content)
        {
            // Written straight into the game folder rather than through
            // AddImage, which would rename a collision - the duplicate names
            // here are the point.
            string folder = Path.Combine(_store.ImagesRoot, _gameId.ToString(), "backgrounds");
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, name), content);
        }

        [Test]
        public void ByteIdenticalFiles_CollapseToOne()
        {
            byte[] picture = { 1, 2, 3, 4, 5, 6, 7, 8 };
            AddRaw("original_preserved.png", picture);
            AddRaw("sgdb_12345.png", picture);

            var candidates = _store.GetImagePaths(_gameId, ArtworkKind.Background);

            Assert.AreEqual(1, candidates.Count,
                "two names for the same picture must be one rotation candidate");
        }

        // Same length is only a hint. Different content of equal size must
        // both survive - length alone would wrongly merge distinct artwork.
        [Test]
        public void SameLengthDifferentContent_BothSurvive()
        {
            AddRaw("a.png", new byte[] { 1, 2, 3, 4 });
            AddRaw("b.png", new byte[] { 9, 8, 7, 6 });

            Assert.AreEqual(2, _store.GetImagePaths(_gameId, ArtworkKind.Background).Count);
        }

        [Test]
        public void SurvivorIsDeterministic()
        {
            byte[] picture = { 5, 5, 5, 5 };
            AddRaw("zzz_copy.png", picture);
            AddRaw("aaa_first.png", picture);

            var candidates = _store.GetImagePaths(_gameId, ArtworkKind.Background);

            Assert.AreEqual(1, candidates.Count);
            StringAssert.Contains("aaa_first", candidates[0],
                "the alphabetically first name survives, every time");
        }

        [Test]
        public void UniqueFiles_AreUntouched()
        {
            AddRaw("a.png", new byte[] { 1 });
            AddRaw("b.png", new byte[] { 2, 2 });
            AddRaw("c.png", new byte[] { 3, 3, 3 });

            Assert.AreEqual(3, _store.GetImagePaths(_gameId, ArtworkKind.Background).Count);
        }
    }
}
