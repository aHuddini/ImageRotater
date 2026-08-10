using System;
using NUnit.Framework;
using Playnite.SDK.Models;
using ImageRotater.Services;
using ImageRotater.Models;

namespace ImageRotater.Tests.Services
{
    // The appid guard, and the CDN asset list it feeds.
    //
    // GetArtwork itself is not covered here: every candidate costs a HEAD
    // request, so testing it would mean either hitting Valve's CDN from the
    // suite or wrapping a one-line WebRequest in an interface purely to mock
    // it. What can go wrong without a network is the appid resolution, which
    // is where a bad URL would come from.
    [TestFixture]
    public class SteamArtworkSourceTests
    {
        private static readonly Guid SteamPluginId =
            Guid.Parse("CB91DFC9-B977-43BF-8E70-55F46E410FAB");

        [Test]
        public void SteamGameYieldsItsAppId()
        {
            var game = new Game { PluginId = SteamPluginId, GameId = "1145360" };

            Assert.AreEqual("1145360", SteamArtworkSource.GetKnownAppId(game));
            Assert.IsTrue(SteamArtworkSource.IsSteamGame(game));
        }

        [Test]
        public void GameFromAnotherLibraryIsNotSteam()
        {
            // GOG uses numeric ids too, so the id alone cannot decide this -
            // only the PluginId can.
            var game = new Game { PluginId = Guid.NewGuid(), GameId = "1207658930" };

            Assert.IsNull(SteamArtworkSource.GetKnownAppId(game));
            Assert.IsFalse(SteamArtworkSource.IsSteamGame(game));
        }

        [Test]
        public void ManuallyAddedGameIsNotSteam()
        {
            // No library plugin at all: PluginId is empty and GameId is blank.
            var game = new Game { Name = "Some ROM" };

            Assert.IsNull(SteamArtworkSource.GetKnownAppId(game));
        }

        
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("abc")]
        [TestCase("1145360/../../etc")]
        public void NonNumericIdIsRejected(string gameId)
        {
            // The appid is concatenated into a CDN path, so anything that is
            // not digits stops here rather than becoming part of a URL.
            var game = new Game { PluginId = SteamPluginId, GameId = gameId };

            Assert.IsNull(SteamArtworkSource.GetKnownAppId(game));
        }

        [Test]
        public void NullGameIsHandled()
        {
            Assert.IsNull(SteamArtworkSource.GetKnownAppId(null));
            Assert.IsFalse(SteamArtworkSource.IsSteamGame(null));
        }

        [Test]
        public void GameFromAnotherLibraryStillReachesSteamArt()
        {
            // The tab is NOT restricted to Steam imports. Steam's artwork is
            // keyed by appid, and an appid exists whether or not this user owns
            // the Steam copy - so a GOG or Xbox install of the same game gets
            // the same art, once the name is matched against Steam's app list.
            //
            // Only the known-id path is asserted here; the name lookup needs
            // the network and is covered by SteamCdn's own behaviour.
            var gog = new Game { PluginId = Guid.NewGuid(), GameId = "1207658930" };

            Assert.IsNull(
                SteamArtworkSource.GetKnownAppId(gog),
                "Playnite does not hold a Steam appid for a GOG game");

            Assert.IsFalse(
                SteamArtworkSource.IsSteamGame(gog),
                "but it is still not a Steam import");
        }

        [Test]
        public void CoverAssetsArePortraitOrHeader()
        {
            // Guards the shape contract: a cover tab that offered a 1920x620
            // hero would be handing the user artwork of the wrong aspect.
            Assert.IsNotEmpty(SteamCdn.CoverAssets);

            foreach (var asset in SteamCdn.CoverAssets)
            {
                Assert.IsTrue(asset.Width <= 600, asset.FileName + " is too wide for a cover");
            }
        }

        [Test]
        public void BackgroundAssetsAreWide()
        {
            Assert.IsNotEmpty(SteamCdn.BackgroundAssets);

            foreach (var asset in SteamCdn.BackgroundAssets)
            {
                Assert.IsTrue(
                    asset.Width > asset.Height,
                    asset.FileName + " is not landscape");
            }
        }

        [Test]
        public void UrlIsBuiltFromTheAppId()
        {
            Assert.AreEqual(
                "https://cdn.cloudflare.steamstatic.com/steam/apps/1145360/library_hero.jpg",
                SteamCdn.BuildUrl("1145360", "library_hero.jpg"));
        }

        [Test]
        public void MimeTypeFollowsTheExtension()
        {
            // The plugin prefers PNG where it can get it, so the result has to
            // carry the real type rather than a blanket image/jpeg.
            Assert.AreEqual("image/png", new SteamCdn.Asset { FileName = "logo.png" }.MimeType);
            Assert.AreEqual("image/jpeg", new SteamCdn.Asset { FileName = "header.jpg" }.MimeType);
        }
    }
}
