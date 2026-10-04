using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Core;
using AssetsManager.Services.Viewer.Vfx.Resources;
using AssetsManager.Utils;

namespace AssetsManager.Views.Helpers
{
    /// <summary>
    /// Centralized provider for viewport environment elements:
    /// shared ground appearance and studio skybox cubemap (dynamic WAD cubemap).
    /// </summary>
    public static class SceneElements
    {
        public const double GroundLevel = 1000;
        public const float GroundSize = 2000f;
        public const string GroundTexturePath = "pack://application:,,,/AssetsManager;component/Resources/Scene/ground_rift.dds";
        public const string SkyboxChunkVirtualPath = "assets/maps/skyboxes/riots_sru_skybox_cubemap.dds";
        public const int SceneTextureMaxSize = 2048;

        #region Ground Cache & Loader

        private static readonly object GroundLock = new();
        private static BitmapSource _cachedGroundTexture;
        private static bool _groundLoaded;
        private static BitmapSource _cachedGroundAppearanceTexture;
        private static GroundAppearanceKey? _cachedGroundAppearance;
        private static BitmapSource _cachedGroundLogo;
        private static (string Path, long Modified, long Length)? _cachedGroundLogoFile;

        private readonly record struct GroundAppearanceKey(
            string Path, long Modified, long Length, double Scale, double Opacity);

        internal static bool IsGroundLogoSetting(string propertyName) =>
            string.IsNullOrEmpty(propertyName)
            || propertyName is nameof(AppSettings.CustomGroundLogoPath)
                or nameof(AppSettings.GroundLogoScale) or nameof(AppSettings.GroundLogoOpacity);

        public static void ClearGroundCache()
        {
            lock (GroundLock)
            {
                _groundLoaded = false;
                _cachedGroundTexture = null;
                _cachedGroundAppearanceTexture = null;
                _cachedGroundAppearance = null;
                _cachedGroundLogo = null;
                _cachedGroundLogoFile = null;
            }
        }

        public static BitmapSource LoadGroundTexture(AppSettings settings, LogService logService)
        {
            lock (GroundLock)
            {
                try
                {
                    if (!_groundLoaded)
                    {
                        _cachedGroundTexture = LoadBundledGround(logService);
                        _groundLoaded = true;
                    }

                    string path = settings?.CustomGroundLogoPath;
                    var file = string.IsNullOrWhiteSpace(path) ? null : new FileInfo(path);
                    var appearance = new GroundAppearanceKey(
                        file?.Exists == true ? file.FullName : null,
                        file?.Exists == true ? file.LastWriteTimeUtc.Ticks : 0,
                        file?.Exists == true ? file.Length : 0,
                        Math.Clamp(settings?.GroundLogoScale ?? 1.0, 0.25, 1.5),
                        Math.Clamp(settings?.GroundLogoOpacity ?? 1.0, 0.0, 1.0));
                    if (_cachedGroundAppearance == appearance)
                        return _cachedGroundAppearanceTexture;

                    BitmapSource groundTexture = _cachedGroundTexture;
                    if (appearance.Path != null && appearance.Opacity > 0)
                    {
                        var logoFile = (appearance.Path, appearance.Modified, appearance.Length);
                        if (_cachedGroundLogoFile != logoFile)
                        {
                            _cachedGroundLogo = TextureUtils.LoadTextureFromFile(
                                appearance.Path, SceneTextureMaxSize, SceneTextureMaxSize);
                            _cachedGroundLogoFile = logoFile;
                        }
                        if (_cachedGroundLogo != null)
                            groundTexture = ComposeGroundTexture(
                                _cachedGroundTexture, _cachedGroundLogo, appearance.Scale, appearance.Opacity);
                    }
                    else
                    {
                        _cachedGroundLogo = null;
                        _cachedGroundLogoFile = null;
                    }
                    _cachedGroundAppearanceTexture = groundTexture;
                    _cachedGroundAppearance = appearance;
                    return _cachedGroundAppearanceTexture;
                }
                catch (Exception ex)
                {
                    logService?.LogError(ex, "Failed to load preview ground texture.");
                    return _cachedGroundTexture;
                }
            }
        }

