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
using AssetsManager.Views.Models.Models3D;

namespace AssetsManager.Utils
{
    public static class TextureUtils
    {
        private static string NormalizeName(string name)
        {
            return Regex.Replace(name, @"(skin|_)(\d+)", "", RegexOptions.IgnoreCase);
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

            var genericMaterialKeywords = new List<string> { "body", "face", "head", "hair", "mask", "eyes", "leg" };
            string lowerMaterialName = materialName.ToLower();
            if (genericMaterialKeywords.Any(keyword =>
                lowerMaterialName.Equals(keyword) ||
                lowerMaterialName.Equals($"{keyword}_mat")
            ))
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
            var separatorChars = new[] { '_', '-', ' ' };

            Func<string, List<string>> getKeywords = (name) =>
            {
                string normalizedName = NormalizeName(name);
                var initialSplit = normalizedName.Split(separatorChars, StringSplitOptions.RemoveEmptyEntries);
                return initialSplit
                    .SelectMany(word => Regex.Split(word, @"(?<!^)(?=[A-Z])")) // Splits PascalCase
                    .Where(k => !k.Equals("mat", StringComparison.OrdinalIgnoreCase) && !k.Equals("tx", StringComparison.OrdinalIgnoreCase) && !k.Equals("cm", StringComparison.OrdinalIgnoreCase))
                    .Select(k => k.ToLower())
                    .ToList();
            };

            var materialKeywords = getKeywords(materialName);
            string bestScoringMatch = null;
            int bestScore = -1; // Initialize with -1 to ensure any valid score is higher

            foreach (string key in availableTextureKeys)
            {
                var textureKeywords = getKeywords(key);
                string lowerKey = key.ToLower();
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
                    bool bestIsTxCm = bestScoringMatch?.ToLower().Contains("_tx_cm") ?? false;
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
                    // This is implicitly handled by the scoring, but if scores are equal, we need another tie-breaker.
                    // For now, let's keep the "prefer shorter name" as a last resort, but it's less likely to be hit.
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
                            if (maxWidth.HasValue && (imageSharp.Width > maxWidth.Value || imageSharp.Height > maxWidth.Value))
                            {
                                imageSharp.Mutate(x => x.Resize(new ResizeOptions
                                {
                                    Size = new Size(maxWidth.Value, maxWidth.Value),
                                    Mode = ResizeMode.Max
                                }));
                            }

                            using (Image<Bgra32> bgra32Image = imageSharp.CloneAs<Bgra32>())
                            {
                                var pixelBuffer = new byte[bgra32Image.Width * bgra32Image.Height * 4];
                                bgra32Image.CopyPixelDataTo(pixelBuffer);

                                int stride = bgra32Image.Width * 4;
                                var bitmapSource = BitmapSource.Create(bgra32Image.Width, bgra32Image.Height, 96, 96, PixelFormats.Bgra32, null, pixelBuffer, stride);
                                bitmapSource.Freeze();

                                return bitmapSource;
                            }
                        }
                    }
                    return null;
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

        public static BitmapSource LoadTexture(Stream textureStream, string extension)
        {
            return LoadTexture(textureStream, extension, null, null);
        }
    }
}
