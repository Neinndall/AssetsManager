using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using Material3D = System.Windows.Media.Media3D.Material;

namespace AssetsManager.Views.Helpers
{
    public static class SceneElements
    {
        public const double GroundLevel = 1000;
        public const string GroundChunkVirtualPath = "assets/maps/kitpieces/srs/base/textures/ground_c3_midlanecaps_a.tex";
        private const double GroundLogoElevation = 2.0;
        public const int SceneTextureMaxSize = 2048;

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, BitmapSource> _textureCache = new();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, BitmapSource> _groundLogoTextureCache = new();
        private static readonly object GroundLock = new();
        private static BitmapSource _cachedGroundTexture;
        private static bool _groundLoaded;

        public static void ClearGroundCache()
        {
            lock (GroundLock)
            {
                _groundLoaded = false;
                _cachedGroundTexture = null;
            }
        }

        public static BitmapSource LoadStageGroundTexture(AppSettings settings, LogService logService)
        {
            lock (GroundLock)
            {
                if (_groundLoaded)
                    return _cachedGroundTexture;

                _groundLoaded = true;
                try
                {
                    _cachedGroundTexture = LoadGroundFromGame(settings, logService);
                    return _cachedGroundTexture;
                }
                catch (Exception ex)
                {
                    logService?.LogError(ex, "Failed to load stage ground texture from League installation.");
                    return null;
                }
            }
        }

        private static BitmapSource LoadGroundFromGame(AppSettings settings, LogService logService)
        {
            string gameRoot = GetPreferredGameRoot(settings);
            if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot))
            {
                logService?.LogDebug("No valid League of Legends installation directory configured for ground texture.");
                return null;
            }

            string mapWadPath = FindMap11Wad(gameRoot);
            if (string.IsNullOrWhiteSpace(mapWadPath) || !File.Exists(mapWadPath))
            {
                logService?.LogWarning($"Map11.wad.client was not found under '{gameRoot}'.");
                return null;
            }

            ulong chunkHash = LeagueToolkit.Hashing.XxHash64Ext.Hash(GroundChunkVirtualPath);
            using var wad = new LeagueToolkit.Core.Wad.WadFile(mapWadPath);
            if (!wad.Chunks.TryGetValue(chunkHash, out var chunk))
            {
                logService?.LogWarning($"Ground chunk {chunkHash:x16} ({GroundChunkVirtualPath}) not found in '{mapWadPath}'.");
                return null;
            }

