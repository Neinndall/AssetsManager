using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AssetsManager.Utils;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Viewer
{
    internal sealed record SknMaterialTextureResolution(
        string DefaultTextureKey,
        IReadOnlyDictionary<string, string> Overrides);

    internal sealed record SknMaterialTextureMetadata(
        string DefaultTexturePath,
        IReadOnlyDictionary<string, IReadOnlyList<string>> OverrideTexturePaths)
    {
        internal IEnumerable<string> ReferencedTexturePaths =>
            OverrideTexturePaths.Values
                .Select(paths => paths.FirstOrDefault())
                .Prepend(DefaultTexturePath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    internal static class SknMaterialTextureResolver
    {
        private static readonly uint SkinPropertiesClass = Fnv1a.HashLower("SkinCharacterDataProperties");
        private static readonly uint StaticMaterialClass = Fnv1a.HashLower("StaticMaterialDef");
        private static readonly uint SkinMeshProperties = Fnv1a.HashLower("skinMeshProperties");
        private static readonly uint SimpleSkin = Fnv1a.HashLower("simpleSkin");
        private static readonly uint MaterialOverride = Fnv1a.HashLower("materialOverride");
        private static readonly uint Texture = Fnv1a.HashLower("texture");
        private static readonly uint Submesh = Fnv1a.HashLower("submesh");
        private static readonly uint Material = Fnv1a.HashLower("Material");
        private static readonly uint SamplerValues = Fnv1a.HashLower("samplerValues");
        private static readonly uint TextureName = Fnv1a.HashLower("textureName");
        private static readonly uint TexturePath = Fnv1a.HashLower("texturePath");

        internal static SknMaterialTextureResolution Resolve(
            BinTree binTree,
            IEnumerable<string> availableTextureKeys) =>
            Resolve(ReadMetadata(binTree), availableTextureKeys);

        internal static SknMaterialTextureMetadata ReadMetadata(BinTree binTree)
        {
            var materialTexturePaths = BuildMaterialTexturePathMap(binTree);
            var overrideTexturePaths = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            string defaultTexturePath = null;

            foreach (BinTreeObject obj in binTree.Objects.Values)
            {
                if (obj.ClassHash != SkinPropertiesClass ||
                    !obj.Properties.TryGetValue(SkinMeshProperties, out BinTreeProperty meshProperty) ||
                    meshProperty is not BinTreeStruct meshProperties)
                {
                    continue;
                }

                if (defaultTexturePath == null &&
                    TryGetString(meshProperties, Texture, out string texturePath))
                {
                    defaultTexturePath = texturePath;
                }

                if (!meshProperties.Properties.TryGetValue(MaterialOverride, out BinTreeProperty overrideProperty) ||
                    overrideProperty is not BinTreeContainer materialOverrides)
                {
                    continue;
                }

                foreach (BinTreeProperty element in materialOverrides.Elements)
                {
                    if (element is not BinTreeStruct entry ||
                        !TryGetString(entry, Submesh, out string submeshName))
                    {
                        continue;
                    }

                    var candidates = new List<string>(2);
                    if (TryGetString(entry, Texture, out string directTexturePath))
                    {
                        candidates.Add(directTexturePath);
                    }

                    if (entry.Properties.TryGetValue(Material, out BinTreeProperty linkProperty) &&
                        linkProperty is BinTreeObjectLink materialLink &&
                        materialTexturePaths.TryGetValue(materialLink.Value, out string materialTexturePath))
                    {
                        candidates.Add(materialTexturePath);
                    }

                    string normalizedSubmesh = NormalizeMaterialKey(submeshName);
                    if (!string.IsNullOrEmpty(normalizedSubmesh) && candidates.Count > 0)
                    {
                        overrideTexturePaths[normalizedSubmesh] = candidates;
                    }
                }
            }

            return new SknMaterialTextureMetadata(defaultTexturePath, overrideTexturePaths);
        }

        internal static SknMaterialTextureResolution Resolve(
            SknMaterialTextureMetadata metadata,
            IEnumerable<string> availableTextureKeys)
        {
            var textureKeys = availableTextureKeys?.ToList() ?? new List<string>();
            var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach ((string submesh, IReadOnlyList<string> texturePaths) in metadata.OverrideTexturePaths)
            {
                string textureKey = texturePaths
                    .Select(path => MatchTextureKey(path, textureKeys))
                    .FirstOrDefault(key => key != null);
                if (textureKey != null)
                {
                    overrides[submesh] = textureKey;
                }
            }

            return new SknMaterialTextureResolution(
                MatchTextureKey(metadata.DefaultTexturePath, textureKeys),
                overrides);
        }

        internal static string TryResolveBinPath(string sknPath)
        {
            if (string.IsNullOrWhiteSpace(sknPath))
            {
                return null;
            }

            string normalizedPath = Path.GetFullPath(sknPath)
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            string marker = $"{Path.DirectorySeparatorChar}assets{Path.DirectorySeparatorChar}characters{Path.DirectorySeparatorChar}";
            int markerIndex = normalizedPath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                return TryResolveCompanionBinPath(normalizedPath);
            }

            string rootPath = normalizedPath[..markerIndex];
            string[] parts = normalizedPath[(markerIndex + marker.Length)..]
                .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || !parts[1].Equals("skins", StringComparison.OrdinalIgnoreCase))
            {
                return TryResolveCompanionBinPath(normalizedPath);
            }

            string skinBinName = GetSkinBinName(parts[2]);
            if (skinBinName == null)
            {
                return null;
            }

            string virtualPath = $"data/characters/{parts[0]}/skins/{skinBinName}".ToLowerInvariant();
            string namedPath = Path.Combine(rootPath, virtualPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(namedPath))
            {
                return namedPath;
            }

            // Unknown paths extracted from a WAD retain their xxHash64 as the file name.
            string hashedPath = Path.Combine(rootPath, $"{XxHash64Ext.Hash(virtualPath):x16}.bin");
            return File.Exists(hashedPath) ? hashedPath : null;
        }

        internal static string TryResolveTexturePath(string sknPath, string assetTexturePath)
        {
            DirectoryInfo characterRoot = FindCharacterRoot(sknPath);
            if (characterRoot == null || string.IsNullOrWhiteSpace(assetTexturePath))
            {
                return null;
            }

            string assetPath = assetTexturePath.Replace('\\', '/').TrimStart('/');
            string prefix = $"assets/characters/{characterRoot.Name}/";
            if (!assetPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string candidate = Path.GetFullPath(Path.Combine(
                characterRoot.FullName,
                assetPath[prefix.Length..].Replace('/', Path.DirectorySeparatorChar)));
            string extension = Path.GetExtension(candidate);
            string rootedPrefix =
                characterRoot.FullName.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return candidate.StartsWith(rootedPrefix, StringComparison.OrdinalIgnoreCase) &&
                   (extension.Equals(".tex", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".dds", StringComparison.OrdinalIgnoreCase)) &&
                   File.Exists(candidate)
                ? candidate
                : null;
        }

        private static DirectoryInfo FindCharacterRoot(string sknPath)
        {
            if (string.IsNullOrWhiteSpace(sknPath))
            {
                return null;
            }

            for (DirectoryInfo directory = Directory.GetParent(Path.GetFullPath(sknPath));
                 directory?.Parent != null;
                 directory = directory.Parent)
            {
                if (directory.Name.Equals("themes", StringComparison.OrdinalIgnoreCase) ||
                    directory.Name.Equals("skins", StringComparison.OrdinalIgnoreCase))
                {
                    return directory.Parent;
                }
            }

            return null;
        }

        private static string TryResolveCompanionBinPath(string normalizedSknPath)
        {
            string themesMarker = $"{Path.DirectorySeparatorChar}themes{Path.DirectorySeparatorChar}";
            int themesIndex = normalizedSknPath.IndexOf(themesMarker, StringComparison.OrdinalIgnoreCase);
            if (themesIndex < 0)
            {
                return null;
            }

            string characterRoot = normalizedSknPath[..themesIndex];
            string characterName = Path.GetFileName(characterRoot);
            string skinsDirectory = Path.Combine(characterRoot, "skins");
            if (string.IsNullOrWhiteSpace(characterName) || !Directory.Exists(skinsDirectory))
            {
                return null;
            }

            string relativeModelPath = Path.GetRelativePath(characterRoot, normalizedSknPath)
                .Replace(Path.DirectorySeparatorChar, '/');
            string virtualModelPath = $"assets/characters/{characterName}/{relativeModelPath}";

            foreach (string binPath in Directory.EnumerateFiles(
                         skinsDirectory,
                         "skin*.bin",
                         SearchOption.TopDirectoryOnly)
                     .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            {
                using var stream = File.OpenRead(binPath);
                var binTree = new BinTree(stream);
                if (ReferencesModel(binTree, virtualModelPath))
                {
                    return binPath;
                }
            }

            return null;
        }

        private static bool ReferencesModel(BinTree binTree, string virtualModelPath)
        {
            string expectedPath = NormalizeAssetPath(virtualModelPath);
            foreach (BinTreeObject obj in binTree.Objects.Values)
            {
                if (obj.ClassHash != SkinPropertiesClass ||
                    !obj.Properties.TryGetValue(SkinMeshProperties, out BinTreeProperty meshProperty) ||
                    meshProperty is not BinTreeStruct meshProperties ||
                    !meshProperties.Properties.TryGetValue(SimpleSkin, out BinTreeProperty simpleSkinProperty) ||
                    simpleSkinProperty is not BinTreeString simpleSkin)
                {
                    continue;
                }

                if (NormalizeAssetPath(simpleSkin.Value).Equals(
                        expectedPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        internal static string FindUnambiguousFallback(IEnumerable<string> availableTextureKeys)
        {
            var candidates = TextureUtils.GetColorTextureCandidates(availableTextureKeys)
                .Where(key => !IsPresentationTexture(key))
                .ToList();
            return candidates.Count == 1 ? candidates[0] : null;
        }

        internal static string NormalizeMaterialKey(string materialName)
        {
            if (string.IsNullOrWhiteSpace(materialName))
            {
                return string.Empty;
            }

            string key = materialName.TrimEnd('\0').ToLowerInvariant();
            key = Regex.Replace(key, @"_?skn$", string.Empty, RegexOptions.IgnoreCase);
            return Regex.Replace(key, @"[^a-z0-9]", string.Empty);
        }

        private static Dictionary<uint, string> BuildMaterialTexturePathMap(BinTree binTree)
        {
            var result = new Dictionary<uint, string>();
            foreach ((uint pathHash, BinTreeObject obj) in binTree.Objects)
            {
                if (obj.ClassHash != StaticMaterialClass ||
                    !obj.Properties.TryGetValue(SamplerValues, out BinTreeProperty samplerProperty) ||
                    samplerProperty is not BinTreeContainer samplers)
                {
                    continue;
                }

                string bestTexturePath = null;
                int bestRank = 0;
                foreach (BinTreeProperty element in samplers.Elements)
                {
                    if (element is not BinTreeStruct sampler ||
                        !TryGetString(sampler, TextureName, out string slotName) ||
                        !TryGetString(sampler, TexturePath, out string texturePath))
                    {
                        continue;
                    }

                    int rank = RankColorSampler(slotName);
                    if (rank > bestRank)
                    {
                        bestRank = rank;
                        bestTexturePath = texturePath;
                    }
                }

                if (bestTexturePath != null)
                {
                    result[pathHash] = bestTexturePath;
                }
            }

            return result;
        }

        private static int RankColorSampler(string slotName)
        {
            string normalized = NormalizeToken(slotName);
            if (normalized == "maintexture") return 300;
            if (normalized == "layertex01") return 250;
            if (normalized == "diffusetexture") return 200;

            if (normalized.Contains("mask") ||
                normalized.Contains("normal") ||
                normalized.Contains("spec") ||
                normalized.Contains("rough") ||
                normalized.Contains("metal") ||
                normalized.Contains("matcap") ||
                normalized.Contains("emissive"))
            {
                return 0;
            }

            return normalized.Contains("basecolor") ||
                   normalized.Contains("albedo") ||
                   normalized.Contains("diffuse") ||
                   normalized.Contains("color")
                ? 100
                : 0;
        }

        private static bool TryGetString(BinTreeStruct value, uint propertyHash, out string result)
        {
            if (value.Properties.TryGetValue(propertyHash, out BinTreeProperty property) &&
                property is BinTreeString text &&
                !string.IsNullOrWhiteSpace(text.Value))
            {
                result = text.Value;
                return true;
            }

            result = null;
            return false;
        }

        private static string MatchTextureKey(string texturePath, IReadOnlyList<string> availableKeys)
        {
            if (string.IsNullOrWhiteSpace(texturePath))
            {
                return null;
            }

            string fileName = PathUtils.TruncateAtDot(Path.GetFileNameWithoutExtension(
                texturePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));
            return availableKeys.FirstOrDefault(key =>
                key.Equals(fileName, StringComparison.OrdinalIgnoreCase));
        }

        private static string GetSkinBinName(string skinFolder)
        {
            if (skinFolder.Equals("base", StringComparison.OrdinalIgnoreCase))
            {
                return "skin0.bin";
            }

            Match match = Regex.Match(skinFolder, @"^skin0*(\d+)$", RegexOptions.IgnoreCase);
            return match.Success && int.TryParse(match.Groups[1].Value, out int skinId)
                ? $"skin{skinId}.bin"
                : null;
        }

        private static bool IsPresentationTexture(string textureKey)
        {
            string normalized = NormalizeToken(textureKey);
            return normalized.Contains("loadscreen") ||
                   normalized.Contains("splash") ||
                   normalized.Contains("loading");
        }

        private static string NormalizeToken(string value) =>
            Regex.Replace(value?.ToLowerInvariant() ?? string.Empty, @"[^a-z0-9]", string.Empty);

        private static string NormalizeAssetPath(string value) =>
            value?.Replace('\\', '/').Trim().ToLowerInvariant() ?? string.Empty;
    }
}
