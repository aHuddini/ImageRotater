using System;
using System.Globalization;
using System.Windows.Data;

namespace ImageRotater.Controls
{
    // Binds a group of RadioButtons to a single SelectionMode property.
    // Each button passes its own mode name as ConverterParameter and is checked
    // only when the setting equals it.
    public class SelectionModeConverter : IValueConverter
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

            SelectionMode mode;
            if (Enum.TryParse(parameter.ToString(), true, out mode))
            {
                return mode;
            }

            return Binding.DoNothing;
        }
    }
}
