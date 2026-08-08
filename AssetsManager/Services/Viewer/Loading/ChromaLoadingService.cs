using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Loading
{
    public class ChromaLoadingService
    {
        private readonly LogService _logService;

        public ChromaLoadingService(LogService logService)
        {
            _logService = logService;
        }

        public async Task<List<ChromaFamilyModel>> LoadFamiliesAsync(string rootPath)
        {
            return await Task.Run(() =>
            {
                var families = new List<ChromaFamilyModel>();
                try
                {
                    if (!Directory.Exists(rootPath))
                        return families;

                    string[] directories = Directory.GetDirectories(rootPath)
                        .OrderBy(GetSkinOrder)
                        .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    ChromaFamilyModel currentFamily = null;
                    bool currentFamilyAdded = false;

                    foreach (string directory in directories)
                    {
                        string skinName = Path.GetFileName(directory);
                        string[] textureFiles = Directory.GetFiles(directory, "*.tex")
                            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                            .ToArray();
                        string modelPath = Directory.GetFiles(directory, "*.skn").FirstOrDefault();

                        if (modelPath != null)
                        {
                            PreviewData preview = LoadPreview(textureFiles, skinName);
                            currentFamily = new ChromaFamilyModel
                            {
                                Name = skinName.ToUpperInvariant(),
                                ModelName = Path.GetFileNameWithoutExtension(modelPath),
                                ModelPath = modelPath,
                                PreviewImage = preview.Image,
                                SwatchColor = preview.Color
                            };
                            currentFamilyAdded = false;
                            continue;
                        }

                        if (currentFamily == null || textureFiles.Length == 0)
                            continue;

                        PreviewData chromaPreview = LoadPreview(textureFiles, skinName);
                        currentFamily.Chromas.Add(new ChromaSkinModel
                        {
                            Name = skinName.ToUpperInvariant(),
                            TexturePath = directory,
                            ModelPath = currentFamily.ModelPath,
                            PreviewImage = chromaPreview.Image,
                            SwatchColor = chromaPreview.Color,
                            PreviewTextureName = chromaPreview.TextureName
                        });
                        if (!currentFamilyAdded)
                        {
                            families.Add(currentFamily);
                            currentFamilyAdded = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logService.LogError(ex, $"Error scanning for chromas in path: {rootPath}");
                }

                return families;
            });
        }

        private PreviewData LoadPreview(IReadOnlyList<string> textureFiles, string skinName)
        {
            if (textureFiles.Count == 0)
                return default;

            string primaryTexture = textureFiles
                .OrderByDescending(path => RankPreviewTexture(path, skinName))
                .ThenBy(path => Path.GetFileName(path).Length)
                .First();
            try
            {
                using FileStream stream = new(
                    primaryTexture,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);
                BitmapSource bitmap = TextureUtils.LoadTexture(stream, ".tex", 256, 256);
                if (bitmap == null)
                    return new PreviewData(
                        null,
                        Colors.Transparent,
                        Path.GetFileNameWithoutExtension(primaryTexture));

                bitmap.Freeze();
                return new PreviewData(
                    bitmap,
                    ExtractDominantColor(bitmap),
                    Path.GetFileNameWithoutExtension(primaryTexture));
            }
            catch (Exception ex)
            {
                _logService.LogWarning(
                    $"Could not extract preview for chroma {skinName}: {ex.Message}");
                return new PreviewData(
                    null,
                    Colors.Transparent,
                    Path.GetFileNameWithoutExtension(primaryTexture));
            }
        }

        private static int RankPreviewTexture(string path, string skinName)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            int rank = name.Contains("_tx_cm", StringComparison.OrdinalIgnoreCase) ? 100 : 0;
            if (name.Contains(skinName, StringComparison.OrdinalIgnoreCase)) rank += 20;
            if (name.Contains("_main_tx", StringComparison.OrdinalIgnoreCase)) rank += 50;
            if (name.EndsWith("_tx_cm", StringComparison.OrdinalIgnoreCase)) rank += 25;
            if (name.Contains("loadscreen", StringComparison.OrdinalIgnoreCase)) rank -= 200;
            if (name.Contains("_ult_", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("_mask_", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("_recall_", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("_voidling", StringComparison.OrdinalIgnoreCase))
            {
                rank -= 40;
            }
            return rank;
        }

        private static Color ExtractDominantColor(BitmapSource bitmap)
        {
            try
            {
                const int sampleSize = 64;
                int startX = Math.Max(0, (bitmap.PixelWidth - sampleSize) / 2);
                int startY = Math.Max(0, (bitmap.PixelHeight - sampleSize) / 2);
                int width = Math.Min(sampleSize, bitmap.PixelWidth);
                int height = Math.Min(sampleSize, bitmap.PixelHeight);
                var sourceRect = new Int32Rect(startX, startY, width, height);
                int stride = (width * bitmap.Format.BitsPerPixel + 7) / 8;
                int bufferSize = stride * height;
                byte[] samplePixels = ArrayPool<byte>.Shared.Rent(bufferSize);

                try
                {
                    bitmap.CopyPixels(sourceRect, samplePixels, stride, 0);
                    long red = 0;
                    long green = 0;
                    long blue = 0;
                    int samples = 0;
                    int bytesPerPixel = bitmap.Format.BitsPerPixel / 8;

                    for (int index = 0; index < bufferSize; index += bytesPerPixel)
                    {
                        if (index + 2 >= bufferSize)
                            break;
                        blue += samplePixels[index];
                        green += samplePixels[index + 1];
                        red += samplePixels[index + 2];
                        samples++;
                    }

                    return samples == 0
                        ? Colors.Gray
                        : Color.FromRgb(
                            (byte)(red / samples),
                            (byte)(green / samples),
                            (byte)(blue / samples));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(samplePixels);
                }
            }
            catch
            {
                return Colors.Gray;
            }
        }

        private static int GetSkinOrder(string directoryPath)
        {
            string name = Path.GetFileName(directoryPath);
            if (name.Equals("base", StringComparison.OrdinalIgnoreCase))
                return 0;

            Match match = Regex.Match(name, @"^skin0*(\d+)$", RegexOptions.IgnoreCase);
            return match.Success && int.TryParse(match.Groups[1].Value, out int skinId)
                ? skinId + 1
                : int.MaxValue;
        }

        private readonly record struct PreviewData(
            BitmapSource Image,
            Color Color,
            string TextureName);
    }
}
