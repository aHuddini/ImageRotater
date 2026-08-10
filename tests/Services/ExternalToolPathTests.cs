using System;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    // Putting deno where yt-dlp can find it.
    //
    // yt-dlp needs a JavaScript runtime to answer YouTube's nsig and PO-token
    // challenges, and looks for one on PATH BY NAME - there is no argument
    // that points it at a specific binary. So the only way to supply the copy
    // the user chose is to hand the child process a PATH with that folder on
    // it.
    //
    // Worth testing because getting it wrong is silent in the worst way: yt-dlp
    // exits 0 and returns an empty result set, which looks like "YouTube has
    // nothing for this game" rather than "a tool is missing".
    [TestFixture]
    public class ExternalToolPathTests
    {
        private string _dir;
        private string _exe;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ir-path-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);

            _exe = Path.Combine(_dir, "deno.exe");
            File.WriteAllText(_exe, "stub");
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        [Test]
        public void ToolFolderIsPutOnTheChildPath()
        {
            var psi = new ProcessStartInfo("yt-dlp.exe");

            ExternalTool.PrependToolDirToPath(psi, _exe);

            Assert.IsTrue(
                psi.EnvironmentVariables["PATH"].StartsWith(_dir, StringComparison.OrdinalIgnoreCase),
                "the tool's folder should come first so the chosen copy wins");
        }

        [Test]
        public void ExistingPathIsKept()
        {
            // Overwriting PATH rather than prepending would strip the child of
            // everything else it needs to run.
            var psi = new ProcessStartInfo("yt-dlp.exe");
            string before = psi.EnvironmentVariables["PATH"];

            ExternalTool.PrependToolDirToPath(psi, _exe);

            Assert.IsTrue(
                psi.EnvironmentVariables["PATH"].EndsWith(before, StringComparison.Ordinal),
                "the inherited PATH should still be there behind the added folder");
        }

        [Test]
        public void OurOwnEnvironmentIsUntouched()
        {
            string before = Environment.GetEnvironmentVariable("PATH");

            ExternalTool.PrependToolDirToPath(new ProcessStartInfo("yt-dlp.exe"), _exe);

            Assert.AreEqual(before, Environment.GetEnvironmentVariable("PATH"));
        }

        [Test]
        public void MissingToolChangesNothing()
        {
            // An unset deno path is the normal state for anyone not using
            // YouTube import, so this runs constantly and must be harmless.
            var psi = new ProcessStartInfo("yt-dlp.exe");
            string before = psi.EnvironmentVariables["PATH"];

            ExternalTool.PrependToolDirToPath(psi, Path.Combine(_dir, "absent.exe"));

            Assert.AreEqual(before, psi.EnvironmentVariables["PATH"]);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void EmptyPathIsIgnored(string configured)
        {
            var psi = new ProcessStartInfo("yt-dlp.exe");
            string before = psi.EnvironmentVariables["PATH"];

            ExternalTool.PrependToolDirToPath(psi, configured);

            Assert.AreEqual(before, psi.EnvironmentVariables["PATH"]);
        }

        [Test]
        public void NullProcessInfoDoesNotThrow()
        {
            Assert.DoesNotThrow(() => ExternalTool.PrependToolDirToPath(null, _exe));
        }

        [Test]
        public void CallingTwiceKeepsBothFolders()
        {
            // ffmpeg and deno can live in different places, and a second call
            // must not discard the first.
            string other = Path.Combine(_dir, "sub");
            Directory.CreateDirectory(other);

            string otherExe = Path.Combine(other, "ffmpeg.exe");
            File.WriteAllText(otherExe, "stub");

            var psi = new ProcessStartInfo("yt-dlp.exe");

            ExternalTool.PrependToolDirToPath(psi, _exe);
            ExternalTool.PrependToolDirToPath(psi, otherExe);

            string path = psi.EnvironmentVariables["PATH"];

            Assert.IsTrue(path.Contains(_dir), "the first folder should survive the second call");
            Assert.IsTrue(path.Contains(other), "the second folder should be there too");
        }
    }
}
