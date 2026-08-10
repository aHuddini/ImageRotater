using NUnit.Framework;
using ImageRotater;

namespace ImageRotater.Tests.Services
{
    // The Setup tab's status lines.
    //
    // Worth a test for one specific reason: these are the only non-ASCII
    // characters in the project, and a C# file saved as UTF-8 WITHOUT a byte
    // order mark is read as ANSI by the compiler. The source looks right in
    // every editor and the build succeeds, but the tick reaches the screen as
    // a question mark. Asserting the codepoint catches that; reading the file
    // does not.
    [TestFixture]
    public class SetupStatusTests
    {
        private const char Tick = '✓';
        private const char Cross = '✗';

        [Test]
        public void OkShowsATick()
        {
            var status = SetupStatus.Ok("Found - v8.0");

            Assert.AreEqual(Tick.ToString(), status.Glyph);
            Assert.AreEqual("Found - v8.0", status.Message);
        }

        [Test]
        public void ProblemShowsACross()
        {
            var status = SetupStatus.Problem("Will not run");

            Assert.AreEqual(Cross.ToString(), status.Glyph);
        }

        [Test]
        public void NeutralShowsNoGlyph()
        {
            // Absent is not a failure: every one of these tools is optional, so
            // a red cross against a user who never wanted YouTube import would
            // read as something being broken.
            var status = SetupStatus.Neutral("No key set.");

            Assert.IsEmpty(status.Glyph);
            Assert.AreEqual("Gray", status.Brush);
        }

        [Test]
        public void EachStateHasItsOwnColour()
        {
            Assert.AreNotEqual(
                SetupStatus.Ok("a").Brush,
                SetupStatus.Problem("b").Brush);

            Assert.AreNotEqual(
                SetupStatus.Ok("a").Brush,
                SetupStatus.Neutral("c").Brush);
        }

        [Test]
        public void BrushesAreParseableColours()
        {
            // Bound straight to Foreground, where WPF runs them through
            // BrushConverter. A typo in the hex does not throw - it renders
            // black, which on a dark theme is invisible.
            foreach (var status in new[]
            {
                SetupStatus.Ok("a"),
                SetupStatus.Problem("b"),
                SetupStatus.Neutral("c")
            })
            {
                var brush = new System.Windows.Media.BrushConverter()
                    .ConvertFromString(status.Brush);

                Assert.IsNotNull(brush, status.Brush + " should be a usable colour");
            }
        }
    }
}
