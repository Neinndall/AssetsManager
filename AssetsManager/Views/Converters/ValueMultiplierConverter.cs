using System;
using System.Globalization;
using System.Windows.Data;

namespace AssetsManager.Views.Converters
{
    public class ValueMultiplierConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double val && parameter != null)
            {
                if (double.TryParse(parameter.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double factor))
                {
                    return val * factor;
                }
            }
            return value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double val && parameter != null)
            {
                if (double.TryParse(parameter.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double factor) && factor != 0)
                {
                    return val / factor;
                }
            }
            return value;
        }
    }
}
