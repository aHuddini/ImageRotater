using NUnit.Framework;
using ImageRotater.Models;

namespace ImageRotater.Tests.Services
{
    [TestFixture]
    public class WidthBucketTests
    {
        [TestCase(1.0, 480)]
        [TestCase(480.0, 480)]
        [TestCase(481.0, 960)]
        [TestCase(960.0, 960)]
        [TestCase(1600.0, 1920)]
        [TestCase(1920.0, 1920)]
        [TestCase(1921.0, 3840)]
        [TestCase(3840.0, 3840)]
        public void ForWidth_RoundsUpToBucket(double measured, int expected)
        {
            Assert.AreEqual(expected, WidthBucket.ForWidth(measured));
        }

        // A 5K/8K display or a bad measurement must not produce a bucket
        // larger than the largest supported decode width.
        [TestCase(5120.0)]
        [TestCase(7680.0)]
        [TestCase(100000.0)]
        public void ForWidth_ClampsAboveLargestBucket(double measured)
        {
            Assert.AreEqual(3840, WidthBucket.ForWidth(measured));
        }

        // Layout can report 0 or NaN before the first measure pass. Never
        // return 0 — a zero DecodePixelWidth means "full size" to WPF, which
        // is the exact bug this whole design exists to prevent.
        [TestCase(0.0)]
        [TestCase(-5.0)]
        [TestCase(double.NaN)]
        [TestCase(double.PositiveInfinity)]
        public void ForWidth_InvalidMeasurementFallsBackToSmallestBucket(double measured)
        {
            Assert.AreEqual(480, WidthBucket.ForWidth(measured));
        }
    }
}
