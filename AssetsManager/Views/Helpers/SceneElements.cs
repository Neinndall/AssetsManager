using System;
using System.IO;
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
        public const string GroundTexturePath = "pack://application:,,,/AssetsManager;component/Resources/Scene/Floor/ground_rift.dds";
        private const double GroundLogoElevation = 2.0;
        public const int SceneTextureMaxSize = 2048;

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, BitmapSource> _textureCache = new();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, BitmapSource> _groundLogoTextureCache = new();

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
            MeshGeometry3D groundMesh = new MeshGeometry3D();

            // Define vertices for a large square plane (e.g., 800x800 units)
            // Y-coordinate is 0 to place it at the base of the model
            groundMesh.Positions = new Point3DCollection()
            {
                new Point3D(-1000, GroundLevel, -1000), // Bottom-left
                new Point3D(1000, GroundLevel, -1000),  // Bottom-right
                new Point3D(1000, GroundLevel, 1000),   // Top-right
                new Point3D(-1000, GroundLevel, 1000)   // Top-left
            };

            // Define triangle indices (two triangles for a square)
            groundMesh.TriangleIndices = new Int32Collection() { 0, 3, 2, 0, 2, 1 };

            // Define texture coordinates (simple mapping for a solid color)
            groundMesh.TextureCoordinates = new PointCollection()
            {
                new System.Windows.Point(0, 1),
                new System.Windows.Point(1, 1),
                new System.Windows.Point(1, 0),
                new System.Windows.Point(0, 0)
            };

            BitmapSource groundTexture = LoadSceneTexture(GroundTexturePath, logService);

            Material3D groundMaterial;
            if (groundTexture != null)
            {
                groundMaterial = new DiffuseMaterial(new ImageBrush(groundTexture));
            }
            else
            {
                // Fallback to a solid color if texture loading fails
                groundMaterial = new DiffuseMaterial(new SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 120, 80))); // Earthy color
                logService.LogError($"Failed to load ground texture from {GroundTexturePath}. Using solid color fallback.");
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
