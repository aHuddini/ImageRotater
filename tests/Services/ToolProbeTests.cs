using System;
using System.IO;
using NUnit.Framework;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    // Locating and validating the external tools.
    //
    // ffmpeg and yt-dlp are both GPL and this plugin is MIT, so neither can be
    // bundled - the plugin uses what the user already has. That makes "is it
    // there, and does it work" a real question rather than an assumption, and
    // the answer decides whether a feature is offered or explained away.
    [TestFixture]
    public class ToolProbeTests
    {
        private string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ImageRotaterTool_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        [Test]
        public void AnExplicitPathToTheExecutableIsUsed()
        {
            string exe = Path.Combine(_dir, "ffmpeg.exe");
            File.WriteAllBytes(exe, new byte[] { 1 });

            Assert.AreEqual(exe, ExternalTool.Resolve(exe, "ffmpeg.exe"));
        }

        // Browsing to the folder rather than the file is the likelier mistake,
        // so it resolves rather than failing.
        [Test]
        public void ADirectoryContainingTheExecutableIsUsed()
        {
            string exe = Path.Combine(_dir, "ffmpeg.exe");
            File.WriteAllBytes(exe, new byte[] { 1 });

            Assert.AreEqual(exe, ExternalTool.Resolve(_dir, "ffmpeg.exe"));
        }

        // A wrong explicit path must NOT silently fall back to PATH.
        //
        // The user set that path on purpose; quietly using a different copy
        // hides their mistake and makes the status line a lie.
        [Test]
        public void AWrongExplicitPathDoesNotFallBackToPath()
        {
            string missing = Path.Combine(_dir, "nowhere", "ffmpeg.exe");

            Assert.IsNull(ExternalTool.Resolve(missing, "ffmpeg.exe"),
                "falling back would hide the user's own typo behind a working feature");
        }

        [Test]
        public void AnEmptyPathSearchesPath()
        {
            // Whatever the machine has - the point is that an empty setting
            // means "look for it" rather than "there is none".
            string found = ExternalTool.Resolve(string.Empty, "cmd.exe");

            Assert.IsNotNull(found, "cmd.exe is on PATH on every Windows install");
            Assert.IsTrue(File.Exists(found));
        }

        // File.Exists is not enough: a corrupt download, a wrong-architecture
        // build or a zero-byte placeholder all pass it and then fail at the
        // moment the user wanted the feature.
        [Test]
        public void AFileThatWillNotRunIsReportedAsSuch()
        {
            string fake = Path.Combine(_dir, "ffmpeg.exe");
            File.WriteAllText(fake, "this is not an executable");

            var probe = new ToolProbe();

            Assert.AreEqual("Will not run", probe.Probe(fake, ToolProbe.FfmpegVersionFlag));
            Assert.IsFalse(probe.Works(fake, ToolProbe.FfmpegVersionFlag));
        }

        [Test]
        public void AMissingToolIsNotFound()
        {
            var probe = new ToolProbe();

            Assert.AreEqual("Not found", probe.Probe(null, ToolProbe.FfmpegVersionFlag));
            Assert.AreEqual("Not found", probe.Probe(string.Empty, ToolProbe.FfmpegVersionFlag));
            Assert.AreEqual("Not found",
                probe.Probe(Path.Combine(_dir, "absent.exe"), ToolProbe.FfmpegVersionFlag));
        }

        // A real executable that runs and reports a version.
        [Test]
        public void AWorkingToolReportsFound()
        {
            string cmd = ExternalTool.FindOnPath("cmd.exe");
            Assert.IsNotNull(cmd);

            var probe = new ToolProbe();

            // /c exit runs and returns 0 without printing a version, which is
            // the "ran but the version line was unfamiliar" case.
            Assert.IsTrue(probe.Works(cmd, "/c exit"),
                "exit code 0 is what decides the tool works");
        }

        // ffmpeg needs a SINGLE dash. Asking with "--version" exits non-zero
        // and prints to stderr, so the wrong flag reports a working install as
        // broken - a detail worth pinning down in a test rather than a comment.
        [Test]
        public void TheVersionFlagsAreTheOnesEachToolActuallyAccepts()
        {
            Assert.AreEqual("-version", ToolProbe.FfmpegVersionFlag);
            Assert.AreEqual("--version", ToolProbe.YtDlpVersionFlag);
        }
    }
}
