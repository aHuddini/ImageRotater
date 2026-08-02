using System;
using System.IO;
using NUnit.Framework;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    // Telling the plugin's own rotation apart from the user's artwork.
    //
    // OriginalArtPreserver copies a game's pre-existing artwork into the
    // plugin's folder so it rotates like any other candidate. It is supposed to
    // skip artwork the plugin itself wrote - otherwise every restart preserves
    // the previous rotation as a fresh "original".
    //
    // The guard was a PATH test: is this file inside our folder? It can never
    // be true. Database.AddFile IMPORTS a copy into Playnite's store, so the
    // moment the plugin writes anything, the game's artwork id resolves inside
    // PLAYNITE's folder. The check matched nothing and the preserver duly
    // copied our own rotations back in.
    //
    // Visible symptom, and the reason this was found: a game set up with a
    // single animated cover acquired a still candidate it never had, so
    // rotation started landing on the still and the animation "stopped
    // working" at random.
    //
    // The fix is to ask by ID, which is exact.
    [TestFixture]
    public class WrittenArtworkOwnershipTests
    {
        private string _dir;
        private PlayniteBackgroundWriter _writer;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ImageRotaterOwn_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);

            // No IPlayniteAPI needed: the ownership record is plain state.
            _writer = new PlayniteBackgroundWriter(null, _dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        [Test]
        public void AnUnknownArtworkIdIsNotOurs()
        {
            Assert.IsFalse(_writer.WroteArtworkId("some-playnite-id"),
                "the user's own artwork must be preserved, not skipped");
        }

        [Test]
        public void NullAndEmptyAreNotOurs()
        {
            Assert.IsFalse(_writer.WroteArtworkId(null));
            Assert.IsFalse(_writer.WroteArtworkId(string.Empty));
        }

        // The preserver asks this with whatever Playnite currently holds, and
        // Playnite's ids are opaque strings whose casing the plugin does not
        // control - so a case difference must not read as "not ours" and
        // reintroduce the duplicate.
        [Test]
        public void ArtworkWeWroteIsRecognisedRegardlessOfCasing()
        {
            var written = typeof(PlayniteBackgroundWriter).GetMethod(
                "NoteWritten",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            Assert.IsNotNull(written, "NoteWritten records what the plugin imported");

            written.Invoke(_writer, new object[] { "abc123DEF" });

            Assert.IsTrue(_writer.WroteArtworkId("abc123DEF"));
            Assert.IsTrue(_writer.WroteArtworkId("ABC123def"),
                "a casing difference must not make our own rotation look like the user's art");
        }

        // The record has to SURVIVE A RESTART, and an earlier version did not.
        //
        // The reasoning for keeping it in memory only was that the ids are
        // transient because "the current value is rewritten before any preserve
        // can run". That is false. On restart the set starts empty while
        // Game.CoverImage still holds an id this plugin wrote last session, and
        // Preserve runs on the FIRST selection - before any rotation rewrites
        // it. So the plugin failed to recognise its own artwork and copied it
        // in as a fresh "original_" candidate, once per restart. On a game
        // whose pick was animated that handed rotation a still frame of its own
        // GIF to alternate with, which reads as the animation breaking.
        [Test]
        public void WrittenIdsSurviveARestart()
        {
            var written = typeof(PlayniteBackgroundWriter).GetMethod(
                "NoteWritten",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            written.Invoke(_writer, new object[] { "playnite-id-from-last-session" });

            // A second writer over the same directory is what the next Playnite
            // launch constructs.
            var afterRestart = new PlayniteBackgroundWriter(null, _dir);

            Assert.IsTrue(afterRestart.WroteArtworkId("playnite-id-from-last-session"),
                "the plugin must still recognise its own artwork after a restart, or it "
                + "preserves it as if it were the user's and gains a candidate every launch");
        }

        // Every rotation imports a fresh id and deletes the previous copy, so
        // the record has to hold more than the latest one - a preserve can run
        // against an id written a rotation or two ago.
        [Test]
        public void EveryWrittenIdIsRemembered()
        {
            var written = typeof(PlayniteBackgroundWriter).GetMethod(
                "NoteWritten",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            foreach (string id in new[] { "first", "second", "third" })
            {
                written.Invoke(_writer, new object[] { id });
            }

            Assert.IsTrue(_writer.WroteArtworkId("first"), "an earlier rotation is still ours");
            Assert.IsTrue(_writer.WroteArtworkId("third"));
        }
    }
}
