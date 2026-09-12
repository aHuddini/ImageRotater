using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using NUnit.Framework;
using ImageRotater.Controls;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    // The transition setting reaches three renderers through one static, and
    // the settings page reaches the static through one converter. Both have
    // to agree on every value or a radio button silently selects nothing.
    [TestFixture]
    public class TransitionTests
    {
        private readonly EnumRadioConverter _converter = new EnumRadioConverter();

        [TearDown]
        public void TearDown()
        {
            Transition.Style = TransitionStyle.Crossfade;
        }

        [TestCase(TransitionStyle.Crossfade, false)]
        [TestCase(TransitionStyle.FadeThroughBlack, true)]
        [TestCase(TransitionStyle.FadeThroughWhite, true)]
        [TestCase(TransitionStyle.Cut, false)]
        public void Only_the_colour_styles_flash(TransitionStyle style, bool flash)
        {
            Transition.Style = style;
            Assert.That(Transition.IsFlash, Is.EqualTo(flash));
        }

        [Test]
        public void White_is_white_and_everything_else_is_black()
        {
            Transition.Style = TransitionStyle.FadeThroughWhite;
            Assert.That(Transition.FlashColor, Is.EqualTo(Colors.White));

            Transition.Style = TransitionStyle.FadeThroughBlack;
            Assert.That(Transition.FlashColor, Is.EqualTo(Colors.Black));
        }

        [Test]
        public void Half_is_half_the_duration()
        {
            Assert.That(Transition.Half.TotalMilliseconds * 2, Is.EqualTo(Transition.Duration.TotalMilliseconds));
        }

        [TestCase("Crossfade", TransitionStyle.Crossfade)]
        [TestCase("FadeThroughBlack", TransitionStyle.FadeThroughBlack)]
        [TestCase("fadethroughwhite", TransitionStyle.FadeThroughWhite)]
        [TestCase("Cut", TransitionStyle.Cut)]
        public void Radio_converter_parses_every_transition_by_name(string parameter, TransitionStyle expected)
        {
            object back = _converter.ConvertBack(true, typeof(TransitionStyle), parameter, CultureInfo.InvariantCulture);
            Assert.That(back, Is.EqualTo(expected));

            object isChecked = _converter.Convert(expected, typeof(bool), parameter, CultureInfo.InvariantCulture);
            Assert.That(isChecked, Is.True);
        }

        [Test]
        public void Radio_converter_still_parses_selection_modes()
        {
            object back = _converter.ConvertBack(true, typeof(SelectionMode), "EverySelection", CultureInfo.InvariantCulture);
            Assert.That(back, Is.EqualTo(SelectionMode.EverySelection));
        }

        [Test]
        public void Unchecking_and_unknown_names_write_nothing()
        {
            Assert.That(_converter.ConvertBack(false, typeof(TransitionStyle), "Cut", CultureInfo.InvariantCulture),
                Is.EqualTo(Binding.DoNothing));
            Assert.That(_converter.ConvertBack(true, typeof(TransitionStyle), "Wipe", CultureInfo.InvariantCulture),
                Is.EqualTo(Binding.DoNothing));
        }
    }
}
