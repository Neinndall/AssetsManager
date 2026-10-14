using AssetsManager.Services.Viewer.Resources;
using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Windows;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Core;
using AssetsManager.Utils;

namespace AssetsManager.Views.Helpers
{
    internal readonly record struct GroundAppearance(
        BitmapSource Texture, BitmapSource Logo = null, double LogoScale = 1, double LogoOpacity = 1)
    {
        internal Vector2 LogoUvSize
        {
            get
            {
                if (Logo == null) return Vector2.One;
                double scale = double.IsFinite(LogoScale) ? Math.Clamp(LogoScale, 0.25, 1.5) : 1;
                double size = 850.0 / SceneElements.GroundSize * scale;
                double aspect = (double)Logo.PixelWidth / Logo.PixelHeight;
                return new Vector2((float)(size * Math.Min(1, aspect)), (float)(size / Math.Max(1, aspect)));
            }
        }

        internal float Opacity => double.IsFinite(LogoOpacity) ? (float)Math.Clamp(LogoOpacity, 0, 1) : 1;
    }

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

        #region Ground Cache & Loader

        private static readonly object GroundLock = new();
        private static BitmapSource _cachedGroundTexture;
        private static bool _groundLoaded;
        private static BitmapSource _cachedGroundLogo;
        private static (string Path, long Modified, long Length)? _cachedGroundLogoFile;

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
                _cachedGroundLogo = null;
                _cachedGroundLogoFile = null;
            }
        }

        internal static GroundAppearance LoadGroundAppearance(AppSettings settings, LogService logService)
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
                    if (file?.Exists == true)
                    {
                        var logoFile = (file.FullName, file.LastWriteTimeUtc.Ticks, file.Length);
                        if (_cachedGroundLogoFile != logoFile)
                        {
                            _cachedGroundLogo = TextureUtils.LoadTextureFromFile(file.FullName);
                            _cachedGroundLogoFile = logoFile;
                        }
                    }
                    else
                    {
                        _cachedGroundLogo = null;
                        _cachedGroundLogoFile = null;
                    }
                    return new GroundAppearance(_cachedGroundTexture, _cachedGroundLogo,
                        settings?.GroundLogoScale ?? 1, settings?.GroundLogoOpacity ?? 1);
                }
                catch (Exception ex)
                {
                    logService?.LogError(ex, "Failed to load preview ground texture.");
                    return new GroundAppearance(_cachedGroundTexture);
                }
            }
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
                    return TextureUtils.LoadTexture(stream, ".dds");
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
                    return TextureUtils.LoadTexture(stream, ".dds");
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
        private static CubeMapData _cachedGenericSky;
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

        internal static CubeMapData LoadGenericSkyCube(LogService logService) =>
            LoadGenericSkyCube(null, logService);

        /// <summary>
        /// Loads the official Summoner's Rift sky cubemap from Map11.wad.client matching LTK Manager.
        /// </summary>
        internal static CubeMapData LoadGenericSkyCube(AppSettings settings, LogService logService)
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

        private static CubeMapData LoadSkyFromGame(AppSettings settings, LogService logService)
        {
            using var stream = OpenMap11Chunk(settings, SkyboxChunkVirtualPath, logService);
            return stream == null ? null : CubeMapDecoder.Decode(stream);
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
