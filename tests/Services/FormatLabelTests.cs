using NUnit.Framework;
using ImageRotater.Models;

namespace ImageRotater.Tests.Services
{
    // What a search result says it is.
    //
    // Worth getting right because WPF cannot display every format that can be
    // downloaded - a WEBP saves fine and shows as nothing - so the label is how
    // a user tells beforehand. It used to read "web" or "alternate", which
    // answered a different question than the one being asked.
    [TestFixture]
    public class FormatLabelTests
    {
        private static string LabelFor(string url, string mime)
        {
            return new SteamGridDbArtwork { Url = url, Mime = mime }.FormatLabel;
        }

        [TestCase("https://cdn.x.com/a/library_hero.jpg", "image/jpeg", "JPG")]
        [TestCase("https://cdn.x.com/a/logo.png", "image/png", "PNG")]
        [TestCase("https://cdn.x.com/a/thing.webp", "image/webp", "WEBP")]
        public void ExtensionWins(string url, string mime, string expected)
        {
            Assert.AreEqual(expected, LabelFor(url, mime));
        }

        [Test]
        public void CacheBusterIsIgnored()
        {
            // Steam appends ?t=<stamp> to its asset URLs.
            Assert.AreEqual("MP4", LabelFor("https://x.com/a/clip.mp4?t=1758127023", "video/mp4"));
        }

        [Test]
        public void MimeCoversAUrlWithNoExtension()
        {
            // A YouTube watch page has no extension at all.
            Assert.AreEqual("MP4", LabelFor("https://www.youtube.com/watch?v=abc123", "video/mp4"));
        }

        [Test]
        public void ADotInTheDomainIsNotAnExtension()
        {
            // Otherwise "cdn.cloudflare.steamstatic.com/foo" would label as
            // whatever followed the last dot.
            Assert.AreEqual("JPEG", LabelFor("https://cdn.cloudflare.steamstatic.com/foo", "image/jpeg"));
        }

        [Test]
        public void UnknownStaysHonest()
        {
            // Better than guessing: a wrong format label is worse than no
            // answer, because the user acts on it.
            Assert.AreEqual("?", LabelFor("https://x.com/thing", null));
        }
    }
}
