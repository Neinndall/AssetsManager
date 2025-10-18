using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using LeagueToolkit.Core.Renderer;
using LeagueToolkit.Toolkit;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using AssetsManager.Views.Models;

namespace AssetsManager.Utils
{
    public static class TextureUtils
    {
        public static string FindBestTextureMatch(string materialName, IEnumerable<string> availableTextureKeys)
        {
            if (materialName == null) return availableTextureKeys.FirstOrDefault();

            return availableTextureKeys.FirstOrDefault(key => key.Equals(materialName, System.StringComparison.OrdinalIgnoreCase))
                ?? availableTextureKeys.FirstOrDefault(key => key.StartsWith(materialName, System.StringComparison.OrdinalIgnoreCase))
                ?? availableTextureKeys.FirstOrDefault();
        }

        public static void UpdateMaterial(ModelPart modelPart)
        {
            if (modelPart.Geometry != null &&
                !string.IsNullOrEmpty(modelPart.SelectedTextureName) &&
                modelPart.AllTextures.TryGetValue(modelPart.SelectedTextureName, out BitmapSource texture))
            {
                var materialGroup = new MaterialGroup();

                // Material difuso con la textura
                var imageBrush = new ImageBrush(texture)
                {
                    ViewportUnits = BrushMappingMode.Absolute,
                    TileMode = TileMode.Tile,
                    Stretch = Stretch.Fill
                };
                materialGroup.Children.Add(new DiffuseMaterial(imageBrush));

                // Componente especular para dar brillo/reflejo
                materialGroup.Children.Add(new SpecularMaterial(new SolidColorBrush(Colors.White), 15));

                // Componente emisivo suave para mejor visibilidad
                materialGroup.Children.Add(new EmissiveMaterial(new SolidColorBrush(System.Windows.Media.Color.FromArgb(10, 255, 255, 255))));

                modelPart.Geometry.Material = materialGroup;

                // IMPORTANTE: También aplicar al BackMaterial para ver ambas caras
                modelPart.Geometry.BackMaterial = materialGroup;
            }
        }

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
