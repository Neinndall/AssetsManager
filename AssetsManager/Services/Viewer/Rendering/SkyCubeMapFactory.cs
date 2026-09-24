using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Core;
using AssetsManager.Services.Viewer.Vfx.Resources;
using AssetsManager.Utils;

namespace AssetsManager.Services.Viewer.Rendering
{
    /// <summary>
    /// Builds the generic AssetsManager Studio sky as the same six-face CPU cubemap used by authored MAP skies.
    /// Face order follows OpenGL cubemaps: +X, -X, +Y, -Y, +Z, -Z.
    /// </summary>
    internal static class SkyCubeMapFactory
    {
        private const int MaxFaceSize = 2048;

        private static readonly string[] GenericFacePaths =
        {
            "pack://application:,,,/AssetsManager;component/Resources/Scene/Sky/sky_right.dds", // +X
            "pack://application:,,,/AssetsManager;component/Resources/Scene/Sky/sky_left.dds",  // -X
            "pack://application:,,,/AssetsManager;component/Resources/Scene/Sky/sky_up.dds",    // +Y
            "pack://application:,,,/AssetsManager;component/Resources/Scene/Sky/sky_down.dds",  // -Y
            "pack://application:,,,/AssetsManager;component/Resources/Scene/Sky/sky_back.dds",  // +Z
            "pack://application:,,,/AssetsManager;component/Resources/Scene/Sky/sky_front.dds", // -Z
        };

        private static readonly object CacheGate = new();
        private static VfxCubeMapData _cachedGenericSky;
        private static bool _genericSkyAttempted;

        internal static VfxCubeMapData LoadGeneric(LogService logService)
        {
            lock (CacheGate)
            {
                if (_genericSkyAttempted)
                    return _cachedGenericSky;

                _genericSkyAttempted = true;
                try
                {
                    var faces = new List<byte[]>(6);
                    int width = 0;
                    int height = 0;

                    foreach (string path in GenericFacePaths)
                    {
                        BitmapSource bitmap = LoadFace(path);
                        if (bitmap == null)
                        {
                            logService?.LogError($"Failed to load generic Studio sky face: {path}");
                            _cachedGenericSky = null;
                            return null;
                        }

                        if (width == 0)
                        {
                            width = bitmap.PixelWidth;
                            height = bitmap.PixelHeight;
                        }
                        else if (bitmap.PixelWidth != width || bitmap.PixelHeight != height)
                        {
                            logService?.LogError(
                                $"Generic Studio sky faces must share one size. Expected {width}x{height}, " +
                                $"got {bitmap.PixelWidth}x{bitmap.PixelHeight} for {path}.");
                            _cachedGenericSky = null;
                            return null;
                        }

                        faces.Add(ToRgbaBottomUp(bitmap));
                    }

                    _cachedGenericSky = new VfxCubeMapData(width, height, faces);
                    return _cachedGenericSky.IsValid ? _cachedGenericSky : null;
                }
                catch (Exception ex)
                {
                    logService?.LogError(ex, "Failed to build the generic Studio sky cubemap.");
                    _cachedGenericSky = null;
                    return null;
                }
            }
        }

        private static BitmapSource LoadFace(string path)
        {
            var resource = Application.GetResourceStream(new Uri(path));
            if (resource?.Stream == null)
                return null;

            using (resource.Stream)
                return TextureUtils.LoadTexture(resource.Stream, ".dds", MaxFaceSize);
        }

        private static byte[] ToRgbaBottomUp(BitmapSource source)
        {
            BitmapSource bitmap = source.Format == PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

            int width = bitmap.PixelWidth;
            int height = bitmap.PixelHeight;
            int stride = checked(width * 4);
            byte[] bgraTopDown = new byte[checked(stride * height)];
            bitmap.CopyPixels(bgraTopDown, stride, 0);

            byte[] rgbaBottomUp = new byte[bgraTopDown.Length];
            for (int y = 0; y < height; y++)
            {
                int sourceRow = y * stride;
                int targetRow = (height - 1 - y) * stride;
                for (int x = 0; x < width; x++)
                {
                    int sourceOffset = sourceRow + x * 4;
                    int targetOffset = targetRow + x * 4;
                    rgbaBottomUp[targetOffset] = bgraTopDown[sourceOffset + 2];
                    rgbaBottomUp[targetOffset + 1] = bgraTopDown[sourceOffset + 1];
                    rgbaBottomUp[targetOffset + 2] = bgraTopDown[sourceOffset];
                    rgbaBottomUp[targetOffset + 3] = bgraTopDown[sourceOffset + 3];
                }
            }

            return rgbaBottomUp;
        }
    }
}
