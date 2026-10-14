using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AssetsManager.Services.Viewer.Rendering.Core
{
    /// <summary>Tightly packed BGRA8 pixels of a decoded bitmap, ready for a GL upload.</summary>
    internal static class GlBitmapPixels
    {
        internal static (int Width, int Height, byte[] Pixels) ToBgra32(BitmapSource source)
        {
            BitmapSource bitmap = source;
            if (bitmap.Format != PixelFormats.Bgra32)
            {
                var converted = new FormatConvertedBitmap();
                converted.BeginInit();
                converted.Source = bitmap;
                converted.DestinationFormat = PixelFormats.Bgra32;
                converted.EndInit();
                converted.Freeze();
                bitmap = converted;
            }

            int width = bitmap.PixelWidth;
            int height = bitmap.PixelHeight;
            int stride = checked(width * 4);
            byte[] pixels = new byte[checked(height * stride)];
            bitmap.CopyPixels(pixels, stride, 0);
            return (width, height, pixels);
        }
    }
}
