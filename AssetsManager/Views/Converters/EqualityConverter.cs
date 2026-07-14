using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AssetsManager.Views.Converters
{
    public class EqualityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null || parameter == null) 
                return targetType == typeof(Visibility) ? Visibility.Collapsed : (object)false;

            // Handle enums and strings
            string valueString = value.ToString();
            string parameterString = parameter.ToString();

            bool isInverted = parameterString.StartsWith("!");
            if (isInverted) parameterString = parameterString.Substring(1);

            bool isEqual = string.Equals(valueString, parameterString, StringComparison.OrdinalIgnoreCase);
            bool result = isInverted ? !isEqual : isEqual;

            if (targetType == typeof(Visibility))
            {
                return result ? Visibility.Visible : Visibility.Collapsed;
            }

            return result;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isChecked && isChecked && parameter != null)
            {
                // Note: This returns the parameter directly. If it's used for an enum property, 
                // WPF will handle the string-to-enum conversion if needed.
                string parameterString = parameter.ToString();
                if (parameterString.StartsWith("!")) parameterString = parameterString.Substring(1);
                return parameterString;
            }
            return Binding.DoNothing;
        }
    }
}
