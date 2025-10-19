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
using AssetsManager.Services.Core;
using AssetsManager.Views.Models;

namespace AssetsManager.Utils
{
    public static class TextureUtils
    {
        public static string FindBestTextureMatch(string materialName, string skinName, IEnumerable<string> availableTextureKeys, string defaultTextureKey, LogService logService)
        {
            logService.LogDebug($"Finding texture for material: '{materialName}'");

            string exactMatch = availableTextureKeys.FirstOrDefault(key => key.Equals(materialName, StringComparison.OrdinalIgnoreCase));
            if (exactMatch != null)
            {
                logService.LogDebug($"Found texture '{exactMatch}' via exact name match.");
                return exactMatch;
            }

            var genericMaterialNames = new List<string> { "body", "face", "head", "eyes", "leg" };
            if (genericMaterialNames.Contains(materialName.ToLower()))
            {
                string mainTextureCandidate = $"{skinName}_tx_cm";
                string genericMatch = availableTextureKeys.FirstOrDefault(key => key.Equals(mainTextureCandidate, StringComparison.OrdinalIgnoreCase));
                if (genericMatch != null)
                {
                    logService.LogDebug($"Found main texture '{genericMatch}' for generic material '{materialName}'.");
                    return genericMatch;
                }
            }

            string propTexture = availableTextureKeys.FirstOrDefault(key => key.Contains("_prop_tx_cm", StringComparison.OrdinalIgnoreCase));
            if (propTexture != null)
            {
                if (!genericMaterialNames.Contains(materialName.ToLower()))
                {
                    logService.LogDebug($"Material '{materialName}' is not a generic body part, assigning generic prop texture '{propTexture}'.");
                    return propTexture;
                }
            }

            logService.LogDebug("No exact or generic match found. Trying keyword-based scoring with PascalCase splitting...");
            var separatorChars = new[] { '_', '-', ' ' };
            var initialSplit = materialName.Split(separatorChars, StringSplitOptions.RemoveEmptyEntries);

            var materialKeywords = initialSplit
                .SelectMany(word => Regex.Split(word, @"(?<!^)(?=[A-Z])")) // Splits PascalCase
                .Where(k => !k.Equals("mat", StringComparison.OrdinalIgnoreCase))
                .Select(k => k.ToLower())
                .ToList();

            string bestScoringMatch = null;
            int bestScore = 0;

            foreach (string key in availableTextureKeys)
            {
                string lowerKey = key.ToLower();
                int currentScore = 0;

                foreach (string keyword in materialKeywords)
                {
                    if (string.IsNullOrWhiteSpace(keyword)) continue;
                    if (lowerKey.Contains(keyword))
                    {
                        currentScore++;
                    }
                }

                if (currentScore > bestScore)
                {
                    bestScore = currentScore;
                    bestScoringMatch = key;
                }
                else if (currentScore > 0 && currentScore == bestScore)
                {
                    bool bestIsTxCm = bestScoringMatch?.ToLower().Contains("_tx_cm") ?? false;
                    bool currentIsTxCm = lowerKey.Contains("_tx_cm");

                    if (currentIsTxCm && !bestIsTxCm)
                    {
                        bestScoringMatch = key;
                    }
                    else if (!currentIsTxCm && bestIsTxCm)
                    {
                    }
                    else if (bestScoringMatch == null || key.Length < bestScoringMatch.Length)
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
                        using (Image<Rgba32> imageSharp = tex.Mips[0].ToImage())
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