            using var decompressed = wad.LoadChunkDecompressed(chunk);
            using var stream = new MemoryStream(decompressed.Span.ToArray(), writable: false);
            return TextureUtils.LoadTexture(stream, ".tex", SceneTextureMaxSize);
        }

        private static string GetPreferredGameRoot(AppSettings settings)
        {
            if (settings == null)
                return null;

            string preferred = settings.PreferredClient == AssetsManager.Views.Models.Settings.PreferredClient.PBE
                ? settings.LolPbeDirectory
                : settings.LolLiveDirectory;

            if (!string.IsNullOrWhiteSpace(preferred) && Directory.Exists(preferred))
                return preferred;

            string fallback = settings.PreferredClient == AssetsManager.Views.Models.Settings.PreferredClient.PBE
                ? settings.LolLiveDirectory
                : settings.LolPbeDirectory;

            if (!string.IsNullOrWhiteSpace(fallback) && Directory.Exists(fallback))
                return fallback;

            return null;
        }

        private static string FindMap11Wad(string gameRoot)
        {
            string candidate1 = Path.Combine(gameRoot, "Game", "DATA", "FINAL", "Maps", "Shipping", "Map11.wad.client");
            if (File.Exists(candidate1)) return candidate1;

            string candidate2 = Path.Combine(gameRoot, "DATA", "FINAL", "Maps", "Shipping", "Map11.wad.client");
            if (File.Exists(candidate2)) return candidate2;

            try
            {
                return Directory.EnumerateFiles(gameRoot, "Map11.wad.client", SearchOption.AllDirectories).FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        public static BitmapSource LoadSceneTexture(string path, LogService logService)
        {
            if (string.IsNullOrEmpty(path)) return null;

            return _textureCache.GetOrAdd(path, p =>
            {
                try
                {
                    if (File.Exists(p))
                    {
                        using (FileStream fileStream = new FileStream(p, FileMode.Open, FileAccess.Read))
                            return TextureUtils.LoadTexture(fileStream, Path.GetExtension(p), SceneTextureMaxSize);
                    }
                    else
                    {
                        using (Stream resourceStream = Application.GetResourceStream(new Uri(p)).Stream)
                            return TextureUtils.LoadTexture(resourceStream, Path.GetExtension(p), SceneTextureMaxSize);
                    }
                }
                catch (Exception ex)
                {
                    logService.LogError(ex, $"Failed to load scene texture: {p}");
                    return null;
                }
            });
        }

        private static BitmapSource LoadGroundLogoTexture(string path, LogService logService)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

            return _groundLogoTextureCache.GetOrAdd(path, p =>
            {
                try
                {
                    return TextureUtils.LoadTextureFromFile(p);
                }
                catch (Exception ex)
                {
                    logService?.LogError(ex, $"Failed to load ground logo: {p}");
                    return null;
                }
            });
        }

        public static ModelVisual3D CreateGroundPlane(
            LogService logService,
            string groundLogoPath = null,
            double groundLogoScale = 1.0,
            double groundLogoOpacity = 1.0)
        {
            return CreateGroundPlane(null, logService, groundLogoPath, groundLogoScale, groundLogoOpacity);
        }

        public static ModelVisual3D CreateGroundPlane(
            AppSettings settings,
            LogService logService,
            string groundLogoPath = null,
            double groundLogoScale = 1.0,
            double groundLogoOpacity = 1.0)
        {
            MeshGeometry3D groundMesh = new MeshGeometry3D();

            // Ground plane size matching LTK (2400x2400 units, half = 1200)
            const double half = 1200;
            groundMesh.Positions = new Point3DCollection()
            {
                new Point3D(-half, GroundLevel, -half), // Bottom-left
                new Point3D(half, GroundLevel, -half),  // Bottom-right
                new Point3D(half, GroundLevel, half),   // Top-right
                new Point3D(-half, GroundLevel, half)   // Top-left
            };

            // Define triangle indices (two triangles for a square)
            groundMesh.TriangleIndices = new Int32Collection() { 0, 3, 2, 0, 2, 1 };

            // Define texture coordinates
            groundMesh.TextureCoordinates = new PointCollection()
            {
                new System.Windows.Point(0, 1),
                new System.Windows.Point(1, 1),
                new System.Windows.Point(1, 0),
                new System.Windows.Point(0, 0)
            };

            BitmapSource groundTexture = LoadStageGroundTexture(settings, logService);

            Material3D groundMaterial;
            if (groundTexture != null)
            {
                groundMaterial = new DiffuseMaterial(new ImageBrush(groundTexture));
            }
            else
            {
                // Fallback to LTK's token flat color (#232a32) if game files are not present
                groundMaterial = new DiffuseMaterial(new SolidColorBrush(System.Windows.Media.Color.FromRgb(35, 42, 50)));
            }

            GeometryModel3D groundModel = new GeometryModel3D(groundMesh, groundMaterial);
            var scene = new Model3DGroup();
            scene.Children.Add(groundModel);

            BitmapSource groundLogo = LoadGroundLogoTexture(groundLogoPath, logService);
            if (groundLogo != null)
            {
                double logoMaxSize = 850 * Math.Clamp(groundLogoScale, 0.25, 1.5);
                double aspectRatio = (double)groundLogo.PixelWidth / groundLogo.PixelHeight;
                double logoWidth = aspectRatio >= 1 ? logoMaxSize : logoMaxSize * aspectRatio;
                double logoHeight = aspectRatio >= 1 ? logoMaxSize / aspectRatio : logoMaxSize;

                var logoMesh = new MeshGeometry3D
                {
                    Positions = new Point3DCollection
                    {
                        new Point3D(-logoWidth / 2, GroundLevel + GroundLogoElevation, -logoHeight / 2),
                        new Point3D(logoWidth / 2, GroundLevel + GroundLogoElevation, -logoHeight / 2),
                        new Point3D(logoWidth / 2, GroundLevel + GroundLogoElevation, logoHeight / 2),
                        new Point3D(-logoWidth / 2, GroundLevel + GroundLogoElevation, logoHeight / 2)
                    },
                    TriangleIndices = new Int32Collection { 0, 3, 2, 0, 2, 1 },
                    TextureCoordinates = new PointCollection
                    {
                        new Point(0, 0), new Point(1, 0), new Point(1, 1), new Point(0, 1)
                    }
                };

                var logoBrush = new ImageBrush(groundLogo)
                {
                    Stretch = Stretch.Uniform,
                    Opacity = Math.Clamp(groundLogoOpacity, 0.0, 1.0)
                };
                RenderOptions.SetBitmapScalingMode(logoBrush, BitmapScalingMode.HighQuality);
                scene.Children.Add(new GeometryModel3D(logoMesh, new DiffuseMaterial(logoBrush)));
            }

            return new ModelVisual3D { Content = scene };
        }
    }
}
