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

    internal static class SknMaterialTextureResolver
    {
        private static readonly uint SkinPropertiesClass = Fnv1a.HashLower("SkinCharacterDataProperties");
        private static readonly uint StaticMaterialClass = Fnv1a.HashLower("StaticMaterialDef");
        private static readonly uint SkinMeshProperties = Fnv1a.HashLower("skinMeshProperties");
        private static readonly uint MaterialOverride = Fnv1a.HashLower("materialOverride");
        private static readonly uint Texture = Fnv1a.HashLower("texture");
        private static readonly uint Submesh = Fnv1a.HashLower("submesh");
        private static readonly uint Material = Fnv1a.HashLower("Material");
        private static readonly uint SamplerValues = Fnv1a.HashLower("samplerValues");
        private static readonly uint TextureName = Fnv1a.HashLower("textureName");
        private static readonly uint TexturePath = Fnv1a.HashLower("texturePath");

        internal static SknMaterialTextureResolution Resolve(
            BinTree binTree,
            IEnumerable<string> availableTextureKeys)
        {
            var textureKeys = availableTextureKeys?.ToList() ?? new List<string>();
            var materialTextures = BuildMaterialTextureMap(binTree, textureKeys);
            var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string defaultTextureKey = null;

            foreach (BinTreeObject obj in binTree.Objects.Values)
            {
                if (obj.ClassHash != SkinPropertiesClass ||
                    !obj.Properties.TryGetValue(SkinMeshProperties, out BinTreeProperty meshProperty) ||
                    meshProperty is not BinTreeStruct meshProperties)
                {
                    continue;
                }

                if (meshProperties.Properties.TryGetValue(Texture, out BinTreeProperty defaultProperty) &&
                    defaultProperty is BinTreeString defaultTexture)
                {
                    defaultTextureKey ??= MatchTextureKey(defaultTexture.Value, textureKeys);
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

                    string textureKey = null;
                    if (TryGetString(entry, Texture, out string directTexturePath))
                    {
                        textureKey = MatchTextureKey(directTexturePath, textureKeys);
                    }

                    if (textureKey == null &&
                        entry.Properties.TryGetValue(Material, out BinTreeProperty linkProperty) &&
                        linkProperty is BinTreeObjectLink materialLink)
                    {
                        materialTextures.TryGetValue(materialLink.Value, out textureKey);
                    }

                    string normalizedSubmesh = NormalizeMaterialKey(submeshName);
                    if (!string.IsNullOrEmpty(normalizedSubmesh) && textureKey != null)
                    {
                        overrides[normalizedSubmesh] = textureKey;
                    }
                }
            }

            return new SknMaterialTextureResolution(defaultTextureKey, overrides);
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
                return null;
            }

            string rootPath = normalizedPath[..markerIndex];
            string[] parts = normalizedPath[(markerIndex + marker.Length)..]
                .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || !parts[1].Equals("skins", StringComparison.OrdinalIgnoreCase))
            {
                return null;
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

        private static Dictionary<uint, string> BuildMaterialTextureMap(
            BinTree binTree,
            IReadOnlyList<string> textureKeys)
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

                string textureKey = MatchTextureKey(bestTexturePath, textureKeys);
                if (textureKey != null)
                {
                    result[pathHash] = textureKey;
                }
            }

            return result;
        }

        private static int RankColorSampler(string slotName)
        {
            string normalized = NormalizeToken(slotName);
            if (normalized == "maintexture") return 300;
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
    }
}
