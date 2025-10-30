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
using AssetsManager.Views.Models;

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

            Func<string, List<string>> getKeywords = (name) => {
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
            int bestScore = 0;

            foreach (string key in availableTextureKeys)
            {
                var textureKeywords = getKeywords(key);
                int currentScore = materialKeywords.Intersect(textureKeywords).Count();

                if (currentScore > bestScore)
                {
                    bestScore = currentScore;
                    bestScoringMatch = key;
                }
                else if (currentScore > 0 && currentScore == bestScore)
                {
                    bool bestIsTxCm = bestScoringMatch?.ToLower().Contains("_tx_cm") ?? false;
                    bool currentIsTxCm = key.ToLower().Contains("_tx_cm");

                    if (currentIsTxCm && !bestIsTxCm)
                    {
                        bestScoringMatch = key;
                    }
                    else if (!currentIsTxCm && bestIsTxCm)
                    {
                    }
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
