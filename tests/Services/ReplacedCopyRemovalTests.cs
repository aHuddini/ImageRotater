using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using NUnit.Framework;
using ImageRotater.Models;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    // Removing the library copy a rotation just replaced must not hold up
    // the rotation.
    //
    // Playnite's RemoveFile retries a locked file five times, half a second
    // apart, before giving up - and the file IS locked, because the theme's
    // tile still has the previous cover open. Run on the UI thread, that was
    // a 2.6 s freeze on every cover rotation, measured in Fullscreen with
    // Aniki ReMake, for a delete that then failed anyway.
    [TestFixture]
    public class ReplacedCopyRemovalTests
    {
        private string _dir;

        private class SlowRemovalWriter : PlayniteBackgroundWriter
        {
            public readonly ManualResetEventSlim RemovalStarted = new ManualResetEventSlim();
            public int Removals;

            public SlowRemovalWriter(string dir) : base(null, dir) { }

            private int _imports;

            protected override string ImportFile(string imagePath, Guid gameId)
            {
                return "import-" + (++_imports);
            }

            protected override void InvokeOnUi(Action action)
            {
                try { action(); } catch (NullReferenceException) { }
            }

            protected override void SetCurrent(Playnite.SDK.Models.Game game, ArtworkKind kind, string id) { }

            // Stands in for Playnite's retry loop.
            protected override void RemoveLibraryFile(string id)
            {
                RemovalStarted.Set();
                Interlocked.Increment(ref Removals);
                Thread.Sleep(2500);
            }
        }

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ImageRotaterRemove_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        [Test]
        public void SecondRotation_DoesNotWaitForThePreviousCopyToBeRemoved()
        {
            string art = Path.Combine(_dir, "art.png");
            File.WriteAllBytes(art, new byte[] { 1, 2, 3, 4 });

            var writer = new SlowRemovalWriter(_dir);
            var game = new Playnite.SDK.Models.Game { Id = Guid.NewGuid(), Name = "Game", CoverImage = "import-1" };

            // First write records "import-1" as the original and imports
            // "import-2"; the second replaces "import-2", which is ours and
            // therefore safe to remove.
            writer.SetArtwork(game, art, ArtworkKind.Cover);
            game.CoverImage = "import-2";

            var timer = Stopwatch.StartNew();
            writer.SetArtwork(game, art, ArtworkKind.Cover);
            timer.Stop();

            Assert.IsTrue(writer.RemovalStarted.Wait(2000), "the replaced copy must still be removed");
            Assert.Less(timer.ElapsedMilliseconds, 1000,
                "the rotation returned before the removal finished - it is not on the caller's thread");
        }
    }
}
