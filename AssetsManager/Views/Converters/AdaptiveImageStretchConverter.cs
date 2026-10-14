using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;

namespace AssetsManager.Views.Converters
{
    /// <summary>
    /// Evaluates image asset paths, formats, or parameters to determine the optimal WPF Stretch mode
    /// (e.g. Uniform for cutout/transparent assets like PNG/ICO/SVG, UniformToFill for full photographic JPG/JPEG illustrations).
    /// </summary>
    public class AdaptiveImageStretchConverter : IValueConverter
    {
        public Stretch DefaultNonCutoutStretch { get; set; } = Stretch.UniformToFill;
        public Stretch DefaultCutoutStretch { get; set; } = Stretch.Uniform;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string path && !string.IsNullOrWhiteSpace(path))
            {
                string extension = Path.GetExtension(path);
                
                // Cutout / transparent image formats (chroma renders, icons, emotes, badges, vectors)
                if (string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".ico", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".svg", StringComparison.OrdinalIgnoreCase))
                {
                    return DefaultCutoutStretch;
                }
            }

            // Full-frame background illustrations (skin splash arts, banners, photographic wallpapers)
            return DefaultNonCutoutStretch;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
