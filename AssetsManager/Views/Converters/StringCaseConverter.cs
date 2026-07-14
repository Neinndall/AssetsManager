using System;
using System.Globalization;
using System.Windows.Data;

namespace AssetsManager.Views.Converters
{
    public class StringCaseConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null) return null;
            string str = value.ToString();
            string param = parameter as string;

            if (param != null && param.Equals("lower", StringComparison.OrdinalIgnoreCase))
            {
                return str.ToLower();
            }

            return str.ToUpper();
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
