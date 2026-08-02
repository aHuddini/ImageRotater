using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    // Playnite's library store names files by GUID with whatever extension the
    // original download carried - frequently none, sometimes something
    // meaningless like ".php". Preserving that name verbatim made the user's
    // own artwork invisible to GetImagePaths, which filters on supported
    // extensions.
    //
    // Two silent failures came from that: the original never became a rotation
    // candidate, and the game got no published file - which then threw
    // FileNotFoundException inside Playnite's layout pass for themes that load
    // with CacheOption=OnLoad.
    [TestFixture]
    public class PreservedExtensionTests
    {
        private string _dir;
        private MethodInfo _detect;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ImageRotaterExt_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);

            _detect = typeof(OriginalArtPreserver).GetMethod(
                "DetectExtension", BindingFlags.NonPublic | BindingFlags.Static);

            Assert.IsNotNull(_detect, "DetectExtension is the behaviour under test");
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private string Detect(string name, byte[] header)
        {
            string path = Path.Combine(_dir, name);
            File.WriteAllBytes(path, header);
            return (string)_detect.Invoke(null, new object[] { path });
        }

        // The exact file that crashed Fullscreen: JPEG bytes, no extension.
        [Test]
        public void JpegWithNoExtension_IsDetected()
        {
            Assert.AreEqual(".jpg",
                Detect("original_19f2761e", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0x4A, 0x46 }));
        }

        // The other one: JPEG bytes wearing a ".php" extension.
        [Test]
        public void JpegNamedPhp_IsDetectedByContent()
        {
            Assert.AreEqual(".jpg",
                Detect("original_1d29874b.php", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0x4A, 0x46 }));
        }

        [Test]
        public void CommonFormats_AreDetected()
        {
            Assert.AreEqual(".png",
                Detect("a", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));
            Assert.AreEqual(".gif",
                Detect("b", new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }));
            Assert.AreEqual(".bmp",
                Detect("c", new byte[] { 0x42, 0x4D, 0, 0, 0, 0, 0, 0 }));
            Assert.AreEqual(".webp",
                Detect("d", new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 }));
        }

        // Unknown content must still produce a usable name rather than an
        // empty extension, which would reintroduce the invisible-file bug.
        [Test]
        public void UnrecognisedContent_FallsBackToAUsableExtension()
        {
            Assert.AreEqual(".jpg", Detect("keeps-its-name.jpg", new byte[] { 1, 2, 3, 4 }),
                "an unrecognised header should keep a sensible existing extension");
            Assert.AreEqual(".png", Detect("nameless", new byte[] { 1, 2, 3, 4 }),
                "no header match and no extension must still yield something readable");
        }

        [Test]
        public void EmptyFile_DoesNotThrow()
        {
            Assert.DoesNotThrow(() => Detect("empty", new byte[0]));
        }

        // Whatever is detected has to be something the store will actually list
        // as a candidate - that is the entire point.
        [Test]
        public void DetectedExtensions_AreAllSupportedByTheStore()
        {
            string root = Path.Combine(_dir, "store");
            var store = new GameImageStore(root);
            Guid gameId = Guid.NewGuid();

            string jpeg = Path.Combine(_dir, "original_headerless");
            File.WriteAllBytes(jpeg, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0x4A, 0x46 });

            string detected = (string)_detect.Invoke(null, new object[] { jpeg });
            string named = Path.Combine(_dir, "original_headerless" + detected);
            File.Copy(jpeg, named, true);

            store.AddImage(gameId, named, global::ImageRotater.Models.ArtworkKind.Cover);

            Assert.AreEqual(1, store.GetImagePaths(gameId, global::ImageRotater.Models.ArtworkKind.Cover).Count,
                "a preserved original must be visible to the store as a rotation candidate");
        }
    }
}
