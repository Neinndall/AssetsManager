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
using SixLabors.ImageSharp.Processing;
using AssetsManager.Services.Core;
using AssetsManager.Views.Models.Viewer;
using AssetsManager.Views.Models.Settings;

namespace AssetsManager.Utils
{
    public static class TextureUtils
    {
        private static readonly HashSet<string> GenericMaterialKeywords =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "body", "face", "head", "hair", "mask", "eyes", "leg" };

        public static IReadOnlyList<string> GetColorTextureCandidates(IEnumerable<string> textureKeys)
        {
            var keys = textureKeys?.ToList() ?? new List<string>();
            var colorKeys = keys
                .Where(IsColorTextureCandidate)
                .OrderByDescending(key => key.Contains("_tx_cm", StringComparison.OrdinalIgnoreCase))
                .ThenBy(key => key.Length)
                .ToList();

            return colorKeys.Count > 0 ? colorKeys : keys;
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

        public static string FindBestTextureMatch(string materialName, string skinName, IEnumerable<string> availableTextureKeys, string defaultTextureKey, LogService logService)
        {
            var textureKeys = availableTextureKeys?.ToList() ?? new List<string>();
            if (textureKeys.Count == 0)
            {
                logService?.LogDebug($"No textures available for material: '{materialName}'");
                return null;
            }

            string exactMatch = textureKeys.FirstOrDefault(key => key.Equals(materialName, StringComparison.OrdinalIgnoreCase));
            if (exactMatch != null)
            {
                logService?.LogDebug($"Found texture '{exactMatch}' via exact name match.");
                return exactMatch;
            }

            string lowerMaterialName = materialName.ToLowerInvariant();
            bool isGeneric = GenericMaterialKeywords.Contains(lowerMaterialName);
            if (!isGeneric && lowerMaterialName.EndsWith("_mat") && lowerMaterialName.Length > 4)
            {
                string baseWord = lowerMaterialName.Substring(0, lowerMaterialName.Length - 4);
                isGeneric = GenericMaterialKeywords.Contains(baseWord);
            }

            if (isGeneric)
            {
                string skinTxCm = $"{skinName}_tx_cm";
                string genericMatch = textureKeys
                    .FirstOrDefault(key => key.IndexOf(skinTxCm, StringComparison.OrdinalIgnoreCase) >= 0);
                if (genericMatch != null)
                {
                    logService?.LogDebug($"Found main texture '{genericMatch}' for generic material '{materialName}'.");
                    return genericMatch;
                }
            }

            string propTexture = textureKeys.FirstOrDefault(key => key.Contains("_prop_tx_cm", StringComparison.OrdinalIgnoreCase));
            if (propTexture != null)
            {
                logService?.LogDebug($"Falling back to generic prop texture '{propTexture}' for material '{materialName}'.");
                return propTexture;
            }

            logService?.LogDebug($"No specific match found. Falling back to default: '{defaultTextureKey}'");
            return defaultTextureKey;
        }

        public static void UpdateMaterial(ModelPart modelPart)
        {
            if (modelPart.Geometry == null || string.IsNullOrEmpty(modelPart.SelectedTextureName))
                return;

            if (!modelPart.AllTextures.TryGetValue(modelPart.SelectedTextureName, out BitmapSource texture))
            {
                string fullKey = modelPart.AllTextures.Keys
                    .FirstOrDefault(k => string.Equals(PathUtils.TruncateAtDot(k), modelPart.SelectedTextureName, StringComparison.OrdinalIgnoreCase));
                if (fullKey != null)
                    modelPart.AllTextures.TryGetValue(fullKey, out texture);
            }

            if (texture != null)
            {
                var materialGroup = new MaterialGroup();

                // Force Bgr32 format for opaque parts to prevent WPF from placing them in the transparent rendering pass
                BitmapSource textureToUse = texture;
                if (!modelPart.IsTransparent && texture.Format != PixelFormats.Bgr32)
                {
                    try
                    {
                        textureToUse = new FormatConvertedBitmap(texture, PixelFormats.Bgr32, null, 0);
                    }
                    catch
                    {
                        textureToUse = texture;
                    }
                }

                var imageBrush = CreateViewerTextureBrush(textureToUse);
                if (modelPart.IsTransparent)
                {
                    imageBrush.Opacity = 0.99; // Force WPF to place this in the transparent pass
                }
                materialGroup.Children.Add(new DiffuseMaterial(imageBrush));

                modelPart.Geometry.Material = materialGroup;
                modelPart.Geometry.BackMaterial = materialGroup;
            }
        }

        private static ImageBrush CreateViewerTextureBrush(BitmapSource texture)
        {
            var imageBrush = new ImageBrush(texture)
            {
                Viewport = new System.Windows.Rect(0, 0, 1, 1),
                ViewportUnits = BrushMappingMode.Absolute,
                TileMode = TileMode.Tile,
                Stretch = Stretch.Fill
            };

            RenderOptions.SetBitmapScalingMode(imageBrush, BitmapScalingMode.HighQuality);
            RenderOptions.SetCachingHint(imageBrush, CachingHint.Cache);
            RenderOptions.SetEdgeMode(imageBrush, EdgeMode.Unspecified);

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
            return LoadTexture(textureStream, extension, maxWidth, maxHeight);
        }

        public static BitmapSource LoadTexture(Stream textureStream, string extension, int? maxWidth = null, int? maxHeight = null)
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
            catch (Exception)
            {
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

            // Mantener el buffer estable evita que WPF renderice pixeles de un array reutilizado.
            using (Image<Bgra32> bgraImage = imageSharp.CloneAs<Bgra32>())
            {
                int bufferSize = bgraImage.Width * bgraImage.Height * 4;
                byte[] pixelBuffer = new byte[bufferSize];

                bgraImage.CopyPixelDataTo(pixelBuffer);

                int stride = bgraImage.Width * 4;

                var bitmapSource = BitmapSource.Create(
                    bgraImage.Width,
                    bgraImage.Height,
                    96, 96,
                    PixelFormats.Bgra32,
                    null,
                    pixelBuffer,
                    stride);

                bitmapSource.Freeze();
                return bitmapSource;
            }
        }

        public static BitmapSource LoadTexture(Stream textureStream, string extension)
        {
            return LoadTexture(textureStream, extension, null, null);
        }

        public static void SaveBitmapSourceAsImage(BitmapSource bitmapSource, string originalFileName, string destinationPath, ImageExportFormat format, Action<string> onFileSavedCallback)
        {
            BitmapEncoder encoder;
            string extension;

            switch (format)
            {
                case ImageExportFormat.Jpeg:
                    encoder = new JpegBitmapEncoder { QualityLevel = 90 };
                    extension = ".jpg";
                    break;
                case ImageExportFormat.Png:
                default:
                    encoder = new PngBitmapEncoder();
                    extension = ".png";
                    break;
            }

            encoder.Frames.Add(BitmapFrame.Create(bitmapSource));

            string fileName = Path.ChangeExtension(originalFileName, extension);
            string filePath = PathUtils.GetUniqueFilePath(destinationPath, fileName);

            using (var fileStream = new FileStream(filePath, FileMode.Create))
            {
                encoder.Save(fileStream);
            }
            onFileSavedCallback?.Invoke(filePath);
        }

        public static bool ExportTextureAsPngDirect(byte[] fileBytes, string originalFileName, string destinationPath, Action<string> onFileSavedCallback, LogService logService)
        {
            try
            {
                using (var memoryStream = new MemoryStream(fileBytes))
                {
                    var tex = Texture.Load(memoryStream);
                    if (tex.Mips.Length > 0)
                    {
                        var mip = tex.Mips[0];
                        string fileName = Path.ChangeExtension(originalFileName, ".png");
                        string filePath = PathUtils.GetUniqueFilePath(destinationPath, fileName);

                        int pixelCount = mip.Width * mip.Height;
                        uint[] pixels = System.Buffers.ArrayPool<uint>.Shared.Rent(pixelCount);
                        try
                        {
                            var flatSpan = pixels.AsSpan(0, pixelCount);
                            for (int y = 0; y < mip.Height; y++)
                            {
                                var rowDst = System.Runtime.InteropServices.MemoryMarshal.Cast<uint, Rgba32>(flatSpan.Slice(y * mip.Width, mip.Width));
                                mip.Span.GetRowSpan(y).CopyTo(rowDst);
                            }
                            TexSharp.TextureExporter.SaveToPng(flatSpan.ToArray(), mip.Width, mip.Height, filePath);
                        }
                        finally
                        {
                            System.Buffers.ArrayPool<uint>.Shared.Return(pixels);
                        }

                        onFileSavedCallback?.Invoke(filePath);
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                logService?.LogError(ex, $"Failed to save texture directly as PNG, falling back to legacy WPF exporter.");
            }
            return false;
        }
    }
}