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
