using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using ImageRotater.Models;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    // Levelling a background to the game's common width is a full decode,
    // bicubic resize and PNG encode - 100 to 300 ms on the UI thread, per
    // rotation, measured on real 1080p and 4K sources. It used to write a
    // temp file and delete it after import, so the same picture paid that
    // every time it came round. Now the result is kept beside the source and
    // reused, so an image is levelled once.
    [TestFixture]
    public class NormaliseCacheTests
    {
        private string _dir;
        private Guid _gameId;

        private class RecordingWriter : PlayniteBackgroundWriter
        {
            public readonly List<string> Imported = new List<string>();

            public RecordingWriter(string dir) : base(null, dir) { }

            protected override string ImportFile(string imagePath, Guid gameId)
            {
                Imported.Add(imagePath);
                return Guid.NewGuid().ToString();
            }

            protected override void InvokeOnUi(Action action)
            {
                try { action(); } catch (NullReferenceException) { }
            }

            protected override void SetCurrent(Playnite.SDK.Models.Game game, ArtworkKind kind, string id) { }
        }

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ImageRotaterNorm_" + Guid.NewGuid().ToString("N"));
            _gameId = Guid.NewGuid();
            Directory.CreateDirectory(Path.Combine(_dir, "Images", _gameId.ToString(), "backgrounds"));
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        // Short, so the encode stays cheap; only the width matters here.
        private string MakeBackground(string name, int width)
        {
            string path = Path.Combine(_dir, "Images", _gameId.ToString(), "backgrounds", name);

            using (var bitmap = new Bitmap(width, 8))
            {
                bitmap.Save(path, ImageFormat.Png);
            }

            return path;
        }

        [Test]
        public void SecondRotationOntoTheSameImage_ReusesTheLevelledCopy()
        {
            string wide = MakeBackground("wide.png", 2560);
            MakeBackground("narrow.png", 1280);

            var writer = new RecordingWriter(_dir)
            {
                ScreenWidth = () => 1920,
                NormaliseBackgrounds = () => true
            };
            var game = new Playnite.SDK.Models.Game { Id = _gameId, Name = "Game" };

            writer.SetArtwork(game, wide, ArtworkKind.Background);

            string levelled = writer.Imported.Single();
            Assert.AreNotEqual(wide, levelled, "2560 wide must be brought down to the 1920 target");
            Assert.IsTrue(File.Exists(levelled), "the levelled copy must survive the import");
            StringAssert.Contains(Letterboxer.CacheFolderName, levelled,
                "kept in the cache folder every listing and bulk job already skips");

            DateTime written = File.GetLastWriteTimeUtc(levelled);
            Thread.Sleep(30);

            writer.SetArtwork(game, wide, ArtworkKind.Background);

            Assert.AreEqual(levelled, writer.Imported[1], "the same copy is imported again");
            Assert.AreEqual(written, File.GetLastWriteTimeUtc(levelled), "and it was not re-encoded");
        }

        [Test]
        public void ANewerSource_IsLevelledAgain()
        {
            string wide = MakeBackground("wide.png", 2560);

            var writer = new RecordingWriter(_dir)
            {
                ScreenWidth = () => 1920,
                NormaliseBackgrounds = () => true
            };
            var game = new Playnite.SDK.Models.Game { Id = _gameId, Name = "Game" };

            writer.SetArtwork(game, wide, ArtworkKind.Background);
            string levelled = writer.Imported.Single();
            DateTime first = File.GetLastWriteTimeUtc(levelled);

            // A download under the same name replaces the picture.
            Thread.Sleep(30);
            File.SetLastWriteTimeUtc(wide, DateTime.UtcNow.AddSeconds(5));

            writer.SetArtwork(game, wide, ArtworkKind.Background);

            Assert.Greater(File.GetLastWriteTimeUtc(levelled), first, "a stale copy would resurrect the old artwork");
        }

        [Test]
        public void TheLevelledCopy_IsNeverARotationCandidate()
        {
            string wide = MakeBackground("wide.png", 2560);

            var writer = new RecordingWriter(_dir)
            {
                ScreenWidth = () => 1920,
                NormaliseBackgrounds = () => true
            };
            writer.SetArtwork(new Playnite.SDK.Models.Game { Id = _gameId }, wide, ArtworkKind.Background);

            var store = new GameImageStore(_dir);
            CollectionAssert.AreEqual(
                new[] { wide },
                store.GetImagePaths(_gameId, ArtworkKind.Background).ToArray());
        }
    }
}
