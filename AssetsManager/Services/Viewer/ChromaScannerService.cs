using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer
{
    /// <summary>
    /// Service dedicated to scanning game directories for skins and chromas.
    /// Connects the filesystem structure with the ChromaSelectionModel.
    /// </summary>
    public class ChromaScannerService
    {
        private readonly LogService _logService;

        public ChromaScannerService(LogService logService)
        {
            _logService = logService;
        }

        /// <summary>
        /// Scans a directory for subdirectories containing textures (.tex) and links them to a model.
        /// Implements a sequential look-back strategy for chromas sharing a parent skin model.
        /// </summary>
        public async Task<List<ChromaSkinModel>> ScanSkinsAsync(string rootPath)
        {
            return await Task.Run(() =>
            {
                var skins = new List<ChromaSkinModel>();
                try
                {
                    if (!Directory.Exists(rootPath)) return skins;

                    // 1. Get all subdirectories and sort them (base, skin01, skin02...)
                    string[] subDirs = Directory.GetDirectories(rootPath)
                                                .OrderBy(d => d)
                                                .ToArray();

                    string lastFoundModelPath = null;

                    foreach (var dir in subDirs)
                    {
                        string dirName = Path.GetFileName(dir);
                        
                        // Look for .tex files in the directory
                        string[] texFiles = Directory.GetFiles(dir, "*.tex");
                        if (texFiles.Length == 0) continue;

                        // Check if THIS specific directory has a .skn model
                        string localSkn = Directory.GetFiles(dir, "*.skn").FirstOrDefault();
                        bool hasOwnModel = localSkn != null;
                        
                        if (hasOwnModel)
                        {
                            lastFoundModelPath = localSkn;
                        }

                        // 3. Only add to list if it's a CHROMA (inherits model, doesn't have its own)
                        if (!hasOwnModel)
                        {
                            var skinModel = new ChromaSkinModel
                            {
                                Name = dirName.ToUpper(),
                                TexturePath = dir,
                                ModelPath = lastFoundModelPath,
                                IsSelected = false
                            };

                            // Extract preview...
                            try
                            {
                                // Strategy: Find all valid candidates and pick the one that is most likely the main diffuse map.
                                // We look for textures containing both the chroma ID (dirName) and '_tx_cm'.
                                var candidates = texFiles.Where(f => f.Contains(dirName, StringComparison.OrdinalIgnoreCase) && 
                                                                    f.Contains("_tx_cm", StringComparison.OrdinalIgnoreCase))
                                                         .ToList();

                                string primaryTex = null;

                                if (candidates.Count > 0)
                                {
                                    // If we have multiple (like bigscreens, loadscreens, etc.), pick the SHORTEST filename.
                                    // The main texture is usually the cleanest one: mordekaiser_skin58_tx_cm...
                                    primaryTex = candidates.OrderBy(f => Path.GetFileName(f).Length).First();
                                }
                                else
                                {
                                    // Fallback to any '_tx_cm' or just the first file
                                    primaryTex = texFiles.FirstOrDefault(f => f.Contains("_tx_cm", StringComparison.OrdinalIgnoreCase)) 
                                                 ?? texFiles[0];
                                }

                                using (FileStream stream = new FileStream(primaryTex, FileMode.Open, FileAccess.Read, FileShare.Read))
                                {
                                    // Chroma previews are tiny gallery thumbnails, not full-size render assets.
                                    // Capping the texture to 256px reduces GPU/CPU memory by 10-50x per chroma
                                    // (typical 2K source → ~64x reduction in pixels) while preserving visual fidelity
                                    // at the small card size used by ChromaSelectionControl.
                                    BitmapSource bitmap = TextureUtils.LoadTexture(stream, ".tex", 256, 256);
                                    if (bitmap != null)
                                    {
                                        bitmap.Freeze();
                                        skinModel.PreviewImage = bitmap;
                                        skinModel.SwatchColor = ExtractDominantColor(bitmap);
                                        skinModel.PreviewTextureName = Path.GetFileNameWithoutExtension(primaryTex);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _logService.LogWarning($"Could not extract preview for chroma {dirName}: {ex.Message}");
                            }

                            skins.Add(skinModel);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logService.LogError(ex, $"Error scanning for chromas in path: {rootPath}");
                }

                return skins;
            });
        }

        /// <summary>
        /// Simple algorithm to extract the dominant color of a bitmap for the UI Swatch.
        /// Optimized to only sample a small region without copying the whole bitmap.
        /// </summary>
        private Color ExtractDominantColor(BitmapSource bitmap)
        {
            try
            {
                // Sampling a 64x64 area from the center is more than enough for a swatch
                int sampleSize = 64;
                int startX = Math.Max(0, (bitmap.PixelWidth - sampleSize) / 2);
                int startY = Math.Max(0, (bitmap.PixelHeight - sampleSize) / 2);
                int width = Math.Min(sampleSize, bitmap.PixelWidth);
                int height = Math.Min(sampleSize, bitmap.PixelHeight);

                Int32Rect sourceRect = new Int32Rect(startX, startY, width, height);
                int stride = (width * bitmap.Format.BitsPerPixel + 7) / 8;
                
                // Use a small buffer (stackalloc if possible, but 64x64*4 = 16KB is safe for heap too)
                byte[] samplePixels = new byte[stride * height];
                bitmap.CopyPixels(sourceRect, samplePixels, stride, 0);

                long r = 0, g = 0, b = 0;
                int samples = 0;
                int bytesPerPixel = bitmap.Format.BitsPerPixel / 8;

                for (int i = 0; i < samplePixels.Length; i += bytesPerPixel)
                {
                    if (i + 2 < samplePixels.Length)
                    {
                        // Assuming Bgra32 or Bgr32
                        b += samplePixels[i];
                        g += samplePixels[i + 1];
                        r += samplePixels[i + 2];
                        samples++;
                    }
                }

                if (samples == 0) return Colors.Gray;
                return Color.FromRgb((byte)(r / samples), (byte)(g / samples), (byte)(b / samples));
            }
            catch
            {
                return Colors.Gray;
            }
        }

        /// <summary>
        /// Attempts to find the primary .skn model associated with a skins folder.
        /// </summary>
        public string FindAssociatedModel(string skinsPath)
        {
            try
            {
                // Strategy 1: Look in the parent folder (character root)
                string parent = Path.GetDirectoryName(skinsPath);
                if (parent != null)
                {
                    string[] sknFiles = Directory.GetFiles(parent, "*.skn");
                    if (sknFiles.Length > 0) return sknFiles[0];
                }

                // Strategy 2: Look in skin0 (the base skin usually has the model)
                string skin0 = Path.Combine(skinsPath, "skin0");
                if (Directory.Exists(skin0))
                {
                    string[] sknFiles = Directory.GetFiles(skin0, "*.skn");
                    if (sknFiles.Length > 0) return sknFiles[0];
                }
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Could not find associated model for skins path: {skinsPath}");
            }
            return null;
        }
    }
}
