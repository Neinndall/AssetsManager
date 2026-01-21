using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AssetsManager.Views.Converters
{
    public class EqualityToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null || parameter == null) return Visibility.Collapsed;

            // Handle enums and strings
            string valueString = value.ToString();
            string parameterString = parameter.ToString();

            bool isInverted = parameterString.StartsWith("!");
            if (isInverted) parameterString = parameterString.Substring(1);

            bool isEqual = string.Equals(valueString, parameterString, StringComparison.OrdinalIgnoreCase);

            if (isInverted)
            {
                return isEqual ? Visibility.Collapsed : Visibility.Visible;
            }

            return isEqual ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
