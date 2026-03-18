using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
                                string primaryTex = texFiles.FirstOrDefault(f => f.Contains("_tx_cm") || f.Contains("_base"));
                                if (primaryTex == null) primaryTex = texFiles[0];

                                using (Stream stream = File.OpenRead(primaryTex))
                                {
                                    BitmapSource bitmap = TextureUtils.LoadTexture(stream, ".tex");
                                    if (bitmap != null)
                                    {
                                        skinModel.PreviewImage = bitmap;
                                        skinModel.SwatchColor = ExtractDominantColor(bitmap);
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
                    _logService.LogError(ex, "Error scanning for chromas");
                }

                return skins;
            });
        }

        /// <summary>
        /// Simple algorithm to extract the dominant color of a bitmap for the UI Swatch.
        /// </summary>
        private Color ExtractDominantColor(BitmapSource bitmap)
        {
            try
            {
                // We take a sample of pixels from the center of the texture
                int width = bitmap.PixelWidth;
                int height = bitmap.PixelHeight;
                int stride = (width * bitmap.Format.BitsPerPixel + 7) / 8;
                byte[] pixels = new byte[stride * height];
                bitmap.CopyPixels(pixels, stride, 0);

                long r = 0, g = 0, b = 0;
                int samples = 0;

                // Sample every 32 pixels for performance
                for (int i = 0; i < pixels.Length; i += 32 * (bitmap.Format.BitsPerPixel / 8))
                {
                    if (i + 2 < pixels.Length)
                    {
                        b += pixels[i];
                        g += pixels[i + 1];
                        r += pixels[i + 2];
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
            catch { }
            return null;
        }
    }
}
