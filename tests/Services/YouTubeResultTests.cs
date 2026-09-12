using NUnit.Framework;
using ImageRotater.Models;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    // What a YouTube search result is allowed to claim about itself.
    //
    // Two separate ways this went wrong, and both looked like "the tile just
    // does not say that": a duration that was parsed correctly and then painted
    // underneath an opaque button, and a resolution that was identical on every
    // single result because it was the thumbnail's rather than the video's.
    [TestFixture]
    public class YouTubeResultTests
    {
        // A real line from `yt-dlp "ytsearch:..." --dump-json --flat-playlist`,
        // captured rather than written by hand. duration comes back as a JSON
        // FLOAT -- 10780.0, not 10780 -- and a parser that reads it as an
        // integer token gets zero, which renders as no duration at all rather
        // than as an error.
        private const string RealSearchLine =
            "{\"_type\":\"url\",\"id\":\"dFV-N9-_-og\","
            + "\"title\":\"Tekken 8 OST Full Soundtrack\","
            + "\"duration\":10780.0,\"duration_string\":\"2:59:40\","
            + "\"view_count\":178989,\"channel\":\"OP Music\","
            + "\"uploader\":\"OP Music\",\"width\":null,\"height\":null}";

        [Test]
        public void ParsesADurationThatArrivesAsAFloat()
        {
            YouTubeVideo video = YouTubeSearch.Parse(RealSearchLine);

            Assert.IsNotNull(video, "the line did not parse at all");
            Assert.AreEqual(10780, video.DurationSeconds,
                "duration is a JSON float in flat-playlist output; read as an integer "
                + "token it silently becomes 0, and an empty badge looks like a layout bug");
            Assert.AreEqual("2:59:40", video.DurationText);
        }

        [Test]
        public void ParsesTheViewCount()
        {
            YouTubeVideo video = YouTubeSearch.Parse(RealSearchLine);

            Assert.AreEqual(178989, video.ViewCount);
            StringAssert.Contains("K views", video.ViewCountText);
        }

        // flat-playlist carries no width or height at all -- both are null in
        // the captured line above. Nothing may present a number as the video's
        // resolution, because there is none to present.
        [Test]
        public void YouTubeTileDoesNotPrintTheThumbnailSizeAsTheVideosResolution()
        {
            var item = new SteamGridDbArtwork
            {
                IsYouTube = true,
                Width = 480,
                Height = 360,
                DurationText = "2:59:40",
                ViewCountText = "179K views"
            };

            Assert.AreNotEqual(item.Dimensions, item.CaptionText,
                "480x360 is hqdefault.jpg's size and is identical for every video on "
                + "YouTube - printing it makes every result claim the same resolution");

            Assert.AreEqual("179K views", item.CaptionText,
                "views is the one number that is both known from the search and "
                + "different between results");
        }

        // Everything that is not YouTube still states its real size.
        [Test]
        public void OtherSourcesStillPrintTheirDimensions()
        {
            var item = new SteamGridDbArtwork { Width = 1920, Height = 620 };

            Assert.AreEqual("1920x620", item.CaptionText);
        }

        // The duration badge and the preview button both sit in the tile's
        // overlay grid. A later sibling paints over an earlier one, so sharing a
        // corner means one of them is invisible - which is what happened, for
        // every YouTube result, from the day the badge was added.
        [Test]
        public void DurationBadgeAndPreviewButtonDoNotShareACorner()
        {
            string xaml = System.IO.File.ReadAllText(TileXamlPath());

            int badge = xaml.IndexOf("{Binding DurationText}");
            Assert.Greater(badge, -1, "the duration badge has gone");

            // The alignment pair immediately preceding the badge's binding.
            string before = xaml.Substring(0, badge);
            int border = before.LastIndexOf("<Border");
            string badgeTag = xaml.Substring(border, badge - border);

            int button = xaml.IndexOf("Click=\"Preview_Click\"");
            Assert.Greater(button, -1, "the preview button has gone");

            string buttonTag = xaml.Substring(button, System.Math.Min(400, xaml.Length - button));

            bool badgeBottomRight = badgeTag.Contains("HorizontalAlignment=\"Right\"")
                && badgeTag.Contains("VerticalAlignment=\"Bottom\"");
            bool buttonBottomRight = buttonTag.Contains("HorizontalAlignment=\"Right\"")
                && buttonTag.Contains("VerticalAlignment=\"Bottom\"");

            Assert.IsFalse(badgeBottomRight && buttonBottomRight,
                "the duration badge and the preview button are both bottom-right in the "
                + "same grid cell; the button is opaque and declared later, so it paints "
                + "over the badge and the duration is never visible");
        }

        private static string TileXamlPath()
        {
            var dir = new System.IO.DirectoryInfo(TestContext.CurrentContext.TestDirectory);

            while (dir != null &&
                   !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "src")))
            {
                dir = dir.Parent;
            }

            Assert.IsNotNull(dir, "could not locate src from the test directory");

            string path = System.IO.Path.Combine(
                dir.FullName, "src", "Controls", "SteamGridDbSearchView.xaml");

            Assert.IsTrue(System.IO.File.Exists(path), path + " not found");
            return path;
        }
    }
}
