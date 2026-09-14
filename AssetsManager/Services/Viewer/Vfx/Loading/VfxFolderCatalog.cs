using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AssetsManager.Services.Core;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Viewer.Vfx.Loading;

/// <summary>Classifies extracted BINs by their object types, including files retaining WAD hash names.</summary>
internal static class VfxFolderCatalog
{
    internal static IReadOnlyList<VfxSkinItem> Scan(string root, CancellationToken cancellationToken, LogService log = null)
    {
        var skins = new List<VfxSkinItem>();
        var effects = new List<VfxSkinItem>();
        uint skinClass = Fnv1a.HashLower("SkinCharacterDataProperties");
        uint systemClass = Fnv1a.HashLower("VfxSystemDefinitionData");
        foreach (string path in Directory.EnumerateFiles(root, "*.bin", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var stream = File.OpenRead(path);
                var tree = new BinTree(stream);
                bool skin = tree.Objects.Values.Any(item => item.ClassHash == skinClass);
                if (!skin && !tree.Objects.Values.Any(item => item.ClassHash == systemClass)) continue;
                string stem = Path.GetFileNameWithoutExtension(path);
                int index = stem.StartsWith("skin", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(stem.AsSpan(4), out int id) ? id : int.MaxValue;
                var entry = new VfxSkinItem
                {
                    DisplayName = Path.GetRelativePath(root, path),
                    BinPath = Path.GetFullPath(path),
                    SkinIndex = index
                };
                (skin ? skins : effects).Add(entry);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
            {
                log?.LogWarning($"Unable to classify VFX BIN '{path}': {ex.Message}");
            }
        }
        string rootName = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string rootStem = rootName;
        if (rootStem.EndsWith(".wad.client", StringComparison.OrdinalIgnoreCase))
            rootStem = rootStem[..^11];
        else if (rootStem.EndsWith(".wad", StringComparison.OrdinalIgnoreCase))
            rootStem = rootStem[..^4];
        rootStem = rootStem.Trim().ToLowerInvariant();

        int CalculateScore(VfxSkinItem item)
        {
            string normalized = item.DisplayName.Replace('\\', '/').ToLowerInvariant();
            int score = 100;

            if (!string.IsNullOrEmpty(rootStem))
            {
                if (normalized.Contains($"/characters/{rootStem}/skins/") ||
                    normalized.StartsWith($"data/characters/{rootStem}/skins/") ||
                    normalized.StartsWith($"assets/characters/{rootStem}/skins/"))
                {
                    score = 0;
                }
                else if (normalized.Contains($"/{rootStem}/skins/"))
                {
                    score = 10;
                }
            }

            if (normalized.Contains("faerie") || normalized.Contains("critter") ||
                normalized.Contains("pet") || normalized.Contains("turret") ||
                normalized.Contains("trap") || normalized.Contains("dummy") ||
                normalized.Contains("clone") || normalized.Contains("projectile") ||
                normalized.Contains("minion") || normalized.Contains("companion"))
            {
                score += 500;
            }

            return score;
        }

        return (skins.Count > 0 ? skins : effects)
            .OrderBy(CalculateScore)
            .ThenBy(item => item.SkinIndex)
            .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
