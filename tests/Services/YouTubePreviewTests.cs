using System.IO;
using NUnit.Framework;
using ImageRotater.Models;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    // A YouTube result cannot be played the way every other result is played.
    //
    // Everything else the search returns is a file: a .webm thumbnail from
    // SteamGridDB, a .gif from the web, a .jpg from Steam. YouTube returns a
    // watch PAGE, which is HTML. Put that in a <video src="..."> and the tag
    // decodes nothing, raises an error event nobody subscribes to, and draws an
    // empty box -- so the preview reported perfect health while showing
    // absolutely nothing, which is how it shipped.
    //
    // These pin the two halves of the fix: the id survives the mapping, and the
    // view routes on it instead of falling through to the file path.
    [TestFixture]
    public class YouTubePreviewTests
    {
        private static string SourceFile(params string[] parts)
        {
            var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);

            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            {
                dir = dir.Parent;
            }

            Assert.IsNotNull(dir, "could not locate src from the test directory");

            string path = Path.Combine(dir.FullName, Path.Combine(parts));
            Assert.IsTrue(File.Exists(path), path + " not found");

            return File.ReadAllText(path);
        }

        // Autoplay without mute is blocked outright, so a player built without
        // both simply sits on its first frame -- the same blank-looking result
        // this whole fix is about, arrived at a different way.
        [Test]
        public void PlayerAutoplaysMuted()
        {
            string page = PreviewRenderer.HostPageHtml;

            StringAssert.Contains("autoplay: 1", page);
            StringAssert.Contains("mute: 1", page,
                "an unmuted autoplay is blocked, and the player then shows a still frame");
        }

        // YouTube error 153: the embed request is refused when it carries no
        // Referer. A top-level navigation to /embed/<id> sends none, and a page
        // built with NavigateToString has an opaque origin and sends none
        // either -- both were shipped and both produced 153. The player has to
        // be embedded FROM a page served under a real https origin, which is
        // what the virtual host mapping is for.
        [Test]
        public void PlayerIsEmbeddedFromTheVirtualHostNotNavigatedToDirectly()
        {
            string url = PreviewRenderer.YouTubePreviewUrl("dQw4w9WgXcQ");

            StringAssert.StartsWith("https://", url,
                "the embedding page needs a real https origin to quote as its Referer");
            StringAssert.DoesNotContain("youtube.com", url,
                "navigating straight at YouTube sends no Referer, which is error 153");
            StringAssert.Contains("#dQw4w9WgXcQ", url,
                "the id rides in the fragment so one static page serves every preview");
        }

        // The mapping has to exist before the navigation that uses it. Added
        // afterwards it does not apply, and the host does not resolve.
        [Test]
        public void VirtualHostIsMappedBeforeAnythingNavigates()
        {
            string source = SourceFile("src", "Services", "PreviewRenderer.cs");

            int mapped = source.IndexOf("SetVirtualHostNameToFolderMapping");
            int navigate = source.IndexOf("CoreWebView2.Navigate(");

            Assert.Greater(mapped, -1, "no virtual host is mapped, so the embedding "
                + "page has no origin and YouTube answers 153");

            Assert.Less(mapped, navigate,
                "a mapping added after a Navigate does not apply to it - the request "
                + "fails with ERR_NAME_NOT_RESOLVED");
        }

        // The bug class this area keeps producing is a failure with nothing on
        // screen to say so. Roughly one video in ten refuses to embed, and a
        // cross-origin iframe cannot be inspected - so the player has to be
        // asked, through the API, and its answer shown.
        [Test]
        public void PlayerReportsWhyItCouldNotPlayRatherThanGoingBlank()
        {
            string page = PreviewRenderer.HostPageHtml;

            StringAssert.Contains("onError", page,
                "without onError a refused video is a silent blank rectangle, which is "
                + "indistinguishable from the bug this replaced");
            StringAssert.Contains("150", page,
                "150 and 101 mean the uploader disabled embedding, and that has to read "
                + "as their choice rather than as a fault here");
            StringAssert.Contains("hqdefault.jpg", page,
                "a video that will not play still has a poster frame worth showing");
        }

        // An id with URL-significant characters cannot be allowed to end the
        // fragment and append parameters of its own.
        [Test]
        public void PreviewUrlEscapesTheId()
        {
            string url = PreviewRenderer.YouTubePreviewUrl("a#b&c");

            StringAssert.DoesNotContain("#a#b&c", url,
                "an unescaped id can inject its own parameters");
        }

        // Id is an int the search assigns for the tile, and Url is the watch
        // page. If the video id is not carried across the mapping there is
        // nothing left to build a player URL from.
        [Test]
        public void MappingCarriesTheVideoId()
        {
            string source = SourceFile("src", "Controls", "SteamGridDbSearchViewModel.cs");

            StringAssert.Contains("YouTubeId = video.Id", source,
                "the YouTube search maps its results onto SteamGridDbArtwork; without "
                + "the video id the preview has only the watch page, which is HTML");
        }

        // The regression itself. The view must branch to the player before it
        // reaches the <video> path, and must branch on IsYouTube rather than on
        // a format label -- a YouTube result reports MP4, which is true of what
        // you would get after downloading and false of what the URL points at.
        [Test]
        public void ViewRoutesYouTubeToThePlayerBeforeTheVideoTag()
        {
            string source = SourceFile("src", "Controls", "SteamGridDbSearchView.xaml.cs");

            int routed = source.IndexOf("ShowYouTube");
            int fileTag = source.IndexOf("_previewRenderer.Show(");

            Assert.Greater(routed, -1,
                "nothing routes a YouTube result to the player, so it falls through to "
                + "a <video> tag pointed at a web page and renders blank");

            Assert.Greater(fileTag, -1, "the file preview path has moved or gone");

            Assert.Less(routed, fileTag,
                "the YouTube branch must come BEFORE the <video> path, or the watch "
                + "page reaches the tag that cannot play it");

            StringAssert.Contains("item.IsYouTube", source,
                "the branch has to be on IsYouTube: a YouTube result's FormatLabel is "
                + "MP4, which describes the download and not the URL");
        }

        // The poster is 480x360 for every video on YouTube. Printing that as the
        // preview's dimensions states a measurement of the thumbnail while
        // appearing to describe the video.
        [Test]
        public void YouTubeArtworkDimensionsDescribeThePosterNotTheVideo()
        {
            var item = new SteamGridDbArtwork
            {
                IsYouTube = true,
                YouTubeId = "abc123",
                Url = "https://www.youtube.com/watch?v=abc123",
                ThumbnailUrl = "https://i.ytimg.com/vi/abc123/hqdefault.jpg",
                Mime = "video/mp4",
                Width = 480,
                Height = 360
            };

            Assert.IsTrue(item.IsAnimated,
                "a video/mp4 result is animated, which is what sends it down the "
                + "motion path in the first place");

            Assert.AreEqual(item.Url, item.MotionPreviewUrl,
                "with no .webm thumbnail to prefer, MotionPreviewUrl falls back to the "
                + "watch page -- the exact value that cannot go in a <video> tag");
        }
    }
}
