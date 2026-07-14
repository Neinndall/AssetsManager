using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace AssetsManager.Views.Converters
{
    public class BoolToBrushConverter : IValueConverter
    {
        public Brush TrueBrush { get; set; }
        public Brush FalseBrush { get; set; }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // 1. Case: Direct Brush (apply opacity from parameter)
            if (value is SolidColorBrush brush)
            {
                if (parameter != null && double.TryParse(parameter.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double opacity))
                {
                    var newBrush = brush.Clone();
                    newBrush.Opacity = opacity;
                    return newBrush;
                }
                return brush;
            }

            // 2. Case: Boolean (standard behavior)
            return (value is bool boolValue && boolValue) ? TrueBrush : FalseBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
