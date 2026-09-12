using System;
using System.Globalization;
using System.Windows.Data;

namespace ImageRotater.Controls
{
    // Binds a group of RadioButtons to a single enum property - SelectionMode,
    // TransitionStyle, any other. Each button passes its own value's name as
    // ConverterParameter and is checked only when the setting equals it.
    public class EnumRadioConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null || parameter == null)
            {
                return false;
            }

            return string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // Only the button being checked writes back. The unchecking of the
            // previous button must not also fire, or it would overwrite the new
            // value with Binding.DoNothing races.
            bool isChecked = value is bool && (bool)value;
            if (!isChecked || parameter == null)
            {
                return Binding.DoNothing;
            }

            try
            {
                return Enum.Parse(targetType, parameter.ToString(), true);
            }
            catch (Exception)
            {
                return Binding.DoNothing;
            }
        }
    }
}
