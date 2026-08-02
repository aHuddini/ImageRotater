using System;
using NUnit.Framework;

namespace ImageRotater.Tests.Services
{
    // The control takes a Func<ImageRotaterSettings>, not a settings object,
    // because saving settings replaces the whole object. These pin that an
    // accessor of the shape used in ImageRotater.cs survives that swap - a
    // captured reference would read stale values forever and the
    // EnableRotation toggle would appear dead after the first save.
    [TestFixture]
    public class SettingsAccessorTests
    {
        // Stands in for ImageRotaterSettingsViewModel.Settings: the one mutable
        // link in the chain, reassigned by CancelEdit.
        private class Holder
        {
            public ImageRotaterSettings Settings { get; set; }
        }

        [Test]
        public void Accessor_SeesSettingsObjectSwap()
        {
            var holder = new Holder { Settings = new ImageRotaterSettings { EnableRotation = true } };
            Func<ImageRotaterSettings> accessor = () => holder.Settings;

            Assert.IsTrue(accessor().EnableRotation);

            // A settings save/cancel hands over an entirely new object.
            holder.Settings = new ImageRotaterSettings { EnableRotation = false };

            Assert.IsFalse(accessor().EnableRotation, "accessor read a stale settings object");
        }

        // Contrast: what capturing the object by reference would have done.
        [Test]
        public void CapturedReference_GoesStaleAcrossSwap()
        {
            var holder = new Holder { Settings = new ImageRotaterSettings { EnableRotation = true } };
            ImageRotaterSettings captured = holder.Settings;

            holder.Settings = new ImageRotaterSettings { EnableRotation = false };

            Assert.IsTrue(captured.EnableRotation, "captured reference should still hold the old object");
            Assert.IsFalse(holder.Settings.EnableRotation);
        }
    }
}
