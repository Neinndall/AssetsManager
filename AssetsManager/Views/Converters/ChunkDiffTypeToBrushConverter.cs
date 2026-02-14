using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using AssetsManager.Views.Models.Wad;

namespace AssetsManager.Views.Converters
{
    public class ChunkDiffTypeToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is ChunkDiffType type)
            {
                switch (type)
                {
                    case ChunkDiffType.New: return Brushes.Green;
                    case ChunkDiffType.Removed: return Brushes.Red;
                    case ChunkDiffType.Modified: return Brushes.DodgerBlue;
                    case ChunkDiffType.Renamed: return Brushes.Orange;
                    case ChunkDiffType.Dependency: return Brushes.Teal;
                    default: return Brushes.White;
                }
            }
            return Brushes.White;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
