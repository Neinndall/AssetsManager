using System;
using System.Globalization;
using System.Windows.Data;
using Material.Icons;

namespace AssetsManager.Views.Converters
{
    public class BooleanToMaterialIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue && parameter is string paramStr)
            {
                var icons = paramStr.Split(',');
                if (icons.Length == 2)
                {
                    if (Enum.TryParse(icons[0].Trim(), out MaterialIconKind trueIcon) &&
                        Enum.TryParse(icons[1].Trim(), out MaterialIconKind falseIcon))
                    {
                        return boolValue ? trueIcon : falseIcon;
                    }
                }
            }
            return MaterialIconKind.HelpCircleOutline; // Fallback
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
