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
        IReadOnlyDictionary<string, Vector4> Parameters)
    {
        internal IReadOnlySet<string> Switches { get; init; } =
            new HashSet<string>(StringComparer.Ordinal);

        internal uint ShaderHash { get; init; }

        private Dictionary<string, SknMaterialSampler> _normalizedSamplers;

        internal SknMaterialSampler FindSampler(string normalizedToken)
        {
            if (_normalizedSamplers == null)
            {
                var map = new Dictionary<string, SknMaterialSampler>(StringComparer.Ordinal);
                foreach (var sampler in Samplers)
                {
                    string key = SknMaterialTextureResolver.NormalizeToken(sampler.TextureName);
                    map.TryAdd(key, sampler);
                }

                _normalizedSamplers = map;
            }

            return _normalizedSamplers.TryGetValue(normalizedToken, out SknMaterialSampler matchedSampler) ? matchedSampler : null;
        }

        internal bool HasSwitch(params string[] names) =>
            names.Any(name => Switches.Contains(SknMaterialTextureResolver.NormalizeToken(name)));
    }

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
                        .Select(sampler => sampler.TexturePath)))
                .Concat((DefaultMaterial?.Samplers ?? Array.Empty<SknMaterialSampler>())
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
        private static readonly uint SwitchesProperty = Fnv1a.HashLower("switches");
        private static readonly uint SwitchOn = Fnv1a.HashLower("on");
        private static readonly uint ParameterName = Fnv1a.HashLower("name");
        private static readonly uint ParameterValue = Fnv1a.HashLower("value");
        private static readonly uint Techniques = Fnv1a.HashLower("techniques");
        private static readonly uint Passes = Fnv1a.HashLower("passes");
        private static readonly uint Shader = Fnv1a.HashLower("shader");

        internal static SknMaterialTextureResolution Resolve(
            BinTree binTree,
            IEnumerable<string> availableTextureKeys,
            Func<ulong, string> wadChunkPathResolver = null) =>
            Resolve(ReadMetadata(binTree, wadChunkPathResolver), availableTextureKeys);

        internal static SknMaterialTextureMetadata ReadMetadata(
            BinTree binTree,
            Func<ulong, string> wadChunkPathResolver = null)
        {
            return ReadMetadata(new[] { binTree }, wadChunkPathResolver);
        }

        internal static SknMaterialTextureMetadata ReadMetadata(
            IEnumerable<BinTree> binTrees,
            Func<ulong, string> wadChunkPathResolver = null)
        {
            List<BinTree> trees = (binTrees ?? Enumerable.Empty<BinTree>())
                .Where(tree => tree != null)
                .ToList();
            if (trees.Count == 0)
            {
                return new SknMaterialTextureMetadata(
                    null,
                    null,
                    new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, SknMaterialDefinition>(StringComparer.OrdinalIgnoreCase));
            }

            BinTree primaryTree = trees[0];
            var materialDefinitions = BuildMaterialDefinitionMap(trees, wadChunkPathResolver);
            var overrideTexturePaths = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            var overrideMaterials = new Dictionary<string, SknMaterialDefinition>(StringComparer.OrdinalIgnoreCase);
            string defaultTexturePath = null;
            SknMaterialDefinition defaultMaterial = null;

            foreach (BinTreeObject obj in primaryTree.Objects.Values)
            {
                if (obj.ClassHash != SkinPropertiesClass ||
                    !obj.Properties.TryGetValue(SkinMeshProperties, out BinTreeProperty meshProperty) ||
                    meshProperty is not BinTreeStruct meshProperties)
                {
                    continue;
                }

                if (defaultTexturePath == null &&
                    TryGetTexturePath(meshProperties, Texture, wadChunkPathResolver, out string texturePath))
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
                    bool hasAuthoredOverride = false;
                    if (entry.Properties.TryGetValue(Material, out BinTreeProperty linkProperty) &&
                        linkProperty is BinTreeObjectLink materialLink)
                    {
                        hasAuthoredOverride = true;
                        if (materialDefinitions.TryGetValue(
                                materialLink.Value,
                                out SknMaterialDefinition materialDefinition))
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
                    }

                    if (TryGetTexturePath(entry, Texture, wadChunkPathResolver, out string directTexturePath))
                    {
                        hasAuthoredOverride = true;
                        candidates.Add(directTexturePath);
                    }

                    if (hasAuthoredOverride)
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
                if (effect.Kind != ModelMaterialEffectKind.None ||
                    effect.MaterialTint != Vector4.One)
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
                metadata.OverrideTexturePaths.Keys
                    .Concat(metadata.OverrideMaterials.Keys)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
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

        internal static string TryResolveDependencyBinPath(string skinBinPath, string dependencyPath)
        {
            if (string.IsNullOrWhiteSpace(skinBinPath) || string.IsNullOrWhiteSpace(dependencyPath))
            {
                return null;
            }

            string normalizedBinPath = Path.GetFullPath(skinBinPath)
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            string dataMarker = $"{Path.DirectorySeparatorChar}data{Path.DirectorySeparatorChar}";
            int dataIndex = normalizedBinPath.IndexOf(dataMarker, StringComparison.OrdinalIgnoreCase);
            if (dataIndex < 0)
            {
                return null;
            }

            string virtualPath = dependencyPath
                .Replace('\\', '/')
                .TrimStart('/')
                .ToLowerInvariant();
            if (!virtualPath.StartsWith("data/", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string rootPath = normalizedBinPath[..dataIndex];
            string namedPath = Path.Combine(rootPath, virtualPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(namedPath))
            {
                return namedPath;
            }

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
            string characterPrefix = $"assets/characters/{characterRoot.Name}/";

            string candidateRoot = null;
            string relativePath = null;

            if (assetPath.StartsWith(characterPrefix, StringComparison.OrdinalIgnoreCase))
            {
                candidateRoot = characterRoot.FullName;
                relativePath = assetPath[characterPrefix.Length..];
            }
            else if (assetPath.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) &&
                     characterRoot.Parent?.Parent is DirectoryInfo assetsRoot &&
                     assetsRoot.Name.Equals("assets", StringComparison.OrdinalIgnoreCase))
            {
                candidateRoot = assetsRoot.FullName;
                relativePath = assetPath["assets/".Length..];
            }

            if (candidateRoot == null || relativePath == null)
            {
                return null;
            }

            string candidate = Path.GetFullPath(Path.Combine(candidateRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            string extension = Path.GetExtension(candidate);
            string rootedPrefix = Path.GetFullPath(candidateRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            return candidate.StartsWith(rootedPrefix, StringComparison.OrdinalIgnoreCase) &&
                   (extension.Equals(".tex", StringComparison.OrdinalIgnoreCase) || extension.Equals(".dds", StringComparison.OrdinalIgnoreCase)) &&
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

        internal static IReadOnlyList<string> GetSelectableTextureCandidates(
            IEnumerable<string> textureKeys,
            SknMaterialTextureResolution materialTextures = null) =>
            (textureKeys ?? Enumerable.Empty<string>())
                .Concat(materialTextures?.Overrides?.Values ?? Enumerable.Empty<string>())
                .Append(materialTextures?.DefaultTextureKey)
                .Where(key => !string.IsNullOrWhiteSpace(key) && !IsPresentationTexture(key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

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

        private static Dictionary<uint, SknMaterialDefinition> BuildMaterialDefinitionMap(
            IEnumerable<BinTree> binTrees,
            Func<ulong, string> wadChunkPathResolver)
        {
            var result = new Dictionary<uint, SknMaterialDefinition>();
            foreach (BinTree binTree in binTrees ?? Enumerable.Empty<BinTree>())
            {
                if (binTree == null)
                {
                    continue;
                }

                foreach ((uint pathHash, BinTreeObject obj) in binTree.Objects)
                {
                    if (obj.ClassHash != StaticMaterialClass || result.ContainsKey(pathHash))
                    {
                        continue;
                    }

                    List<SknMaterialSampler> samplers = ReadSamplers(obj, wadChunkPathResolver);
                    Dictionary<string, Vector4> parameters = ReadParameters(obj);
                    HashSet<string> switches = ReadSwitches(obj);
                    if (samplers.Count > 0 || parameters.Count > 0 || switches.Count > 0)
                    {
                        result[pathHash] = new SknMaterialDefinition(samplers, parameters)
                        {
                            Switches = switches,
                            ShaderHash = ReadShaderHash(obj.Properties)
                        };
                    }
                }
            }

            return result;
        }

        private static List<SknMaterialSampler> ReadSamplers(
            BinTreeObject materialObject,
            Func<ulong, string> wadChunkPathResolver)
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
                    TryGetTexturePath(sampler, TexturePath, wadChunkPathResolver, out string texturePath))
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

        private static HashSet<string> ReadSwitches(BinTreeObject materialObject)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (!materialObject.Properties.TryGetValue(SwitchesProperty, out BinTreeProperty property) ||
                property is not BinTreeContainer switches)
            {
                return result;
            }

            foreach (BinTreeProperty element in switches.Elements)
            {
                if (element is not BinTreeStruct switchDefinition ||
                    !TryGetString(switchDefinition, ParameterName, out string name) ||
                    (switchDefinition.Properties.TryGetValue(SwitchOn, out BinTreeProperty enabled) &&
                     enabled is BinTreeBool flag && !flag.Value))
                {
                    continue;
                }

                result.Add(NormalizeToken(name));
            }

            return result;
        }

        private static uint ReadShaderHash(IReadOnlyDictionary<uint, BinTreeProperty> properties)
        {
            if (!properties.TryGetValue(Techniques, out BinTreeProperty techniquesProperty) ||
                techniquesProperty is not BinTreeContainer techniques)
            {
                return 0;
            }

            BinTreeStruct technique = techniques.Elements.OfType<BinTreeStruct>().FirstOrDefault();
            if (technique == null ||
                !technique.Properties.TryGetValue(Passes, out BinTreeProperty passesProperty) ||
                passesProperty is not BinTreeContainer passes)
            {
                return 0;
            }

            BinTreeStruct pass = passes.Elements.OfType<BinTreeStruct>().FirstOrDefault();
            return pass != null &&
                   pass.Properties.TryGetValue(Shader, out BinTreeProperty shaderProperty) &&
                   shaderProperty is BinTreeObjectLink shader
                ? shader.Value
                : 0;
        }

        private static string SelectColorTexturePath(IReadOnlyList<SknMaterialSampler> samplers) =>
            (samplers ?? Array.Empty<SknMaterialSampler>())
                .Where(sampler => RankColorSampler(sampler.TextureName) > 0)
                .OrderByDescending(sampler => RankColorSampler(sampler.TextureName))
                .Select(sampler => sampler.TexturePath)
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));

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

        private static bool TryGetTexturePath(
            BinTreeStruct value,
            uint propertyHash,
            Func<ulong, string> wadChunkPathResolver,
            out string result)
        {
            if (!value.Properties.TryGetValue(propertyHash, out BinTreeProperty property))
            {
                result = null;
                return false;
            }

            if (property is BinTreeWadChunkLink link)
            {
                string resolvedPath = wadChunkPathResolver?.Invoke(link.Value);
                result = PathUtils.ToVirtualPath(
                    string.IsNullOrWhiteSpace(resolvedPath)
                        ? $"{link.Value:x16}"
                        : resolvedPath);
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

        internal static bool IsPresentationTexture(string textureKey)
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
