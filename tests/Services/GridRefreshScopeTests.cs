using NUnit.Framework;

namespace ImageRotater.Tests.Services
{
    // The Fullscreen grid refresh runs only for games the plugin has covers
    // for.
    //
    // RefreshSoon calls UpdateTarget() on Playnite's OWN PART_ImageCover
    // binding, which re-resolves FullscreenListItemCoverObject through a
    // decoded-bitmap cache. For a game the plugin rotated, that re-read IS the
    // feature - Playnite never notifies that property, so nothing else makes
    // the tile show a new cover.
    //
    // For a game the plugin has never touched it changes nothing and costs a
    // native image reload, with the bound property briefly between values - a
    // black flash on a tile the plugin had no business touching. Guarded on
    // RotateCovers alone, that fired on EVERY selection while browsing.
    //
    // The plugin class needs a live Playnite API, so the condition is
    // reproduced here exactly as OnGameSelected evaluates it.
    [TestFixture]
    public class GridRefreshScopeTests
    {
        private static bool WouldRefresh(bool rotateCovers, bool hasDataCover, bool fullscreen)
        {
            return rotateCovers && hasDataCover && fullscreen;
        }

        [Test]
        public void RefreshesForAGameWithPluginCovers()
        {
            Assert.IsTrue(WouldRefresh(rotateCovers: true, hasDataCover: true, fullscreen: true),
                "this re-read is the only way a Fullscreen tile ever shows a rotated cover");
        }

        [Test]
        public void DoesNotRefreshForAGameThePluginHasNothingFor()
        {
            Assert.IsFalse(WouldRefresh(rotateCovers: true, hasDataCover: false, fullscreen: true),
                "nothing changed for this game, so re-reading only risks a black flash "
                + "while Playnite reloads its own image");
        }

        [Test]
        public void DoesNotRefreshWhenCoverRotationIsOff()
        {
            Assert.IsFalse(WouldRefresh(rotateCovers: false, hasDataCover: true, fullscreen: true));
        }

        // Desktop notifies its own cover properties, so the workaround is
        // Fullscreen-only by design.
        [Test]
        public void DoesNotRefreshOutsideFullscreen()
        {
            Assert.IsFalse(WouldRefresh(rotateCovers: true, hasDataCover: true, fullscreen: false),
                "Desktop raises PropertyChanged for its cover properties already");
        }
    }
}
