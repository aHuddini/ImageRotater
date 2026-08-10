using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using ImageRotater;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    // The settings a user can get wrong.
    //
    // Worth testing rather than eyeballing because Save BLOCKS on whatever
    // this returns. A rule that is too strict traps a user in the settings
    // window over something that was never going to break anything - which is
    // why tool paths are reported live instead of validated here.
    [TestFixture]
    public class SettingsValidatorTests
    {
        private string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ir-validate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        [Test]
        public void DefaultSettingsAreValid()
        {
            // The out-of-the-box state has to pass. A fresh install that
            // cannot save its own defaults is the worst possible first run.
            Assert.IsEmpty(SettingsValidator.Validate(new ImageRotaterSettings()));
        }

        [Test]
        public void NullSettingsDoNotThrow()
        {
            Assert.IsNotEmpty(SettingsValidator.Validate(null));
        }

        // --- API key ---------------------------------------------------

        [Test]
        public void EmptyApiKeyIsValid()
        {
            // Absent is normal: SteamGridDB is one source of several.
            Assert.IsNull(SettingsValidator.CheckApiKey(null));
            Assert.IsNull(SettingsValidator.CheckApiKey(""));
            Assert.IsNull(SettingsValidator.CheckApiKey("   "));
        }

        [Test]
        public void WellFormedApiKeyIsValid()
        {
            Assert.IsNull(SettingsValidator.CheckApiKey(new string('a', 32)));
            Assert.IsNull(SettingsValidator.CheckApiKey("0123456789abcdef0123456789ABCDEF"));
        }

        [Test]
        public void SurroundingWhitespaceIsToleratedOnAKey()
        {
            // Pasting from a web page routinely brings a trailing newline. That
            // is not the user making a mistake.
            Assert.IsNull(SettingsValidator.CheckApiKey("  " + new string('a', 32) + "\r\n"));
        }

        [TestCase("abc")]
        [TestCase("0123456789abcdef0123456789abcde")]
        [TestCase("0123456789abcdef0123456789abcdef0")]
        public void WrongLengthApiKeyIsRejected(string key)
        {
            Assert.IsNotNull(SettingsValidator.CheckApiKey(key));
        }

        [Test]
        public void NonHexApiKeyIsRejected()
        {
            // Right length, wrong alphabet - what you get from grabbing a
            // neighbouring field on the page.
            string key = "zzzz456789abcdef0123456789abcdef";

            Assert.AreEqual(32, key.Length, "test fixture should be the right length");
            Assert.IsNotNull(SettingsValidator.CheckApiKey(key));
        }

        // Tool paths are not validated on Save, deliberately: the status
        // line under each box reports them live, and a wrong path only means
        // the feature stays unavailable. Blocking Save on it would trap a user
        // in the settings window.
        [Test]
        public void WrongToolPathDoesNotBlockSaving()
        {
            var settings = new ImageRotaterSettings
            {
                FfmpegPath = Path.Combine(_dir, "nope", "ffmpeg.exe"),
                YtDlpPath = _dir
            };

            Assert.IsEmpty(SettingsValidator.Validate(settings));
        }

        // --- Slideshow intervals ---------------------------------------

        [TestCase(0)]
        [TestCase(5)]
        [TestCase(30)]
        [TestCase(3600)]
        public void ReasonableIntervalsAreValid(int seconds)
        {
            var settings = new ImageRotaterSettings
            {
                BackgroundSlideshowSeconds = seconds,
                CoverSlideshowSeconds = seconds
            };

            Assert.IsEmpty(SettingsValidator.Validate(settings));
        }

        [Test]
        public void NegativeIntervalIsRejected()
        {
            var settings = new ImageRotaterSettings { BackgroundSlideshowSeconds = -1 };

            Assert.IsNotEmpty(SettingsValidator.Validate(settings));
        }

        [Test]
        public void OnlyUninterpretableValuesBlockSaving()
        {
            // Save BLOCKS on whatever Validate returns, so it holds only values
            // with no sensible reading at all. A malformed API key and an
            // absurd interval are both reported live under their own boxes and
            // are recoverable by editing them - trapping the user in the
            // settings window over either would be worse than letting it save.
            var settings = new ImageRotaterSettings
            {
                SteamGridDbApiKey = "too-short",
                FfmpegPath = Path.Combine(_dir, "nope", "ffmpeg.exe"),
                CoverSlideshowSeconds = 99999
            };

            Assert.IsEmpty(SettingsValidator.Validate(settings));
        }

        [Test]
        public void EachBadFieldReportsSeparately()
        {
            // When there IS more than one blocking problem, save shows them all
            // at once rather than one per attempt.
            var settings = new ImageRotaterSettings
            {
                BackgroundSlideshowSeconds = -5,
                CoverSlideshowSeconds = -1
            };

            Assert.AreEqual(2, SettingsValidator.Validate(settings).Count);
        }
    }
}
