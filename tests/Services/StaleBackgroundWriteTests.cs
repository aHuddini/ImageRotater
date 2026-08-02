using System;
using System.IO;
using NUnit.Framework;
using ImageRotater.Models;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    // A background write that has been overtaken must not commit.
    //
    // Backgrounds rotate for the game being LEFT, which pre-stages the next
    // image and keeps arrivals from painting twice. The write is then queued to
    // the UI thread, so it is always one selection behind by design - fine at a
    // normal pace.
    //
    // Scrolling quickly is not a normal pace. Several writes queue up and the
    // UI thread commits them after the user has moved on AGAIN, so
    // Game.BackgroundImage ends up holding artwork for a game two or more
    // selections back and Playnite renders it under the current one. Seen in a
    // live log as:
    //
    //   12:22:07.345  selected "Persona 3 Reload"
    //   12:22:08.520  selected "Phantom Fury"
    //   12:22:08.538  Background: "Persona 3 Reload"   <- 18ms too late
    //
    // The guard is a selection COUNTER rather than an id comparison: the
    // departing game is never the current selection, so comparing the two would
    // suppress every background write there is.
    [TestFixture]
    public class StaleBackgroundWriteTests
    {
        private string _dir;

        // Records what reached the database, and lets a test simulate the user
        // moving on while the write is queued.
        private class RecordingWriter : PlayniteBackgroundWriter
        {
            public int Commits;
            public Action BeforeCommit;

            public RecordingWriter(string dir) : base(null, dir) { }

            // No Playnite database here, so hand back a plausible id rather
            // than reaching for one.
            protected override string ImportFile(string imagePath, Guid gameId)
            {
                return Guid.NewGuid().ToString();
            }

            // Stands in for the dispatcher. The real one runs this later, on
            // the UI thread, which is exactly where the staleness appears.
            //
            // The commit itself calls Games.Update, which needs an API this
            // test does not have - so the exception it throws is swallowed
            // here, AFTER SetCurrent has already recorded whether the write got
            // that far. That is the fact under test.
            protected override void InvokeOnUi(Action action)
            {
                if (BeforeCommit != null)
                {
                    BeforeCommit();
                }

                try
                {
                    action();
                }
                catch (NullReferenceException)
                {
                }
            }

            protected override void SetCurrent(Playnite.SDK.Models.Game game, ArtworkKind kind, string id)
            {
                Commits++;
            }
        }

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ImageRotaterStale_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private string MakeImage()
        {
            string path = Path.Combine(_dir, "art.jpg");
            File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
            return path;
        }

        [Test]
        public void BackgroundWriteCommitsWhenSelectionHasNotMovedOn()
        {
            int generation = 7;

            var writer = new RecordingWriter(_dir) { SelectionGeneration = () => generation };
            var game = new Playnite.SDK.Models.Game { Id = Guid.NewGuid(), Name = "Game" };

            writer.SetArtwork(game, MakeImage(), ArtworkKind.Background);

            Assert.AreEqual(1, writer.Commits,
                "a write that was not overtaken must reach the database");
        }

        [Test]
        public void BackgroundWriteIsDroppedWhenSelectionMovedOnWhileQueued()
        {
            int generation = 7;

            var writer = new RecordingWriter(_dir) { SelectionGeneration = () => generation };
            var game = new Playnite.SDK.Models.Game { Id = Guid.NewGuid(), Name = "Game" };

            // The user scrolls on while this write sits in the dispatcher queue.
            writer.BeforeCommit = () => generation++;

            writer.SetArtwork(game, MakeImage(), ArtworkKind.Background);

            Assert.AreEqual(0, writer.Commits,
                "this write is for a game two selections back - committing it puts "
                + "another game's artwork under the one the user is looking at");
        }

        // Covers rotate for the game ARRIVED at, so a late cover write still
        // belongs to a game the user actually chose. Suppressing those would
        // break cover rotation for no benefit.
        [Test]
        public void CoverWriteCommitsEvenWhenSelectionMovedOn()
        {
            int generation = 7;

            var writer = new RecordingWriter(_dir) { SelectionGeneration = () => generation };
            var game = new Playnite.SDK.Models.Game { Id = Guid.NewGuid(), Name = "Game" };

            writer.BeforeCommit = () => generation++;

            writer.SetArtwork(game, MakeImage(), ArtworkKind.Cover);

            Assert.AreEqual(1, writer.Commits,
                "covers rotate for the game arrived at, so a late one is still correct");
        }

        // Every other caller - tests, tools - has no selection to speak of.
        [Test]
        public void NoSelectionSourceMeansNeverStale()
        {
            var writer = new RecordingWriter(_dir);
            var game = new Playnite.SDK.Models.Game { Id = Guid.NewGuid(), Name = "Game" };

            writer.SetArtwork(game, MakeImage(), ArtworkKind.Background);

            Assert.AreEqual(1, writer.Commits,
                "without a selection source the guard must not suppress anything");
        }
    }
}
