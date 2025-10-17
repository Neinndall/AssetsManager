using LeagueToolkit.Core.Renderer;
using LeagueToolkit.Toolkit;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AssetsManager.Utils
{
    public static class TextureUtils
    {
        public static BitmapSource LoadTexture(Stream textureStream, string extension)
        {
            try
            {
                if (textureStream == null) { return null; }

                if (extension.Equals(".tex", StringComparison.OrdinalIgnoreCase) || extension.Equals(".dds", StringComparison.OrdinalIgnoreCase))
                {
                    Texture tex = Texture.Load(textureStream);
                    if (tex.Mips.Length > 0)
                    {
                        Image<Rgba32> imageSharp = tex.Mips[0].ToImage();

                        var pixelBuffer = new byte[imageSharp.Width * imageSharp.Height * 4];
                        imageSharp.CopyPixelDataTo(pixelBuffer);

                        for (int i = 0; i < pixelBuffer.Length; i += 4)
                        {
                            var r = pixelBuffer[i];
                            var b = pixelBuffer[i + 2];
                            pixelBuffer[i] = b;
                            pixelBuffer[i + 2] = r;
                        }

                        int stride = imageSharp.Width * 4;
                        var bitmapSource = BitmapSource.Create(imageSharp.Width, imageSharp.Height, 96, 96, PixelFormats.Bgra32, null, pixelBuffer, stride);
                        bitmapSource.Freeze();

                        return bitmapSource;
                    }
                    return null;
                }
                else
                {
                    BitmapImage bitmapImage = new BitmapImage();
                    bitmapImage.BeginInit();
                    bitmapImage.StreamSource = textureStream;
                    bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                    bitmapImage.EndInit();
                    bitmapImage.Freeze();
                    return bitmapImage;
                }
            }
            catch (Exception)
            {
                // Logged by the calling service
                return null;
            }
        }
    }
}
