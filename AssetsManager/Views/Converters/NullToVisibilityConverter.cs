using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AssetsManager.Views.Converters
{
    public class NullToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool invert = parameter is string str && str.Equals("Invert", StringComparison.OrdinalIgnoreCase);
            bool isNull = value == null;

            if (invert)
            {
                return isNull ? Visibility.Visible : Visibility.Collapsed;
            }

            return isNull ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
