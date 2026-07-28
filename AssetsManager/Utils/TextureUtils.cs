using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using LeagueToolkit.Core.Renderer;
using LeagueToolkit.Toolkit;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using AssetsManager.Services.Core;
using AssetsManager.Views.Models.Viewer;
using AssetsManager.Views.Models.Settings;

namespace AssetsManager.Utils
{
    public static class TextureUtils
    {
        public static IReadOnlyList<string> GetColorTextureCandidates(IEnumerable<string> textureKeys)
        {
            var keys = textureKeys?.ToList() ?? new List<string>();
            return keys
                .Where(IsColorTextureCandidate)
                .OrderByDescending(key => key.Contains("_tx_cm", StringComparison.OrdinalIgnoreCase))
                .ThenBy(key => key.Length)
                .ToList();
        }

        private static bool IsColorTextureCandidate(string textureKey)
        {
            if (string.IsNullOrWhiteSpace(textureKey)) return false;

            string key = textureKey.ToLowerInvariant();
            string padded = "_" + key.Replace('-', '_').Replace(' ', '_').Replace('.', '_') + "_";

            if (padded.Contains("_normal_") ||
                padded.Contains("_norm_") ||
                padded.Contains("_mask_") ||
                padded.Contains("_masks_") ||
                padded.Contains("_spec_") ||
                padded.Contains("_specular_") ||
                padded.Contains("_rough_") ||
                padded.Contains("_roughness_") ||
                padded.Contains("_metal_") ||
                padded.Contains("_metallic_") ||
                padded.Contains("_orm_") ||
                padded.Contains("_ao_") ||
                padded.Contains("_emissive_") ||
                padded.Contains("_glow_"))
            {
                return false;
            }

            return padded.Contains("_tx_cm_") ||
                   padded.Contains("_cm_") ||
                   padded.Contains("_diffuse_") ||
                   padded.Contains("_color_") ||
                   padded.Contains("_albedo_") ||
                   padded.Contains("_basecolor_") ||
                   padded.Contains("_base_color_");
        }

        public static BitmapSource ResolveTexture(Dictionary<string, BitmapSource> allTextures, string selectedTextureName)
        {
            if (allTextures == null || string.IsNullOrEmpty(selectedTextureName))
                return null;

            allTextures.TryGetValue(selectedTextureName, out BitmapSource texture);
            return texture;
        }

        public static void UpdateMaterial(ModelPart modelPart)
        {
            if (modelPart.Geometry == null || string.IsNullOrEmpty(modelPart.SelectedTextureName))
                return;

            BitmapSource texture = ResolveTexture(modelPart.AllTextures, modelPart.SelectedTextureName);

            if (texture != null)
            {
                var materialGroup = new MaterialGroup();
                var imageBrush = CreateViewerTextureBrush(texture, modelPart.IsTextureTiled);
                materialGroup.Children.Add(new DiffuseMaterial(imageBrush));

                modelPart.Geometry.Material = materialGroup;
                modelPart.Geometry.BackMaterial = modelPart.IsDoubleSided ? materialGroup : null;
            }
        }

        internal static ImageBrush CreateViewerTextureBrush(BitmapSource texture, bool isTiled)
        {
            var imageBrush = new ImageBrush(texture)
            {
                Viewport = new System.Windows.Rect(0, 0, 1, 1),
                ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
                Viewbox = new System.Windows.Rect(0, 0, 1, 1),
                ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
                TileMode = isTiled ? TileMode.Tile : TileMode.None,
                Stretch = Stretch.Fill
            };

            RenderOptions.SetBitmapScalingMode(imageBrush, BitmapScalingMode.HighQuality);
            RenderOptions.SetCachingHint(imageBrush, CachingHint.Cache);
            RenderOptions.SetEdgeMode(imageBrush, EdgeMode.Unspecified);

            if (imageBrush.CanFreeze) imageBrush.Freeze();
            return imageBrush;
        }


        public static BitmapSource LoadTexture(byte[] data, string extension, int? maxWidth = null, int? maxHeight = null)
        {
            if (data == null || data.Length == 0) return null;
            using (var ms = new MemoryStream(data))
            {
                return LoadTexture(ms, extension, maxWidth, maxHeight);
            }
        }

        public static BitmapSource LoadViewerTexture(Stream textureStream, string extension, int? maxWidth = null, int? maxHeight = null)
        {
            return LoadTextureCore(textureStream, extension, maxWidth, maxHeight, null, null);
        }

        public static BitmapSource LoadViewerTexture(Stream textureStream, string extension, LogService logService, string source)
        {
            return LoadTextureCore(textureStream, extension, null, null, logService, source);
        }

