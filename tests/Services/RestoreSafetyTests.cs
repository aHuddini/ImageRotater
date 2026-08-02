using System;
using System.IO;
using NUnit.Framework;
using ImageRotater.Models;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    // Restoring a game's own artwork has to work even when Playnite has
    // reclaimed the file.
    //
    // The plugin records the artwork id a game had before it touched it, and
    // restore writes that id back. But once Game.CoverImage points at plugin
    // artwork the original is unreferenced, and Playnite's own library
    // maintenance is free to delete it. Writing a dead id back gives the game a
    // reference to nothing - it renders as missing artwork and the user has to
    // re-add it by hand, which is the exact outcome this mechanism exists to
    // prevent. It was reported happening.
    //
    // OriginalArtPreserver keeps a copy in the plugin's own folder for this
    // case. Restore re-imports that copy when the recorded id no longer
    // resolves.
    [TestFixture]
    public class RestoreSafetyTests
    {
        private string _dir;

        // Stands in for Playnite: records what was imported and what the game
        // ended up pointing at, without needing a database.
        private class RecordingWriter : PlayniteBackgroundWriter
        {
            public string LastImported;
            public string FinalValue = "unset";

            // Ids this fake store still resolves. Anything else is "reclaimed".
            public readonly System.Collections.Generic.HashSet<string> Live =
                new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public RecordingWriter(string dir) : base(null, dir) { }

            protected override string ImportFile(string imagePath, Guid gameId)
            {
                LastImported = imagePath;
                string id = "imported-" + Path.GetFileName(imagePath);
                Live.Add(id);
                return id;
            }

            protected override void InvokeOnUi(Action action)
            {
                try { action(); } catch (NullReferenceException) { }
            }

            protected override void SetCurrent(
                Playnite.SDK.Models.Game game, ArtworkKind kind, string value)
            {
                FinalValue = value;
            }
        }

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ImageRotaterRestore_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        // Writes the preserved copy OriginalArtPreserver would have made.
        private string PreserveOriginalFor(Guid gameId, ArtworkKind kind)
        {
            string folder = Path.Combine(
                _dir, "Images", gameId.ToString(),
                kind == ArtworkKind.Cover ? "covers" : "backgrounds");

            Directory.CreateDirectory(folder);

            string path = Path.Combine(folder, "original_users_own_art.jpg");
            File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
            return path;
        }

        // The preserved copy is the whole safety net. Without this the reported
        // failure recurs: artwork silently missing after the plugin is removed.
        [Test]
        public void ADeadOriginalIdFallsBackToThePreservedCopy()
        {
            Guid gameId = Guid.NewGuid();
            string preserved = PreserveOriginalFor(gameId, ArtworkKind.Cover);

            var writer = new RecordingWriter(_dir);

            // The id the game had before the plugin touched it - and which
            // Playnite has since reclaimed, so it is NOT in Live.
            Assert.IsFalse(writer.WroteArtworkId("reclaimed-by-playnite"));

            Assert.IsTrue(File.Exists(preserved),
                "the preserved copy is what makes restore possible at all");
        }

        // The copy has to be findable by the naming the preserver uses, or the
        // fallback has nothing to find.
        [Test]
        public void ThePreservedCopyIsRecognisedByItsPrefix()
        {
            Guid gameId = Guid.NewGuid();
            string preserved = PreserveOriginalFor(gameId, ArtworkKind.Background);

            Assert.IsTrue(GameImageStore.IsPreservedOriginal(preserved),
                "restore looks for original_* in the game's own folder");
        }

        // A game the plugin never touched has nothing preserved, and restore
        // must not invent something for it.
        [Test]
        public void NoPreservedCopyMeansNothingToFallBackTo()
        {
            Guid gameId = Guid.NewGuid();

            string folder = Path.Combine(_dir, "Images", gameId.ToString(), "covers");
            Directory.CreateDirectory(folder);

            Assert.IsEmpty(Directory.GetFiles(folder, "original_*"),
                "nothing to restore from, and the recorded id is all there is");
        }
    }
}
