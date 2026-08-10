using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Viewer.Resolvers
{
    internal sealed record SknMaterialTextureResolution(
        string DefaultTextureKey,
        IReadOnlyDictionary<string, string> Overrides,
        IReadOnlyDictionary<string, ModelMaterialEffectDefinition> Effects,
        IReadOnlySet<string> MaterialOverrideKeys,
        ModelMaterialEffectDefinition DefaultEffect)
    {
        internal ModelMaterialEffectDefinition ResolveEffect(string normalizedSubmeshName)
        {
            if (string.IsNullOrEmpty(normalizedSubmeshName))
            {
                return DefaultEffect;
            }

            if (Effects != null && Effects.TryGetValue(normalizedSubmeshName, out ModelMaterialEffectDefinition effect))
            {
                return effect;
            }

            if (MaterialOverrideKeys != null && !MaterialOverrideKeys.Contains(normalizedSubmeshName))
            {
                return DefaultEffect;
            }

            return ModelMaterialEffectDefinition.None;
        }
    }

    internal sealed record SknMaterialSampler(
        string TextureName,
        string TexturePath);

    internal sealed record SknMaterialDefinition(
        IReadOnlyList<SknMaterialSampler> Samplers,
        IReadOnlyDictionary<string, Vector4> Parameters);

    internal sealed record SknMaterialTextureMetadata(
        string DefaultTexturePath,
        SknMaterialDefinition DefaultMaterial,
        IReadOnlyDictionary<string, IReadOnlyList<string>> OverrideTexturePaths,
        IReadOnlyDictionary<string, SknMaterialDefinition> OverrideMaterials)
    {
        internal IEnumerable<string> ReferencedTexturePaths =>
            OverrideTexturePaths.Values
                .SelectMany(paths => paths)
                .Concat(OverrideMaterials.Values
                    .SelectMany(material => material.Samplers
                        .Where(SknMaterialTextureResolver.IsReferencedSampler)
                        .Select(sampler => sampler.TexturePath)))
                .Concat((DefaultMaterial?.Samplers ?? Array.Empty<SknMaterialSampler>())
                    .Where(SknMaterialTextureResolver.IsReferencedSampler)
                    .Select(sampler => sampler.TexturePath))
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
        private static readonly uint ParamValues = Fnv1a.HashLower("paramValues");
        private static readonly uint ParameterName = Fnv1a.HashLower("name");
        private static readonly uint ParameterValue = Fnv1a.HashLower("value");

        internal static SknMaterialTextureResolution Resolve(
            BinTree binTree,
            IEnumerable<string> availableTextureKeys) =>
            Resolve(ReadMetadata(binTree), availableTextureKeys);

        internal static SknMaterialTextureMetadata ReadMetadata(BinTree binTree)
        {
            var materialDefinitions = BuildMaterialDefinitionMap(binTree);
            var overrideTexturePaths = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            var overrideMaterials = new Dictionary<string, SknMaterialDefinition>(StringComparer.OrdinalIgnoreCase);
            string defaultTexturePath = null;
            SknMaterialDefinition defaultMaterial = null;

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

                if (defaultMaterial == null &&
                    meshProperties.Properties.TryGetValue(Material, out BinTreeProperty materialProperty) &&
                    materialProperty is BinTreeObjectLink defaultMaterialLink &&
                    materialDefinitions.TryGetValue(defaultMaterialLink.Value, out SknMaterialDefinition linkedMaterial))
                {
                    defaultMaterial = linkedMaterial;
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

                    string normalizedSubmesh = NormalizeMaterialKey(submeshName);
                    if (string.IsNullOrEmpty(normalizedSubmesh))
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
                        materialDefinitions.TryGetValue(materialLink.Value, out SknMaterialDefinition materialDefinition))
                    {
                        string materialTexturePath = SelectColorTexturePath(materialDefinition.Samplers);
                        if (!string.IsNullOrWhiteSpace(materialTexturePath))
                        {
                            candidates.Add(materialTexturePath);
                        }

                        if (materialDefinition.Samplers.Count > 0)
                        {
                            overrideMaterials[normalizedSubmesh] = materialDefinition;
                        }
                    }

                    if (candidates.Count > 0)
                    {
                        overrideTexturePaths[normalizedSubmesh] = candidates;
                    }
                }
            }

            return new SknMaterialTextureMetadata(
                defaultTexturePath,
                defaultMaterial,
                overrideTexturePaths,
                overrideMaterials);
        }

        internal static SknMaterialTextureResolution Resolve(
            SknMaterialTextureMetadata metadata,
            IEnumerable<string> availableTextureKeys)
        {
            var textureKeys = availableTextureKeys?.ToList() ?? new List<string>();
            var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var effects = new Dictionary<string, ModelMaterialEffectDefinition>(StringComparer.OrdinalIgnoreCase);
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

            foreach ((string submesh, SknMaterialDefinition material) in metadata.OverrideMaterials)
            {
                ModelMaterialEffectDefinition effect = SknMaterialEffectResolver.Resolve(
                    material,
                    submesh,
                    textureKeys,
                    metadata.OverrideTexturePaths.Keys);
                if (effect.Kind != ModelMaterialEffectKind.None)
                {
                    effects[submesh] = effect;
                }
            }

            ModelMaterialEffectDefinition defaultEffect = metadata.DefaultMaterial == null
                ? ModelMaterialEffectDefinition.None
                : SknMaterialEffectResolver.Resolve(
                    metadata.DefaultMaterial,
                    string.Empty,
                    textureKeys,
                    metadata.OverrideTexturePaths.Keys);

            return new SknMaterialTextureResolution(
                MatchTextureKey(
                    SelectColorTexturePath(metadata.DefaultMaterial?.Samplers),
                    textureKeys) ??
                MatchTextureKey(metadata.DefaultTexturePath, textureKeys),
                overrides,
                effects,
                metadata.OverrideMaterials.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase),
                defaultEffect);
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
            string assetsRoot = characterRoot.Parent?.Parent?.FullName;
            string relativePath = null;
            string candidateRoot = null;
            string characterPrefix = $"assets/characters/{characterRoot.Name}/";
            if (assetPath.StartsWith(characterPrefix, StringComparison.OrdinalIgnoreCase))
            {
                relativePath = assetPath[characterPrefix.Length..];
                candidateRoot = characterRoot.FullName;
            }
            else if (assetPath.StartsWith("assets/shared/", StringComparison.OrdinalIgnoreCase) &&
                     assetsRoot != null &&
                     characterRoot.Parent?.Parent?.Name.Equals("assets", StringComparison.OrdinalIgnoreCase) == true)
            {
                relativePath = assetPath["assets/".Length..];
                candidateRoot = assetsRoot;
            }
            else if (assetPath.StartsWith("assets/characters/shared/", StringComparison.OrdinalIgnoreCase) &&
                     assetsRoot != null &&
                     characterRoot.Parent?.Parent?.Name.Equals("assets", StringComparison.OrdinalIgnoreCase) == true)
            {
                relativePath = assetPath["assets/".Length..];
                candidateRoot = assetsRoot;
            }

            if (relativePath == null || candidateRoot == null)
            {
                return null;
            }

            string candidate = Path.GetFullPath(Path.Combine(
                candidateRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            string extension = Path.GetExtension(candidate);
            string rootedPrefix =
                Path.GetFullPath(candidateRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
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

        private static Dictionary<uint, SknMaterialDefinition> BuildMaterialDefinitionMap(BinTree binTree)
        {
            var result = new Dictionary<uint, SknMaterialDefinition>();
            foreach ((uint pathHash, BinTreeObject obj) in binTree.Objects)
            {
                if (obj.ClassHash != StaticMaterialClass)
                {
                    continue;
                }

                List<SknMaterialSampler> samplers = ReadSamplers(obj);
                Dictionary<string, Vector4> parameters = ReadParameters(obj);
                if (samplers.Count > 0 || parameters.Count > 0)
                {
                    result[pathHash] = new SknMaterialDefinition(samplers, parameters);
                }
            }

            return result;
        }

        private static List<SknMaterialSampler> ReadSamplers(BinTreeObject materialObject)
        {
            var result = new List<SknMaterialSampler>();
            if (!materialObject.Properties.TryGetValue(SamplerValues, out BinTreeProperty property) ||
                property is not BinTreeContainer samplers)
            {
                return result;
            }

            foreach (BinTreeProperty element in samplers.Elements)
            {
                if (element is BinTreeStruct sampler &&
                    TryGetString(sampler, TextureName, out string textureName) &&
                    TryGetString(sampler, TexturePath, out string texturePath))
                {
                    result.Add(new SknMaterialSampler(textureName, texturePath));
                }
            }

            return result;
        }

        private static Dictionary<string, Vector4> ReadParameters(BinTreeObject materialObject)
        {
            var result = new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase);
            if (!materialObject.Properties.TryGetValue(ParamValues, out BinTreeProperty property) ||
                property is not BinTreeContainer parameters)
            {
                return result;
            }

            foreach (BinTreeProperty element in parameters.Elements)
            {
                if (element is not BinTreeStruct parameter ||
                    !TryGetString(parameter, ParameterName, out string name) ||
                    !parameter.Properties.TryGetValue(ParameterValue, out BinTreeProperty value) ||
                    value is not BinTreeVector4 vector)
                {
                    continue;
                }

                result[name] = vector.Value;
            }

            return result;
        }

        private static string SelectColorTexturePath(IReadOnlyList<SknMaterialSampler> samplers) =>
            (samplers ?? Array.Empty<SknMaterialSampler>())
                .Where(sampler => RankColorSampler(sampler.TextureName) > 0)
                .OrderByDescending(sampler => RankColorSampler(sampler.TextureName))
                .Select(sampler => sampler.TexturePath)
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));

        internal static bool IsReferencedSampler(SknMaterialSampler sampler) =>
            sampler != null &&
            !string.IsNullOrWhiteSpace(sampler.TexturePath) &&
            !IsNeutralTexturePath(sampler.TexturePath) &&
            (RankColorSampler(sampler.TextureName) > 0 ||
             IsEffectSampler(sampler.TextureName));

        private static bool IsEffectSampler(string textureName)
        {
            string normalized = NormalizeToken(textureName);
            return normalized is "additivescrolltex" or
                "additivescrollmask" or
                "scrolltex" or
                "scrolltexmask" or
                "scrolltexture" or
                "scrolltexturemask" or
                "scrollmask" or
                "mask" or
                "masktexturered" or
                "masktexturegreen" or
                "masktextureblue" or
                "masktexture" or
                "masktex" or
                "fresnelmask" or
                "bloommask" or
                "patternmask" or
                "emissionmask" or
                "emissivemask" or
                "flowmaptex" or
                "flowmap" or
                "flowmapmask" or
                "flowtexture" or
                "flowmaptexture" or
                "flowmask" or
                "noisedisturb" or
                "noisetexture" or
                "transitionpatterntexture" or
                "transitionstate2" or
                "dissolvetex" or
                "dissolvetexture" or
                "dissolvegradienttexture" or
                "dissolvetexture2" or
                "bloommasktexture" or
                "outlinebloommask" or
                "bloomtexture" or
                "emissiontexture" or
                "emissionrtexture" or
                "emissivetexture" or
                "emissionrdistortiongtexture";
        }

        internal static bool IsNeutralTexturePath(string texturePath) =>
            NormalizeToken(PathUtils.TruncateAtDot(
                Path.GetFileNameWithoutExtension(texturePath ?? string.Empty))) == "black";

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

        internal static string MatchTextureKey(string texturePath, IReadOnlyList<string> availableKeys)
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

        internal static string NormalizeToken(string value) =>
            Regex.Replace(value?.ToLowerInvariant() ?? string.Empty, @"[^a-z0-9]", string.Empty);

        private static string NormalizeAssetPath(string value) =>
            value?.Replace('\\', '/').Trim().ToLowerInvariant() ?? string.Empty;
    }
}
