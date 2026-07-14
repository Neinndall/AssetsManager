using System;
using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AssetsManager.Views.Converters
{
    public class CountToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            int count = 0;

            if (value is int intValue)
            {
                count = intValue;
            }
            else if (value is IEnumerable collection)
            {
                // Efficient way to get count if possible
                if (collection is ICollection col)
                {
                    count = col.Count;
                }
                else
                {
                    // Fallback for other enumerables
                    foreach (var _ in collection) count++;
                }
            }

            bool invert = parameter is string str && str.Equals("Invert", StringComparison.OrdinalIgnoreCase);
            
            if (invert)
            {
                return count <= 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            return count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
