using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    // Detecting the mp4s Windows renders as solid black.
    //
    // A YouTube DASH download is a FRAGMENTED mp4: ftyp brand "dash", then
    // moof/mdat fragments. ffmpeg decodes those happily, MediaElement reports
    // them as open and playing - and renders black, with no error anywhere.
    // IsFragmented is the gate for the download-time remux, the per-game
    // repair and the bulk repair, so a wrong answer either leaves black tiles
    // in place or re-writes every healthy video on each run.
    [TestFixture]
    public class FragmentedMp4Tests
    {
        private string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ir-frag-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        // A minimal file whose first atom is "ftyp" with the given brand -
        // which is all the detector reads.
        private string MakeMp4(string name, string brand)
        {
            string path = Path.Combine(_dir, name);

            var bytes = new byte[16];
            bytes[3] = 16; // big-endian atom size
            Encoding.ASCII.GetBytes("ftyp").CopyTo(bytes, 4);
            Encoding.ASCII.GetBytes(brand).CopyTo(bytes, 8);

            File.WriteAllBytes(path, bytes);
            return path;
        }

        [TestCase("dash")]
        [TestCase("iso5")]
        [TestCase("iso6")]
        public void FragmentBrandsAreDetected(string brand)
        {
            // "dash" is what YouTube's own segments carry; iso5/iso6 are the
            // brands fragmented remuxes commonly declare. The real Octopath
            // download read "dash\0\0\0\0iso6avc1mp41".
            Assert.IsTrue(GifConverter.IsFragmented(MakeMp4("frag.mp4", brand)));
        }

        [TestCase("isom")]
        [TestCase("mp42")]
        [TestCase("avc1")]
        public void ProgressiveBrandsPass(string brand)
        {
            // isom is what Steam's trailers and clips carry (verified live),
            // and what ffmpeg's default mp4 mux writes. Flagging these would
            // make every repair rewrite every healthy video, every time.
            Assert.IsFalse(GifConverter.IsFragmented(MakeMp4("fine.mp4", brand)));
        }

        [Test]
        public void NotAnMp4IsNotFlagged()
        {
            string junk = Path.Combine(_dir, "junk.mp4");
            File.WriteAllText(junk, "this is not video");

            Assert.IsFalse(GifConverter.IsFragmented(junk), "no ftyp atom, nothing to repair");
        }

        [Test]
        public void MissingOrTinyFilesAreHandled()
        {
            Assert.IsFalse(GifConverter.IsFragmented(Path.Combine(_dir, "absent.mp4")));

            string tiny = Path.Combine(_dir, "tiny.mp4");
            File.WriteAllBytes(tiny, new byte[] { 0, 0 });

            Assert.IsFalse(GifConverter.IsFragmented(tiny));
        }
    }
}
