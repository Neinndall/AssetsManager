using System;
using System.Buffers;
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

        private static readonly System.Collections.Generic.HashSet<string> GenericMaterialKeywords = 
            new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase) 
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

        public static string FindBestTextureMatch(string materialName, string skinName, IEnumerable<string> availableTextureKeys, string defaultTextureKey, LogService logService)
        {
            logService.LogDebug($"Finding texture for material: '{materialName}'");

            string exactMatch = availableTextureKeys.FirstOrDefault(key => key.Equals(materialName, StringComparison.OrdinalIgnoreCase));
            if (exactMatch != null)
            {
                logService.LogDebug($"Found texture '{exactMatch}' via exact name match.");
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
                string genericMatch = availableTextureKeys.FirstOrDefault(key => key.Equals(mainTextureCandidate, StringComparison.OrdinalIgnoreCase));
                if (genericMatch != null)
                {
                    logService.LogDebug($"Found main texture '{genericMatch}' for generic material '{materialName}'.");
                    return genericMatch;
                }
            }

            logService.LogDebug("No exact or generic match found. Trying keyword-based scoring with PascalCase splitting...");

            var materialKeywords = GetKeywords(materialName);
            string bestScoringMatch = null;
            int bestScore = -1; // Initialize with -1 to ensure any valid score is higher

            foreach (string key in availableTextureKeys)
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

            if (bestScoringMatch != null)
            {
                logService.LogDebug($"Found texture '{bestScoringMatch}' with score {bestScore} via keyword matching.");
                return bestScoringMatch;
            }

            string propTexture = availableTextureKeys.FirstOrDefault(key => key.Contains("_prop_tx_cm", StringComparison.OrdinalIgnoreCase));
            if (propTexture != null)
            {
                logService.LogDebug($"Keyword matching failed. Falling back to generic prop texture '{propTexture}' for material '{materialName}'.");
                return propTexture;
            }

            logService.LogDebug($"No texture found. Falling back to default: '{defaultTextureKey}'");
            return defaultTextureKey;
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

                // Mejora la calidad
                RenderOptions.SetBitmapScalingMode(imageBrush, BitmapScalingMode.HighQuality);
                RenderOptions.SetCachingHint(imageBrush, CachingHint.Cache);
                RenderOptions.SetEdgeMode(imageBrush, EdgeMode.Unspecified);

                materialGroup.Children.Add(new DiffuseMaterial(imageBrush));

                // Reduce el brillo especular (puede causar más aliasing visible)
                materialGroup.Children.Add(new SpecularMaterial(new SolidColorBrush(Colors.White), 8)); // Era 15, ahora 8

                // Emisivo suave
                materialGroup.Children.Add(new EmissiveMaterial(new SolidColorBrush(System.Windows.Media.Color.FromArgb(10, 255, 255, 255))));

                modelPart.Geometry.Material = materialGroup;
                modelPart.Geometry.BackMaterial = materialGroup;
            }
        }

        public static BitmapSource LoadTexture(byte[] data, string extension, int? maxWidth = null, int? maxHeight = null)
        {
            if (data == null || data.Length == 0) return null;
            using (var ms = new MemoryStream(data))
            {
                return LoadTexture(ms, extension, maxWidth, maxHeight);
            }
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
                            return ConvertToBgra32BitmapSource(imageSharp, maxWidth);
                        }
                    }
                    return null;
                }
                else if (extension.Equals(".tga", StringComparison.OrdinalIgnoreCase))
                {
                    using (Image<Rgba32> imageSharp = Image.Load<Rgba32>(textureStream))
                    {
                        return ConvertToBgra32BitmapSource(imageSharp, maxWidth);
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

        private static BitmapSource ConvertToBgra32BitmapSource(Image<Rgba32> imageSharp, int? maxWidth)
        {
            if (maxWidth.HasValue && (imageSharp.Width > maxWidth.Value || imageSharp.Height > maxWidth.Value))
            {
                imageSharp.Mutate(x => x.Resize(new ResizeOptions
                {
                    Size = new Size(maxWidth.Value, maxWidth.Value),
                    Mode = ResizeMode.Max
                }));
            }

            // OPTIMIZACIÓN: Usamos ArrayPool para evitar picos de RAM y GC.
            // Restauramos la conversión a Bgra32 para compatibilidad total con WPF (Colores correctos).
            using (Image<Bgra32> bgraImage = imageSharp.CloneAs<Bgra32>())
            {
                int bufferSize = bgraImage.Width * bgraImage.Height * 4;
                byte[] pixelBuffer = ArrayPool<byte>.Shared.Rent(bufferSize);
                
                try
                {
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
                finally
                {
                    ArrayPool<byte>.Shared.Return(pixelBuffer);
                }
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
    }
}
