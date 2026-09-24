using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AssetsManager.Services.Core;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Viewer.Vfx.Loading
{
    /// <summary>
    /// Discovers MAPGEO backdrops available from the configured game installations without extracting
    /// their WADs. Project MAP sources are merged separately and remain authoritative at load time.
    /// </summary>
    internal static class VfxInstallationMapCatalog
    {
        internal const string MapGeometryPrefix = "data/maps/mapgeometry/";
        private const string GeometrySuffix = ".mapgeo";

        internal static IReadOnlyList<MapSceneSource> Discover(
            AppSettings settings,
            string projectRoot,
            Func<ulong, string> resolveGamePath,
            CancellationToken cancellationToken,
            LogService log = null)
        {
            var found = new Dictionary<string, MapSceneSource>(StringComparer.OrdinalIgnoreCase);
            foreach (string installationRoot in MapAssetResolver.GetInstallationRoots(settings))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string gameRoot = Path.Combine(installationRoot, "Game");
                if (!Directory.Exists(gameRoot))
                    continue;

                IEnumerable<string> mapWads;
                try
                {
                    mapWads = Directory.EnumerateFiles(gameRoot, "Map*.wad.client", SearchOption.AllDirectories)
                        .Concat(Directory.EnumerateFiles(gameRoot, "Map*.wad", SearchOption.AllDirectories))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log?.LogWarning($"Unable to enumerate installation MAP WADs under '{gameRoot}': {ex.Message}");
                    continue;
                }

                foreach (string wadPath in mapWads)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        using var wad = new WadFile(wadPath);
                        foreach (ulong pathHash in wad.Chunks.Keys)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            string path = NormalizeResolvedPath(resolveGamePath?.Invoke(pathHash));
                            if (!TryMapGeometryPath(path, out MapPath map))
                                continue;

                            found.TryAdd(map.Value, new MapSceneSource(map, null, ProjectRoot(projectRoot)));
                        }

                        // Local hash catalogs can be partially populated. Always probe the conventional
                        // shipping geometry names as well; dictionary identity deduplicates paths already
                        // resolved from the catalog while recovering variants whose path hash is unnamed.
                        foreach (string candidate in ConventionalGeometryPaths(wadPath))
                        {
                            ulong hash = XxHash64Ext.Hash(candidate);
                            if (!wad.Chunks.ContainsKey(hash) || !TryMapGeometryPath(candidate, out MapPath map))
                                continue;
                            found.TryAdd(map.Value, new MapSceneSource(map, null, ProjectRoot(projectRoot)));
                        }
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
                    {
                        log?.LogWarning($"Unable to inspect MAP WAD '{wadPath}': {ex.Message}");
                    }
                }
            }

            return found.Values
                .OrderBy(source => source.Map.Value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        internal static bool TryMapGeometryPath(string path, out MapPath map)
        {
            map = null;
            string normalized = PathUtils.ToVirtualPath(path)?.TrimStart('/');
            if (string.IsNullOrWhiteSpace(normalized) ||
                !normalized.StartsWith(MapGeometryPrefix, StringComparison.OrdinalIgnoreCase) ||
                !normalized.EndsWith(GeometrySuffix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return MapPath.TryFromEntryPath(normalized, out map);
        }

        internal static IReadOnlyList<string> ConventionalGeometryPaths(string wadPath)
        {
            string name = Path.GetFileName(wadPath) ?? string.Empty;
            if (name.EndsWith(".wad.client", StringComparison.OrdinalIgnoreCase))
                name = name[..^".wad.client".Length];
            else if (name.EndsWith(".wad", StringComparison.OrdinalIgnoreCase))
                name = name[..^".wad".Length];

            name = name.Trim().ToLowerInvariant();
            if (!name.StartsWith("map", StringComparison.Ordinal) || name.Length <= 3)
                return Array.Empty<string>();

            string root = $"{MapGeometryPrefix}{name}/";
            return new[]
            {
                $"{root}base.mapgeo",
                $"{root}base_srx.mapgeo",
                $"{root}base_tft.mapgeo"
            };
        }

        internal static string BackdropKey(MapSceneSource source) =>
            source?.Map?.Value?.Trim().ToLowerInvariant();

        internal static string Label(MapSceneSource source, bool projectSource)
        {
            if (source?.Map == null)
                return projectSource ? "Project MAP" : "Game MAP";

            string[] parts = source.Map.Value
                .Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries);
            string folder = parts.Length >= 2 ? parts[^2] : parts.FirstOrDefault() ?? source.Map.Value;
            string geometry = parts.LastOrDefault() ?? source.Map.Value;
            string label = string.Equals(folder, geometry, StringComparison.OrdinalIgnoreCase)
                ? geometry
                : $"{folder} · {geometry}";
            return projectSource ? $"{label} · Project" : label;
        }

        private static string NormalizeResolvedPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || IsHexOnly(path))
                return null;
            return PathUtils.ToVirtualPath(path).TrimStart('/');
        }

        private static bool IsHexOnly(string value)
        {
            string text = value?.Trim();
            if (string.IsNullOrEmpty(text) || (text.Length != 16 && text.Length != 8))
                return false;
            return text.All(Uri.IsHexDigit);
        }

        private static string ProjectRoot(string projectRoot) =>
            string.IsNullOrWhiteSpace(projectRoot) ? null : Path.GetFullPath(projectRoot);
    }
}
