using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
        private static readonly Regex NormalizeNameRegex = new Regex(@"(skin|_)(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static string NormalizeName(string name)
        {
            return NormalizeNameRegex.Replace(name, "");
        }

        private static readonly HashSet<string> GenericMaterialKeywords =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "body", "face", "head", "hair", "mask", "eyes", "leg" };

        private static readonly char[] SeparatorChars = { '_', '-', ' ' };

        private static List<string> GetKeywords(string name)
        {
            string normalizedName = NormalizeName(name);
            var parts = normalizedName.Split(SeparatorChars, StringSplitOptions.RemoveEmptyEntries);
            var keywords = new List<string>();

            foreach (var part in parts)
            {
                // PascalCase splitting manually (no regex)
                int lastStart = 0;
                for (int i = 1; i < part.Length; i++)
                {
                    if (char.IsUpper(part[i]))
                    {
                        AddKeyword(keywords, part.Substring(lastStart, i - lastStart));
                        lastStart = i;
                    }
                }
                if (part.Length > lastStart)
                {
                    AddKeyword(keywords, part.Substring(lastStart));
                }
            }

            return keywords;
        }

        private static void AddKeyword(List<string> list, string word)
        {
            if (string.IsNullOrEmpty(word)) return;
            if (word.Equals("mat", StringComparison.OrdinalIgnoreCase) ||
                word.Equals("tx", StringComparison.OrdinalIgnoreCase) ||
                word.Equals("cm", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            list.Add(word.ToLowerInvariant());
        }

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
            string padded = "_" + key.Replace('-', '_').Replace(' ', '_') + "_";

            if (padded.Contains("_normal_") ||
                padded.Contains("_norm_") ||
                padded.Contains("_n_") ||
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
                padded.Contains("_em_") ||
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

            logService?.LogDebug($"Finding texture for material: '{materialName}'");

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
                string mainTextureCandidate = $"{skinName}_tx_cm";
                string genericMatch = textureKeys.FirstOrDefault(key => key.Equals(mainTextureCandidate, StringComparison.OrdinalIgnoreCase));
                if (genericMatch != null)
                {
                    logService?.LogDebug($"Found main texture '{genericMatch}' for generic material '{materialName}'.");
                    return genericMatch;
                }
            }

            logService?.LogDebug("No exact or generic match found. Trying keyword-based scoring with PascalCase splitting...");

            var materialKeywords = GetKeywords(materialName);
            string bestScoringMatch = null;
            int bestScore = -1; // Initialize with -1 to ensure any valid score is higher

            foreach (string key in textureKeys)
            {
                var textureKeywords = GetKeywords(key);
                string lowerKey = key.ToLowerInvariant();
                int currentScore = 0;

                // Score for exact keyword matches or partial matches
                foreach (string matKeyword in materialKeywords)
                {
                    if (textureKeywords.Contains(matKeyword))
                    {
                        currentScore += 2; // Exact keyword match
                    }
                    else if (textureKeywords.Any(texKeyword => texKeyword.Contains(matKeyword) || matKeyword.Contains(texKeyword)))
                    {
                        currentScore += 1; // Partial keyword match
                    }
                }

                // Score for containing the full material name (or parts of it)
                if (lowerKey.Contains(lowerMaterialName))
                {
                    currentScore += 3; // Strong match if texture key contains material name
                }
                else if (materialKeywords.Any(mk => lowerKey.Contains(mk)))
                {
                    currentScore += 1; // Match if texture key contains any material keyword
                }

                // Score for _tx_cm suffix (often indicates a main texture)
                if (lowerKey.Contains("_tx_cm"))
                {
                    currentScore += 1;
                }

                if (currentScore > bestScore)
                {
                    bestScore = currentScore;
                    bestScoringMatch = key;
                }
                else if (currentScore == bestScore)
                {
                    // Tie-breaking:
                    // 1. Prefer textures that contain "_tx_cm" if scores are equal
                    bool bestIsTxCm = bestScoringMatch?.Contains("_tx_cm", StringComparison.OrdinalIgnoreCase) ?? false;
                    bool currentIsTxCm = lowerKey.Contains("_tx_cm");

                    if (currentIsTxCm && !bestIsTxCm)
                    {
                        bestScoringMatch = key;
                    }
                    else if (!currentIsTxCm && bestIsTxCm)
                    {
                        // Keep bestScoringMatch
                    }
                    // 2. If still a tie, prefer the one that is a better substring match (longer common substring)
                    else if (bestScoringMatch == null || key.Length < bestScoringMatch.Length) // Prefer shorter name
                    {
                        bestScoringMatch = key;
                    }
                }
            }

            if (bestScoringMatch != null && bestScore > 0)
            {
                logService?.LogDebug($"Found texture '{bestScoringMatch}' with score {bestScore} via keyword matching.");
                return bestScoringMatch;
            }

            string propTexture = textureKeys.FirstOrDefault(key => key.Contains("_prop_tx_cm", StringComparison.OrdinalIgnoreCase));
            if (propTexture != null)
            {
                logService?.LogDebug($"Keyword matching failed. Falling back to generic prop texture '{propTexture}' for material '{materialName}'.");
                return propTexture;
            }

            logService?.LogDebug($"No texture found. Falling back to default: '{defaultTextureKey}'");
            return defaultTextureKey;
        }

        public static void UpdateMaterial(ModelPart modelPart)
        {
            if (modelPart.Geometry != null &&
                !string.IsNullOrEmpty(modelPart.SelectedTextureName) &&
                modelPart.AllTextures.TryGetValue(modelPart.SelectedTextureName, out BitmapSource texture))
            {
                var materialGroup = new MaterialGroup();
                var imageBrush = CreateViewerTextureBrush(texture);
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

        public static BitmapSource LoadTexture(byte[] data, string extension, int? maxWidth = null, int? maxHeight = null, bool forceOpaque = false)
        {
            if (data == null || data.Length == 0) return null;
            using (var ms = new MemoryStream(data))
            {
                return LoadTexture(ms, extension, maxWidth, maxHeight, forceOpaque);
            }
        }

        public static BitmapSource LoadViewerTexture(Stream textureStream, string extension, int? maxWidth = null, int? maxHeight = null)
        {
            return LoadTexture(textureStream, extension, maxWidth, maxHeight, forceOpaque: true);
        }

        public static BitmapSource LoadTexture(Stream textureStream, string extension, int? maxWidth = null, int? maxHeight = null, bool forceOpaque = false)
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
                            return ConvertToBgra32BitmapSource(imageSharp, maxWidth, maxHeight, forceOpaque);
                        }
                    }
                    return null;
                }
                else if (extension.Equals(".tga", StringComparison.OrdinalIgnoreCase))
                {
                    using (Image<Rgba32> imageSharp = Image.Load<Rgba32>(textureStream))
                    {
                        return ConvertToBgra32BitmapSource(imageSharp, maxWidth, maxHeight, forceOpaque);
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

        private static BitmapSource ConvertToBgra32BitmapSource(Image<Rgba32> imageSharp, int? maxWidth, int? maxHeight, bool forceOpaque)
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
                if (forceOpaque)
                {
                    for (int i = 3; i < bufferSize; i += 4)
                    {
                        pixelBuffer[i] = 255;
                    }
                }

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
            return LoadTexture(textureStream, extension, null, null, false);
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
    }
}
