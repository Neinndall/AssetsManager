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

                        faces.Add(ToRgba(bitmap));
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

        private static byte[] ToRgba(BitmapSource source)
        {
            BitmapSource bitmap = source.Format == PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

            int width = bitmap.PixelWidth;
            int height = bitmap.PixelHeight;
            int stride = checked(width * 4);
            byte[] bgra = new byte[checked(stride * height)];
            bitmap.CopyPixels(bgra, stride, 0);

            byte[] rgba = new byte[bgra.Length];
            for (int i = 0; i < bgra.Length; i += 4)
            {
                rgba[i] = bgra[i + 2];
                rgba[i + 1] = bgra[i + 1];
                rgba[i + 2] = bgra[i];
                rgba[i + 3] = bgra[i + 3];
            }

            return rgba;
        }
    }
}