        public static BitmapSource LoadTexture(Stream textureStream, string extension, int? maxWidth = null, int? maxHeight = null)
        {
            return LoadTextureCore(textureStream, extension, maxWidth, maxHeight, null, null);
        }

        private static BitmapSource LoadTextureCore(
            Stream textureStream,
            string extension,
            int? maxWidth,
            int? maxHeight,
            LogService logService,
            string source)
        {
            try
            {
                if (textureStream == null) { return null; }

                if (extension.Equals(".tex", StringComparison.OrdinalIgnoreCase) || extension.Equals(".dds", StringComparison.OrdinalIgnoreCase))
                {
                    Texture tex = Texture.Load(textureStream);
                    if (tex.Mips.Length > 0)
                    {
                        using (Image<Rgba32> imageSharp = tex.Mips[0].ToImage())
                        {
                            return ConvertToBgra32BitmapSource(imageSharp, maxWidth, maxHeight);
                        }
                    }
                    return null;
                }
                else if (extension.Equals(".tga", StringComparison.OrdinalIgnoreCase))
                {
                    using (Image<Rgba32> imageSharp = Image.Load<Rgba32>(textureStream))
                    {
                        return ConvertToBgra32BitmapSource(imageSharp, maxWidth, maxHeight);
                    }
                }
                else
                {
                    BitmapImage bitmapImage = new BitmapImage();
                    bitmapImage.BeginInit();
                    bitmapImage.StreamSource = textureStream;
                    bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                    if (maxWidth.HasValue)
                    {
                        bitmapImage.DecodePixelWidth = maxWidth.Value;
                    }
                    if (maxHeight.HasValue)
                    {
                        bitmapImage.DecodePixelHeight = maxHeight.Value;
                    }
                    bitmapImage.EndInit();
                    bitmapImage.Freeze();
                    return bitmapImage;
                }
            }
            catch (Exception ex)
            {
                logService?.LogError(ex, $"Failed to decode viewer texture: {source ?? extension ?? "unknown source"}");
                return null;
            }
        }

        private static BitmapSource ConvertToBgra32BitmapSource(Image<Rgba32> imageSharp, int? maxWidth, int? maxHeight)
        {
            if ((maxWidth.HasValue && imageSharp.Width > maxWidth.Value) ||
                (maxHeight.HasValue && imageSharp.Height > maxHeight.Value))
            {
                int resizeWidth = maxWidth ?? imageSharp.Width;
                int resizeHeight = maxHeight ?? imageSharp.Height;
                imageSharp.Mutate(x => x.Resize(new ResizeOptions
                {
                    Size = new Size(resizeWidth, resizeHeight),
                    Mode = ResizeMode.Max
                }));
            }

            int bufferSize = checked(imageSharp.Width * imageSharp.Height * 4);
            byte[] pixelBuffer = new byte[bufferSize];
            imageSharp.CopyPixelDataTo(pixelBuffer);

            for (int i = 0; i < pixelBuffer.Length; i += 4)
            {
                (pixelBuffer[i], pixelBuffer[i + 2]) = (pixelBuffer[i + 2], pixelBuffer[i]);
            }

            int stride = imageSharp.Width * 4;
            var bitmapSource = BitmapSource.Create(
                imageSharp.Width,
                imageSharp.Height,
                96, 96,
                PixelFormats.Bgra32,
                null,
                pixelBuffer,
                stride);

            bitmapSource.Freeze();
            return bitmapSource;
        }

        public static BitmapSource LoadTexture(Stream textureStream, string extension)
        {
            return LoadTexture(textureStream, extension, null, null);
        }

        public static BitmapSource LoadTextureFromFile(string filePath, int? maxWidth = null, int? maxHeight = null)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return null;

            using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            string extension = Path.GetExtension(filePath);
            if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".webp", StringComparison.OrdinalIgnoreCase))
            {
                using Image<Rgba32> image = Image.Load<Rgba32>(fileStream);
                return ConvertToBgra32BitmapSource(image, maxWidth, maxHeight);
            }

            return LoadTexture(fileStream, extension, maxWidth, maxHeight);
        }

        public static async Task SaveBitmapSourceAsImageAsync(
            BitmapSource bitmapSource,
            string originalFileName,
            string destinationPath,
            ImageExportFormat format,
            Action<string> onFileSavedCallback,
            CancellationToken cancellationToken = default)
        {
            string extension = format == ImageExportFormat.Jpeg ? ".jpg" : ".png";
            string fileName = Path.ChangeExtension(originalFileName, extension);
            string filePath = PathUtils.GetUniqueFilePath(destinationPath, fileName);
            await ImageExportUtils.SaveBitmapAsImageAsync(bitmapSource, filePath, format, cancellationToken);
            onFileSavedCallback?.Invoke(filePath);
        }

    }
}
