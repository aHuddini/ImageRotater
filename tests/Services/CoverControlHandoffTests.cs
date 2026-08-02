using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Playnite.SDK.Models;
using ImageRotater.Models;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    // Covers are driven two ways at once, and both must stay active.
    //
    // Writing Game.CoverImage feeds details view, the Desktop grid and
    // everything else. The cover control renders only where a theme places its
    // element, which is one Fullscreen grid template - the one place the write
    // cannot reach, because those tiles bind Playnite's native PART_ImageCover
    // and hold its cached bitmap.
    //
    // Treating them as alternatives was a real regression: standing the write
    // down whenever a theme hosted the control froze covers everywhere the
    // control does not render. The control picking independently for its own
    // tile is a second pick, not a conflicting one - nothing else draws from
    // it, so the two cannot disagree on screen.
    [TestFixture]
    public class CoverControlHandoffTests
    {
        private class CountingWriter : PlayniteBackgroundWriter
        {
            public readonly List<ArtworkKind> Kinds = new List<ArtworkKind>();

            public CountingWriter(string dir) : base(null, dir) { }

            public override bool SetArtwork(Game game, string imagePath, ArtworkKind kind)
            {
                Kinds.Add(kind);
                return true;
            }
        }

        private class StubSource : IBackgroundImageSource
        {
            private readonly IReadOnlyList<string> _paths;
            public StubSource(IReadOnlyList<string> paths) { _paths = paths; }
            public IReadOnlyList<string> GetImagePaths(Game game) { return _paths; }
        }

        private string _root;
        private GameImageStore _store;
        private CountingWriter _writer;
        private Game _game;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "ImageRotaterHandoff_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);

            _store = new GameImageStore(_root);
            _writer = new CountingWriter(_root);
            _game = new Game { Id = Guid.NewGuid(), Name = "Game" };

            // Both kinds present, so a missing write is a real decision rather
            // than an empty candidate list.
            foreach (ArtworkKind kind in new[] { ArtworkKind.Background, ArtworkKind.Cover })
            {
                string src = Path.Combine(_root, kind + ".png");
                File.WriteAllBytes(src, new byte[] { 1, 2, 3, 4 });
                _store.AddImage(_game.Id, src, kind);
            }
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        private BackgroundRotationService Build(ImageRotaterSettings settings)
        {
            var backgrounds = new StubSource(_store.GetImagePaths(_game.Id, ArtworkKind.Background));
            var covers = new StubSource(_store.GetImagePaths(_game.Id, ArtworkKind.Cover));

            return new BackgroundRotationService(
                backgrounds, covers,
                new ImageSelector(new ImagePicker(), new SessionSelectionCache()),
                _writer, null, _store, () => settings);
        }

        private static ImageRotaterSettings Settings(bool rotateCovers, bool useCoverControl)
        {
            return new ImageRotaterSettings
            {
                EnableRotation = true,
                DisplayMode = DisplayMode.UpdatePlayniteBackground,
                SelectionMode = SelectionMode.EverySelection,
                RotateCovers = rotateCovers,
                UseCoverControl = useCoverControl
            };
        }

        // Covers are written whether or not a theme hosts the cover control.
        //
        // The two are not alternatives, and treating them as such was a real
        // regression: Game.CoverImage feeds details view, the Desktop grid and
        // everything else, while the control renders only where a theme places
        // it. Standing the write down on the control's behalf froze covers
        // everywhere the control does not reach.
        [Test]
        public void CoverControlEnabled_WriteStillHappens()
        {
            Build(Settings(rotateCovers: true, useCoverControl: true)).ApplyTo(_game);

            CollectionAssert.Contains(_writer.Kinds, ArtworkKind.Cover,
                "the control reaches only one grid template; every other view needs the write");
        }

        [Test]
        public void CoverControlDisabled_WriteStillHappens()
        {
            Build(Settings(rotateCovers: true, useCoverControl: false)).ApplyTo(_game);

            CollectionAssert.Contains(_writer.Kinds, ArtworkKind.Cover);
        }

        [Test]
        public void BackgroundsAreAlwaysWritten()
        {
            Build(Settings(rotateCovers: true, useCoverControl: true)).ApplyTo(_game);

            CollectionAssert.Contains(_writer.Kinds, ArtworkKind.Background,
                "the cover control must not disable background rotation");
        }

        // The slideshow tick: the game has stayed selected and the timer says
        // "next image". Both the repeat guard ("already did this game") and
        // Session mode ("keep this pick") exist to PREVENT that, so ApplyNext
        // must override both - and pick a different image, or the slideshow
        // visibly does nothing.
        [Test]
        public void ApplyNext_RotatesPastTheGuardAndSessionMode()
        {
            // A second background, so a forced re-pick has somewhere to go.
            string extra = Path.Combine(_root, "second.png");
            File.WriteAllBytes(extra, new byte[] { 5, 6, 7, 8 });
            _store.AddImage(_game.Id, extra, ArtworkKind.Background);

            var settings = Settings(rotateCovers: false, useCoverControl: false);
            settings.SelectionMode = SelectionMode.Session;

            var service = Build(settings);

            service.ApplyTo(_game);
            int First() { return _writer.Kinds.FindAll(k => k == ArtworkKind.Background).Count; }
            int first = First();

            service.ApplyTo(_game);
            Assert.AreEqual(first, First(), "the guard should suppress a repeat selection");

            service.ApplyNext(_game, ArtworkKind.Background);
            Assert.AreEqual(first + 1, First(),
                "a slideshow tick must rotate despite the guard and Session mode");
        }

        [Test]
        public void RotateCoversOff_NeitherPathRuns()
        {
            Build(Settings(rotateCovers: false, useCoverControl: true)).ApplyTo(_game);

            CollectionAssert.DoesNotContain(_writer.Kinds, ArtworkKind.Cover);
        }

        // Fullscreen grid tiles bind this path directly, so it must be set
        // whenever a cover is chosen - that binding is the only thing making
        // those tiles rotate.
        [Test]
        public void ChoosingACover_PublishesItsPath()
        {
            var settings = Settings(rotateCovers: true, useCoverControl: false);
            Build(settings).ApplyTo(_game);

            Assert.IsNotEmpty(settings.CurrentCoverPath ?? string.Empty,
                "the chosen cover path must be published for themes to bind");
            StringAssert.Contains("covers", settings.CurrentCoverPath);
        }

        // The write is skipped when the pick has not changed. The path must
        // still be published, or a theme would bind an empty string.
        [Test]
        public void UnchangedPick_StillPublishesThePath()
        {
            var settings = Settings(rotateCovers: true, useCoverControl: false);
            settings.SelectionMode = SelectionMode.Session;

            var service = Build(settings);
            service.ApplyTo(_game);
            string first = settings.CurrentCoverPath;

            settings.CurrentCoverPath = string.Empty;
            service.Forget(_game.Id);
            service.ApplyTo(_game);

            Assert.AreEqual(first, settings.CurrentCoverPath,
                "a skipped write must not leave the published path empty");
        }

        // Themes compare this against their own tile's game id to decide
        // whether the published path applies to them. If the two ever went out
        // of step, one game's cover would appear on another game's tile.
        [Test]
        public void PublishedPathAndGameId_AlwaysMatchTheSameGame()
        {
            var settings = Settings(rotateCovers: true, useCoverControl: false);
            Build(settings).ApplyTo(_game);

            Assert.AreEqual(_game.Id.ToString(), settings.CurrentCoverGameId,
                "the published id must name the game whose cover was published");
            StringAssert.Contains(_game.Id.ToString("N"), settings.CurrentCoverPath.Replace("-", ""),
                "the published path must belong to that same game's folder");
        }

        // The bug that started this round: selecting the theme-element display
        // mode returned before any work, so backgrounds silently stopped
        // rotating everywhere - including in themes that render nothing.
        [Test]
        public void ThemeElementDisplayMode_StillRotatesBackgrounds()
        {
            var settings = Settings(rotateCovers: true, useCoverControl: false);
            settings.DisplayMode = DisplayMode.ThemeElement;

            Build(settings).ApplyTo(_game);

            CollectionAssert.Contains(_writer.Kinds, ArtworkKind.Background,
                "DisplayMode must not be able to switch rotation off entirely");
        }

        // EnableCoverImage is what themes bind to decide whether to collapse
        // their own native cover element. It must be true only when we will
        // actually render one in its place.
        [Test]
        public void EnableCoverImage_RequiresBothRotateCoversAndTheControl()
        {
            Assert.IsTrue(Settings(true, true).EnableCoverImage);
            Assert.IsFalse(Settings(true, false).EnableCoverImage,
                "control disabled - the theme must keep its own cover");
            Assert.IsFalse(Settings(false, true).EnableCoverImage,
                "covers not rotating - the theme must keep its own cover");
            Assert.IsFalse(Settings(false, false).EnableCoverImage);
        }

        // Themes bind EnableCoverImage, so a change to either underlying
        // setting has to raise a notification for it or the tile keeps hiding
        // (or showing) the wrong element.
        [Test]
        public void EnableCoverImage_NotifiesWhenItsInputsChange()
        {
            foreach (string input in new[] { "RotateCovers", "UseCoverControl" })
            {
                var settings = Settings(rotateCovers: false, useCoverControl: false);

                var raised = new List<string>();
                settings.PropertyChanged += (s, e) => raised.Add(e.PropertyName);

                if (input == "RotateCovers")
                {
                    settings.RotateCovers = true;
                }
                else
                {
                    settings.UseCoverControl = true;
                }

                CollectionAssert.Contains(raised, "EnableCoverImage",
                    $"changing {input} must notify the derived EnableCoverImage");
            }
        }

        // HasDataCover gates the theme per game. It has to actually notify, or
        // a tile would keep the previous game's answer.
        [Test]
        public void HasDataCover_NotifiesOnChangeOnly()
        {
            var settings = Settings(true, true);
            settings.HasDataCover = false;

            var raised = new List<string>();
            settings.PropertyChanged += (s, e) => raised.Add(e.PropertyName);

            settings.HasDataCover = true;
            CollectionAssert.Contains(raised, "HasDataCover");

            raised.Clear();
            settings.HasDataCover = true;
            CollectionAssert.DoesNotContain(raised, "HasDataCover",
                "an unchanged value should not churn theme bindings");
        }
    }
}