        internal static BitmapSource ComposeGroundTexture(
            BitmapSource ground, BitmapSource logo, double scale, double opacity)
        {
            int width = SceneTextureMaxSize;
            int height = SceneTextureMaxSize;
            double maxSize = 850.0 / GroundSize * Math.Clamp(scale, 0.25, 1.5);
            double aspect = (double)logo.PixelWidth / logo.PixelHeight;
            double logoWidth = width * maxSize * Math.Min(1.0, aspect);
            double logoHeight = height * maxSize / Math.Max(1.0, aspect);
            var visual = new DrawingVisual();
            using (DrawingContext drawing = visual.RenderOpen())
            {
                if (ground != null)
                    drawing.DrawImage(ground, new Rect(0, 0, width, height));
                else
                    drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(35, 42, 50)), null,
                        new Rect(0, 0, width, height));
                drawing.PushOpacity(Math.Clamp(opacity, 0.0, 1.0));
                // Ground UVs run in the opposite vertical direction to the former logo overlay.
                drawing.PushTransform(new ScaleTransform(1, -1, width / 2.0, height / 2.0));
                drawing.DrawImage(logo, new Rect((width - logoWidth) / 2, (height - logoHeight) / 2,
                    logoWidth, logoHeight));
                drawing.Pop();
                drawing.Pop();
            }
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }

        private static BitmapSource LoadBundledGround(LogService logService)
        {
            try
            {
                var uri = new Uri(GroundTexturePath, UriKind.RelativeOrAbsolute);
                var streamInfo = Application.GetResourceStream(uri);
                if (streamInfo?.Stream != null)
                {
                    using var stream = streamInfo.Stream;
                    return TextureUtils.LoadTexture(stream, ".dds", SceneTextureMaxSize);
                }
            }
            catch (Exception ex)
            {
                logService?.LogDebug($"Could not load ground from resource stream: {ex.Message}");
            }

            string diskPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Scene", "ground_rift.dds");
            if (File.Exists(diskPath))
            {
                try
                {
                    using var stream = File.OpenRead(diskPath);
                    return TextureUtils.LoadTexture(stream, ".dds", SceneTextureMaxSize);
                }
                catch (Exception ex)
                {
                    logService?.LogError(ex, $"Failed to load ground from disk: {diskPath}");
                }
            }

            return null;
        }

        #endregion

        #region Studio Skybox Cubemap Cache & Loader

        private static readonly object SkyLock = new();
        private static VfxCubeMapData _cachedGenericSky;
        private static bool _skyAttempted;

        public static void ClearSkyCache()
        {
            lock (SkyLock)
            {
                _skyAttempted = false;
                _cachedGenericSky = null;
            }
        }

        /// <summary>
        /// Clears both ground texture and skybox cubemap caches.
        /// </summary>
        public static void ClearSceneCache()
        {
            ClearGroundCache();
            ClearSkyCache();
        }

        internal static VfxCubeMapData LoadGenericSkyCube(LogService logService) =>
            LoadGenericSkyCube(null, logService);

        /// <summary>
        /// Loads the official Summoner's Rift sky cubemap from Map11.wad.client matching LTK Manager.
        /// </summary>
        internal static VfxCubeMapData LoadGenericSkyCube(AppSettings settings, LogService logService)
        {
            lock (SkyLock)
            {
                if (_skyAttempted)
                    return _cachedGenericSky;

                _skyAttempted = true;
                try
                {
                    _cachedGenericSky = LoadSkyFromGame(settings, logService);
                    return _cachedGenericSky;
                }
                catch (Exception ex)
                {
                    logService?.LogError(ex, "Failed to load studio skybox cubemap from League installation.");
                    _cachedGenericSky = null;
                    return null;
                }
            }
        }

        private static VfxCubeMapData LoadSkyFromGame(AppSettings settings, LogService logService)
        {
            using var stream = OpenMap11Chunk(settings, SkyboxChunkVirtualPath, logService);
            return stream == null ? null : VfxCubeMapDecoder.Decode(stream);
        }

        #endregion

        #region Game WAD Chunk Resolution Helpers

        private static MemoryStream OpenMap11Chunk(AppSettings settings, string virtualPath, LogService logService)
        {
            string gameRoot = GetPreferredGameRoot(settings);
            if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot))
            {
                logService?.LogDebug($"No valid League of Legends installation directory configured for '{virtualPath}'.");
                return null;
            }

            string mapWadPath = FindMap11Wad(gameRoot);
            if (string.IsNullOrWhiteSpace(mapWadPath) || !File.Exists(mapWadPath))
            {
                logService?.LogWarning($"Map11.wad.client was not found under '{gameRoot}'.");
                return null;
            }

            ulong chunkHash = LeagueToolkit.Hashing.XxHash64Ext.Hash(virtualPath);
            using var wad = new LeagueToolkit.Core.Wad.WadFile(mapWadPath);
            if (!wad.Chunks.TryGetValue(chunkHash, out var chunk))
            {
                logService?.LogWarning($"Chunk {chunkHash:x16} ({virtualPath}) not found in '{mapWadPath}'.");
                return null;
            }

            using var decompressed = wad.LoadChunkDecompressed(chunk);
            return new MemoryStream(decompressed.Span.ToArray(), writable: false);
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

        #endregion
    }
}
