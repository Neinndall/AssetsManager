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
        ModelMaterialDefinition DefaultMaterialDefinition,
        IReadOnlyDictionary<string, ModelMaterialDefinition> MaterialDefinitions)
    {
        internal IReadOnlyList<string> InitialHiddenSubmeshes { get; init; } = Array.Empty<string>();

        internal ModelMaterialDefinition ResolveMaterialDefinition(string normalizedSubmeshName)
        {
            if (!string.IsNullOrEmpty(normalizedSubmeshName) &&
                MaterialDefinitions != null &&
                MaterialDefinitions.TryGetValue(normalizedSubmeshName, out ModelMaterialDefinition material))
            {
                return material;
            }

            return DefaultMaterialDefinition;
        }
    }

    internal sealed record SknMaterialSampler(
        string TextureName,
        string TexturePath,
        ModelMaterialWrapMode WrapU = ModelMaterialWrapMode.Repeat,
        ModelMaterialWrapMode WrapV = ModelMaterialWrapMode.Repeat,
        bool UsesShaderDefaultTexture = false);

    /// <summary>
    /// Raw authored StaticMaterialDef data. Generic material semantics are resolved separately
    /// so shader render state does not compete with optional League-specific effect layers.
    /// </summary>
    internal sealed record SknMaterialDefinition(
        IReadOnlyList<SknMaterialSampler> Samplers,
        IReadOnlyDictionary<string, Vector4> Parameters)
    {
        internal IReadOnlySet<string> Switches { get; init; } =
            new HashSet<string>(StringComparer.Ordinal);

        internal IReadOnlyDictionary<string, bool> SwitchStates { get; init; } =
            new Dictionary<string, bool>(StringComparer.Ordinal);

        internal IReadOnlyDictionary<string, string> ShaderMacros { get; init; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        internal SknMaterialPassDefinition Pass { get; init; }
        internal bool IsAnimated { get; init; }
        internal uint ShaderHash { get; init; }
        internal string ShaderPath { get; init; }

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
            names.Any(name => SwitchStates.TryGetValue(name, out bool enabled)
                ? enabled
                : Switches.Contains(SknMaterialTextureResolver.NormalizeToken(name)));
    }

    internal sealed record SknMaterialPassDefinition(
        uint ShaderHash,
        IReadOnlyDictionary<string, Vector4> Parameters,
        IReadOnlyDictionary<string, string> ShaderMacros,
        bool? BlendEnabled,
        uint? DestinationColorBlendFactor,
        bool? CullEnabled,
        uint? WindingToCull,
        bool? DepthEnabled,
        uint? WriteMask);

    internal sealed record SknShaderDefinition(
        string Path,
        IReadOnlyList<SknMaterialSampler> DefaultSamplers,
        IReadOnlyDictionary<string, Vector4> DefaultParameters,
        IReadOnlyDictionary<string, bool> DefaultSwitches,
        IReadOnlyDictionary<string, string> FeatureDefines);

    internal sealed record SknMaterialTextureMetadata(
        string DefaultTexturePath,
        SknMaterialDefinition DefaultMaterial,
        IReadOnlyDictionary<string, SknMaterialDefinition> OverrideMaterials)
    {
        internal IReadOnlyList<string> InitialHiddenSubmeshes { get; init; } = Array.Empty<string>();
        internal IReadOnlyDictionary<uint, SknShaderDefinition> ShaderDefinitions { get; init; } =
            new Dictionary<uint, SknShaderDefinition>();
        internal bool HasDefaultMaterialLink { get; init; }
        internal IReadOnlySet<string> OverrideMaterialLinkKeys { get; init; } =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal IReadOnlyDictionary<string, string> DirectOverrideTexturePaths { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal IEnumerable<string> ReferencedTexturePaths =>
            DirectOverrideTexturePaths.Values
                .Concat(OverrideMaterials.Values
                    .SelectMany(material => material.Samplers
                        .Select(sampler => sampler.TexturePath)))
                .Concat((DefaultMaterial?.Samplers ?? Array.Empty<SknMaterialSampler>())
                    .Select(sampler => sampler.TexturePath))
                .Concat(ReferencedShaderDefaultTextures())
                .Prepend(DefaultTexturePath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase);

        private IEnumerable<string> ReferencedShaderDefaultTextures()
        {
            if (ShaderDefinitions == null || ShaderDefinitions.Count == 0)
                return Enumerable.Empty<string>();

            return OverrideMaterials.Values
                .Append(DefaultMaterial)
                .Where(material => material != null && material.ShaderHash != 0)
                .Select(material => material.ShaderHash)
                .Distinct()
                .Where(ShaderDefinitions.ContainsKey)
                .SelectMany(hash => ShaderDefinitions[hash].DefaultSamplers)
                .Select(sampler => sampler.TexturePath);
        }
    }

    internal static class SknMaterialTextureResolver
    {
        private static readonly uint SkinPropertiesClass = Fnv1a.HashLower("SkinCharacterDataProperties");
        private static readonly uint StaticMaterialClass = Fnv1a.HashLower("StaticMaterialDef");
        private static readonly uint CustomShaderClass = Fnv1a.HashLower("CustomShaderDef");
        private static readonly uint SkinMeshProperties = Fnv1a.HashLower("skinMeshProperties");
        private static readonly uint SimpleSkin = Fnv1a.HashLower("simpleSkin");
        private static readonly uint InitialSubmeshToHide = Fnv1a.HashLower("initialSubmeshToHide");
        private static readonly uint MaterialOverride = Fnv1a.HashLower("materialOverride");
        private static readonly uint Texture = Fnv1a.HashLower("texture");
        private static readonly uint Submesh = Fnv1a.HashLower("submesh");
        private static readonly uint Material = Fnv1a.HashLower("Material");
        private static readonly uint SamplerValues = Fnv1a.HashLower("samplerValues");
        private static readonly uint TextureName = Fnv1a.HashLower("textureName");
        private static readonly uint TexturePath = Fnv1a.HashLower("texturePath");
        private static readonly uint AddressU = Fnv1a.HashLower("addressU");
        private static readonly uint AddressV = Fnv1a.HashLower("addressV");
        private static readonly uint ParamValues = Fnv1a.HashLower("paramValues");
        private static readonly uint SwitchesProperty = Fnv1a.HashLower("switches");
        private static readonly uint SwitchOn = Fnv1a.HashLower("on");
        private static readonly uint ShaderMacros = Fnv1a.HashLower("shaderMacros");
        private static readonly uint DynamicMaterial = Fnv1a.HashLower("dynamicMaterial");
        private static readonly uint ParameterName = Fnv1a.HashLower("name");
        private static readonly uint ParameterValue = Fnv1a.HashLower("value");
        private static readonly uint Techniques = Fnv1a.HashLower("techniques");
        private static readonly uint Passes = Fnv1a.HashLower("passes");
        private static readonly uint Shader = Fnv1a.HashLower("shader");
        private static readonly uint BlendEnable = Fnv1a.HashLower("blendEnable");
        private static readonly uint DestinationColorBlendFactor = Fnv1a.HashLower("dstColorBlendFactor");
        private static readonly uint CullEnable = Fnv1a.HashLower("cullEnable");
        private static readonly uint WindingToCull = Fnv1a.HashLower("windingToCull");
        private static readonly uint DepthEnable = Fnv1a.HashLower("depthEnable");
        private static readonly uint WriteMask = Fnv1a.HashLower("writeMask");
        private static readonly uint ObjectPath = Fnv1a.HashLower("objectPath");
        private static readonly uint ShaderTextures = Fnv1a.HashLower("textures");
        private static readonly uint ShaderParameters = Fnv1a.HashLower("parameters");
        private static readonly uint StaticSwitches = Fnv1a.HashLower("staticSwitches");
        private static readonly uint FeatureDefines = Fnv1a.HashLower("featureDefines");
        private static readonly uint DefaultTexturePath = Fnv1a.HashLower("defaultTexturePath");
        private static readonly uint ParameterData = Fnv1a.HashLower("data");
        private static readonly uint LogicalParameters = Fnv1a.HashLower("logicalParameters");
        private static readonly uint OnByDefault = Fnv1a.HashLower("onByDefault");

        internal static SknMaterialTextureResolution Resolve(
            BinTree binTree,
            IEnumerable<string> availableTextureKeys,
            Func<ulong, string> wadChunkPathResolver = null,
            Func<uint, string> binEntryResolver = null) =>
            Resolve(ReadMetadata(binTree, wadChunkPathResolver, binEntryResolver), availableTextureKeys);

        internal static SknMaterialTextureMetadata ReadMetadata(
            BinTree binTree,
            Func<ulong, string> wadChunkPathResolver = null,
            Func<uint, string> binEntryResolver = null)
        {
            return ReadMetadata(new[] { binTree }, wadChunkPathResolver, binEntryResolver);
        }

        internal static SknMaterialTextureMetadata ReadMetadata(
            IEnumerable<BinTree> binTrees,
            Func<ulong, string> wadChunkPathResolver = null,
            Func<uint, string> binEntryResolver = null)
        {
            List<BinTree> trees = (binTrees ?? Enumerable.Empty<BinTree>())
                .Where(tree => tree != null)
                .ToList();
            if (trees.Count == 0)
            {
                return new SknMaterialTextureMetadata(
                    null,
                    null,
                    new Dictionary<string, SknMaterialDefinition>(StringComparer.OrdinalIgnoreCase));
            }

            BinTree primaryTree = trees[0];
            var materialDefinitions = BuildMaterialDefinitionMap(trees, wadChunkPathResolver, binEntryResolver);
            var shaderDefinitions = new Dictionary<uint, SknShaderDefinition>();
            foreach (BinTree tree in trees)
            {
                foreach ((uint shaderHash, SknShaderDefinition shader) in
                         ReadShaderDefinitions(tree, wadChunkPathResolver, binEntryResolver))
                {
                    shaderDefinitions.TryAdd(shaderHash, shader);
                }
            }

            var overrideMaterials = new Dictionary<string, SknMaterialDefinition>(StringComparer.OrdinalIgnoreCase);
            var overrideMaterialLinkKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var directOverrideTexturePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<string> initialHiddenSubmeshes = Array.Empty<string>();
            bool readInitialHiddenSubmeshes = false;
            bool hasDefaultMaterialLink = false;
            string defaultTexturePath = null;
            SknMaterialDefinition defaultMaterial = null;

            foreach (BinTree tree in trees)
            {
                foreach (BinTreeObject obj in tree.Objects.Values)
                {
                    if (obj.ClassHash != SkinPropertiesClass ||
                        !obj.Properties.TryGetValue(SkinMeshProperties, out BinTreeProperty meshProperty) ||
                        meshProperty is not BinTreeStruct meshProperties)
                    {
                        continue;
                    }

                    if (!readInitialHiddenSubmeshes && ReferenceEquals(tree, primaryTree))
                    {
                        readInitialHiddenSubmeshes = true;
                        if (TryGetString(meshProperties, InitialSubmeshToHide, out string hiddenSubmeshes))
                            initialHiddenSubmeshes = SplitSubmeshNames(hiddenSubmeshes);
                    }

                    if (defaultTexturePath == null &&
                        TryGetTexturePath(meshProperties, Texture, wadChunkPathResolver, out string texturePath))
                    {
                        defaultTexturePath = texturePath;
                    }

                    if (meshProperties.Properties.TryGetValue(Material, out BinTreeProperty materialProperty) &&
                        materialProperty is BinTreeObjectLink defaultMaterialLink)
                    {
                        hasDefaultMaterialLink = true;
                        if (defaultMaterial == null &&
                            materialDefinitions.TryGetValue(defaultMaterialLink.Value, out SknMaterialDefinition linkedMaterial))
                        {
                            defaultMaterial = linkedMaterial;
                        }
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

                        if (entry.Properties.TryGetValue(Material, out BinTreeProperty linkProperty) &&
                            linkProperty is BinTreeObjectLink materialLink)
                        {
                            overrideMaterialLinkKeys.Add(normalizedSubmesh);
                            if (materialDefinitions.TryGetValue(
                                    materialLink.Value,
                                    out SknMaterialDefinition materialDefinition) &&
                                (materialDefinition.Samplers.Count > 0 ||
                                 materialDefinition.Parameters.Count > 0 ||
                                 materialDefinition.Switches.Count > 0 ||
                                 materialDefinition.Pass != null ||
                                 materialDefinition.IsAnimated))
                            {
                                overrideMaterials[normalizedSubmesh] = materialDefinition;
                            }
                        }

                        if (TryGetTexturePath(entry, Texture, wadChunkPathResolver, out string directTexturePath))
                        {
                            directOverrideTexturePaths[normalizedSubmesh] = directTexturePath;
                        }
                    }
                }
            }

            return new SknMaterialTextureMetadata(
                defaultTexturePath,
                defaultMaterial,
                overrideMaterials)
            {
                InitialHiddenSubmeshes = initialHiddenSubmeshes,
                ShaderDefinitions = shaderDefinitions,
                HasDefaultMaterialLink = hasDefaultMaterialLink,
                OverrideMaterialLinkKeys = overrideMaterialLinkKeys,
                DirectOverrideTexturePaths = directOverrideTexturePaths
            };
        }

        internal static SknMaterialTextureResolution Resolve(
            SknMaterialTextureMetadata metadata,
            IEnumerable<string> availableTextureKeys)
        {
            var textureKeys = availableTextureKeys?.ToList() ?? new List<string>();
            string skinTextureKey =
                MatchTextureKey(metadata.DefaultTexturePath, textureKeys) ??
                FindBaseDiffuseTextureKey(textureKeys);

            var directOverrideTextureKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach ((string submesh, string texturePath) in metadata.DirectOverrideTexturePaths)
            {
                string textureKey = MatchTextureKey(texturePath, textureKeys);
                if (textureKey != null)
                    directOverrideTextureKeys[submesh] = textureKey;
            }

            IReadOnlySet<string> overrideSubmeshKeys = metadata.OverrideMaterialLinkKeys
                .Concat(metadata.DirectOverrideTexturePaths.Keys)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            ModelMaterialEffectDefinition defaultEffect = metadata.DefaultMaterial == null
                ? ModelMaterialEffectDefinition.None
                : SknMaterialEffectResolver.Resolve(
                    metadata.DefaultMaterial,
                    string.Empty,
                    textureKeys,
                    overrideSubmeshKeys);

            ModelMaterialDefinition defaultMaterialDefinition;
            if (metadata.HasDefaultMaterialLink)
            {
                defaultMaterialDefinition = metadata.DefaultMaterial == null
                    ? ModelMaterialDefinition.Missing
                    : SknStaticMaterialResolver.Resolve(
                        metadata.DefaultMaterial,
                        ResolveShaderDefinition(metadata, metadata.DefaultMaterial),
                        textureKeys,
                        skinTextureKey,
                        defaultEffect);
            }
            else
            {
                defaultMaterialDefinition = ModelMaterialDefinition.TextureOnly(skinTextureKey);
            }

            var materialDefinitions = new Dictionary<string, ModelMaterialDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (string submesh in overrideSubmeshKeys)
            {
                bool hasMaterialLink = metadata.OverrideMaterialLinkKeys.Contains(submesh);
                metadata.OverrideMaterials.TryGetValue(submesh, out SknMaterialDefinition material);
                directOverrideTextureKeys.TryGetValue(submesh, out string directTextureKey);
                string textureFallback = directTextureKey ?? skinTextureKey;

                if (hasMaterialLink)
                {
                    if (material == null)
                    {
                        // A linked material wins over textures even when the linked object is absent.
                        materialDefinitions[submesh] = ModelMaterialDefinition.Missing;
                        continue;
                    }

                    ModelMaterialEffectDefinition effect = SknMaterialEffectResolver.Resolve(
                        material,
                        submesh,
                        textureKeys,
                        overrideSubmeshKeys);
                    materialDefinitions[submesh] = SknStaticMaterialResolver.Resolve(
                        material,
                        ResolveShaderDefinition(metadata, material),
                        textureKeys,
                        textureFallback,
                        effect);
                    continue;
                }

                // A texture-only override falls back to the skin texture if its asset is unavailable.
                materialDefinitions[submesh] = ModelMaterialDefinition.TextureOnly(textureFallback);
            }

            return new SknMaterialTextureResolution(
                defaultMaterialDefinition,
                materialDefinitions)
            {
                InitialHiddenSubmeshes = metadata.InitialHiddenSubmeshes ?? Array.Empty<string>()
            };
        }

        private static SknShaderDefinition ResolveShaderDefinition(
            SknMaterialTextureMetadata metadata,
            SknMaterialDefinition material)
        {
            if (material == null || material.ShaderHash == 0 || metadata?.ShaderDefinitions == null)
            {
                return null;
            }

            return metadata.ShaderDefinitions.TryGetValue(material.ShaderHash, out SknShaderDefinition shader)
                ? shader
                : null;
        }

        internal static string TryResolveShaderBinPath(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                return null;
            }

            string normalizedPath = Path.GetFullPath(assetPath)
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            string assetsMarker = $"{Path.DirectorySeparatorChar}assets{Path.DirectorySeparatorChar}";
            string dataMarker = $"{Path.DirectorySeparatorChar}data{Path.DirectorySeparatorChar}";
            int markerIndex = normalizedPath.IndexOf(assetsMarker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                markerIndex = normalizedPath.IndexOf(dataMarker, StringComparison.OrdinalIgnoreCase);
            }

            if (markerIndex >= 0)
            {
                string rootPath = normalizedPath[..markerIndex];
                string namedPath = Path.Combine(rootPath, "data", "shaders", "shaders.bin");
                if (File.Exists(namedPath))
                {
                    return namedPath;
                }

                string hashedPath = Path.Combine(
                    rootPath,
                    $"{XxHash64Ext.Hash("data/shaders/shaders.bin"):x16}.bin");
                if (File.Exists(hashedPath))
                {
                    return hashedPath;
                }
            }

            return null;
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
            if (dataIndex >= 0)
            {
                string virtualPath = PathUtils.NormalizeSeparators(dependencyPath)
                    .TrimStart('/')
                    .ToLowerInvariant();
                if (virtualPath.StartsWith("data/", StringComparison.OrdinalIgnoreCase))
                {
                    string rootPath = normalizedBinPath[..dataIndex];
                    string namedPath = Path.Combine(rootPath, virtualPath.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(namedPath))
                    {
                        return namedPath;
                    }

                    string hashedPath = Path.Combine(rootPath, $"{XxHash64Ext.Hash(virtualPath):x16}.bin");
                    if (File.Exists(hashedPath))
                    {
                        return hashedPath;
                    }
                }
            }

            // Fallback for extracted folders without \data\ directory structure
            string cleanDep = PathUtils.NormalizeSeparators(dependencyPath).TrimStart('/');
            if (cleanDep.StartsWith("data/", StringComparison.OrdinalIgnoreCase))
            {
                cleanDep = cleanDep["data/".Length..];
            }
            if (!cleanDep.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
            {
                cleanDep += ".bin";
            }

            string depFileName = Path.GetFileName(cleanDep);
            for (DirectoryInfo dir = Directory.GetParent(normalizedBinPath);
                 dir != null;
                 dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, cleanDep.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                string fileCandidate = Path.Combine(dir.FullName, depFileName);
                if (File.Exists(fileCandidate))
                {
                    return fileCandidate;
                }

                string animCandidate = Path.Combine(dir.FullName, "animations", depFileName);
                if (File.Exists(animCandidate))
                {
                    return animCandidate;
                }
            }

            return null;
        }

        internal static string TryResolveTexturePath(string sknPath, string assetTexturePath)
        {
            DirectoryInfo characterRoot = FindCharacterRoot(sknPath);
            if (characterRoot == null || string.IsNullOrWhiteSpace(assetTexturePath))
            {
                return null;
            }

            string assetPath = PathUtils.NormalizeSeparators(assetTexturePath).TrimStart('/');
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

            if (candidateRoot != null && relativePath != null)
            {
                string candidate = Path.GetFullPath(Path.Combine(candidateRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
                string rootedPrefix = Path.GetFullPath(candidateRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

                if (candidate.StartsWith(rootedPrefix, StringComparison.OrdinalIgnoreCase) &&
                    candidate.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(candidate))
                {
                    return candidate;
                }
            }

            string fileName = Path.GetFileName(assetPath);
            if (!string.IsNullOrEmpty(fileName))
            {
                string targetFile = fileName.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)
                    ? fileName
                    : fileName + ".tex";

                string skinDir = Path.GetDirectoryName(Path.GetFullPath(sknPath));
                if (!string.IsNullOrEmpty(skinDir))
                {
                    string candidate = Path.Combine(skinDir, targetFile);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }

                string characterCandidate = Path.Combine(characterRoot.FullName, targetFile);
                if (File.Exists(characterCandidate))
                {
                    return characterCandidate;
                }

                if (characterRoot.Parent?.Parent is DirectoryInfo assetsDir &&
                    assetsDir.Name.Equals("assets", StringComparison.OrdinalIgnoreCase) &&
                    assetsDir.Parent != null)
                {
                    string wadRootCandidate = Path.Combine(assetsDir.Parent.FullName, targetFile);
                    if (File.Exists(wadRootCandidate))
                    {
                        return wadRootCandidate;
                    }
                }
            }

            return null;
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

            string themeName = null;
            string tierName = null;
            if (themesIndex >= 0)
            {
                string afterThemes = normalizedSknPath[(themesIndex + themesMarker.Length)..];
                string[] themeParts = afterThemes.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
                if (themeParts.Length >= 1)
                {
                    themeName = themeParts[0];
                }
                if (themeParts.Length >= 2)
                {
                    tierName = themeParts[1];
                }
            }

            var candidateRoots = new List<string>();
            if (themesIndex >= 0)
            {
                string charRoot = normalizedSknPath[..themesIndex];
                candidateRoots.Add(charRoot);
                string dataRoot = charRoot.Replace(
                    $"{Path.DirectorySeparatorChar}assets{Path.DirectorySeparatorChar}",
                    $"{Path.DirectorySeparatorChar}data{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase);
                if (!dataRoot.Equals(charRoot, StringComparison.OrdinalIgnoreCase))
                {
                    candidateRoots.Add(dataRoot);
                }
            }

            string fallbackThemeBin = null;

            foreach (string root in candidateRoots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                string skinsDirectory = Path.Combine(root, "skins");
                if (Directory.Exists(skinsDirectory))
                {
                    foreach (string binPath in Directory.EnumerateFiles(
                                 skinsDirectory,
                                 "skin*.bin",
                                 SearchOption.TopDirectoryOnly)
                             .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
                    {
                        try
                        {
                            using var stream = File.OpenRead(binPath);
                            var binTree = new BinTree(stream);
                            if (ReferencesModel(binTree, normalizedSknPath))
                            {
                                return binPath;
                            }
                        }
                        catch
                        {
                            // Ignore corrupted bin candidate
                        }
                    }
                }

                if (!string.IsNullOrEmpty(themeName))
                {
                    string themeDir = Path.Combine(root, "themes", themeName);
                    if (Directory.Exists(themeDir))
                    {
                        var candidateBins = new List<string>();
                        if (!string.IsNullOrEmpty(tierName))
                        {
                            candidateBins.Add(Path.Combine(themeDir, $"{tierName}.bin"));
                        }
                        candidateBins.Add(Path.Combine(themeDir, "root.bin"));

                        foreach (string binPath in candidateBins)
                        {
                            if (File.Exists(binPath))
                            {
                                try
                                {
                                    using var stream = File.OpenRead(binPath);
                                    var binTree = new BinTree(stream);
                                    if (ReferencesModel(binTree, normalizedSknPath))
                                    {
                                        fallbackThemeBin ??= binPath;
                                    }
                                }
                                catch
                                {
                                    // Ignore
                                }
                            }
                        }
                    }
                }
            }

            return fallbackThemeBin;
        }

        private static bool ReferencesModel(BinTree binTree, string sknPath)
        {
            string sknFileName = Path.GetFileName(sknPath);
            string normalizedSkn = NormalizeAssetPath(sknPath);

            foreach (BinTreeObject obj in binTree.Objects.Values)
            {
                if (obj.ClassHash != SkinPropertiesClass ||
                    !obj.Properties.TryGetValue(SkinMeshProperties, out BinTreeProperty meshProperty) ||
                    meshProperty is not BinTreeStruct meshProperties ||
                    !meshProperties.Properties.TryGetValue(SimpleSkin, out BinTreeProperty simpleSkinProperty))
                {
                    continue;
                }

                if (simpleSkinProperty is BinTreeString simpleSkin)
                {
                    string declaredSkin = simpleSkin.Value;
                    if (string.IsNullOrWhiteSpace(declaredSkin))
                    {
                        continue;
                    }

                    if (Path.GetFileName(declaredSkin).Equals(sknFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    string normalizedDeclared = NormalizeAssetPath(declaredSkin);
                    if (normalizedDeclared.Equals(normalizedSkn, StringComparison.OrdinalIgnoreCase) ||
                        normalizedSkn.EndsWith(normalizedDeclared, StringComparison.OrdinalIgnoreCase) ||
                        normalizedDeclared.EndsWith(normalizedSkn, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                else if (simpleSkinProperty is BinTreeWadChunkLink chunkLink)
                {
                    if (XxHash64Ext.Hash(normalizedSkn) == chunkLink.Value)
                    {
                        return true;
                    }

                    int assetsIdx = normalizedSkn.IndexOf("assets/", StringComparison.OrdinalIgnoreCase);
                    if (assetsIdx >= 0 && XxHash64Ext.Hash(normalizedSkn.AsSpan(assetsIdx)) == chunkLink.Value)
                    {
                        return true;
                    }
                }
                else if (simpleSkinProperty is BinTreeU64 u64)
                {
                    if (XxHash64Ext.Hash(normalizedSkn) == u64.Value)
                    {
                        return true;
                    }

                    int assetsIdx = normalizedSkn.IndexOf("assets/", StringComparison.OrdinalIgnoreCase);
                    if (assetsIdx >= 0 && XxHash64Ext.Hash(normalizedSkn.AsSpan(assetsIdx)) == u64.Value)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        internal static IReadOnlyList<string> GetSelectableTextureCandidates(
            IEnumerable<string> textureKeys,
            SknMaterialTextureResolution materialTextures = null)
        {
            IEnumerable<string> materialTextureKeys = materialTextures == null
                ? Enumerable.Empty<string>()
                : materialTextures.MaterialDefinitions.Values
                    .Select(material => material?.BaseTextureName)
                    .Append(materialTextures.DefaultMaterialDefinition?.BaseTextureName);

            return (textureKeys ?? Enumerable.Empty<string>())
                .Concat(materialTextureKeys)
                .Where(key => !string.IsNullOrWhiteSpace(key) && !IsPresentationTexture(key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
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

        internal static IReadOnlyDictionary<uint, SknShaderDefinition> ReadShaderDefinitions(
            BinTree shaderTree,
            Func<ulong, string> wadChunkPathResolver = null,
            Func<uint, string> binEntryResolver = null)
        {
            var result = new Dictionary<uint, SknShaderDefinition>();
            if (shaderTree == null)
                return result;

            foreach ((uint pathHash, BinTreeObject obj) in shaderTree.Objects)
            {
                if (obj.ClassHash != CustomShaderClass)
                    continue;

                string shaderPath = TryGetString(obj.Properties, ObjectPath, out string objectPath)
                    ? objectPath
                    : ResolveBinEntryName(pathHash, binEntryResolver);

                var defaultSamplers = new List<SknMaterialSampler>();
                if (obj.Properties.TryGetValue(ShaderTextures, out BinTreeProperty textureProperty) &&
                    textureProperty is BinTreeContainer textures)
                {
                    foreach (BinTreeStruct texture in textures.Elements.OfType<BinTreeStruct>())
                    {
                        if (!TryGetString(texture, ParameterName, out string name))
                            continue;

                        TryGetTexturePath(
                            texture.Properties,
                            DefaultTexturePath,
                            wadChunkPathResolver,
                            out string texturePath);
                        defaultSamplers.Add(new SknMaterialSampler(name, texturePath));
                    }
                }

                var defaultParameters = new Dictionary<string, Vector4>(StringComparer.Ordinal);
                if (obj.Properties.TryGetValue(ShaderParameters, out BinTreeProperty parameterProperty) &&
                    parameterProperty is BinTreeContainer parameters)
                {
                    foreach (BinTreeStruct parameter in parameters.Elements.OfType<BinTreeStruct>())
                    {
                        Vector4 data = parameter.Properties.TryGetValue(ParameterData, out BinTreeProperty dataValue) &&
                                       dataValue is BinTreeVector4 vector
                            ? vector.Value
                            : Vector4.Zero;

                        if (parameter.Properties.TryGetValue(LogicalParameters, out BinTreeProperty logicalProperty) &&
                            logicalProperty is BinTreeContainer logicalParameters)
                        {
                            foreach (BinTreeStruct logical in logicalParameters.Elements.OfType<BinTreeStruct>())
                            {
                                if (TryGetString(logical, ParameterName, out string logicalName))
                                    defaultParameters[logicalName] = data;
                            }
                        }

                        if (TryGetString(parameter, ParameterName, out string physicalName))
                            defaultParameters[physicalName] = data;
                    }
                }

                var defaultSwitches = new Dictionary<string, bool>(StringComparer.Ordinal);
                if (obj.Properties.TryGetValue(StaticSwitches, out BinTreeProperty switchProperty) &&
                    switchProperty is BinTreeContainer switches)
                {
                    foreach (BinTreeStruct switchDefinition in switches.Elements.OfType<BinTreeStruct>())
                    {
                        if (!TryGetString(switchDefinition, ParameterName, out string name))
                            continue;

                        defaultSwitches[name] = switchDefinition.Properties.TryGetValue(OnByDefault, out BinTreeProperty onByDefault)
                            ? ReadBool(onByDefault, false)
                            : false;
                    }
                }

                result[pathHash] = new SknShaderDefinition(
                    shaderPath,
                    defaultSamplers,
                    defaultParameters,
                    defaultSwitches,
                    ReadStringMap(obj.Properties, FeatureDefines));
            }

            return result;
        }

        private static Dictionary<uint, SknMaterialDefinition> BuildMaterialDefinitionMap(
            IEnumerable<BinTree> binTrees,
            Func<ulong, string> wadChunkPathResolver,
            Func<uint, string> binEntryResolver)
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
                    Dictionary<string, Vector4> parameters = ReadParameters(obj.Properties);
                    Dictionary<string, bool> switchStates = ReadSwitchStates(obj.Properties);
                    HashSet<string> switches = switchStates
                        .Where(pair => pair.Value)
                        .Select(pair => NormalizeToken(pair.Key))
                        .ToHashSet(StringComparer.Ordinal);
                    Dictionary<string, string> shaderMacros = ReadStringMap(obj.Properties, ShaderMacros);
                    SknMaterialPassDefinition pass = ReadMaterialPass(obj.Properties);
                    if (samplers.Count > 0 ||
                        parameters.Count > 0 ||
                        switchStates.Count > 0 ||
                        pass != null ||
                        obj.Properties.ContainsKey(DynamicMaterial))
                    {
                        result[pathHash] = new SknMaterialDefinition(samplers, parameters)
                        {
                            Switches = switches,
                            SwitchStates = switchStates,
                            ShaderMacros = shaderMacros,
                            Pass = pass,
                            IsAnimated = obj.Properties.TryGetValue(DynamicMaterial, out BinTreeProperty dynamicValue) &&
                                         dynamicValue is BinTreeStruct,
                            ShaderHash = pass?.ShaderHash ?? 0,
                            ShaderPath = ResolveBinEntryName(pass?.ShaderHash ?? 0, binEntryResolver)
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
                if (element is not BinTreeStruct sampler ||
                    !TryGetString(sampler, TextureName, out string textureName))
                {
                    continue;
                }

                string texturePath = null;
                bool usesShaderDefaultTexture = false;
                if (sampler.Properties.TryGetValue(TexturePath, out BinTreeProperty textureProperty))
                {
                    // League treats a non-empty string texturePath as invalid authored data and
                    // falls back to the matching shader default while preserving this sampler's wrap.
                    if (textureProperty is BinTreeString text && !string.IsNullOrEmpty(text.Value))
                    {
                        usesShaderDefaultTexture = true;
                    }
                    else
                    {
                        TryGetTexturePath(sampler, TexturePath, wadChunkPathResolver, out texturePath);
                    }
                }

                result.Add(new SknMaterialSampler(
                    textureName,
                    texturePath,
                    ReadWrap(sampler.Properties, AddressU),
                    ReadWrap(sampler.Properties, AddressV),
                    usesShaderDefaultTexture));
            }

            return result;
        }

        private static Dictionary<string, Vector4> ReadParameters(
            IReadOnlyDictionary<uint, BinTreeProperty> properties)
        {
            var result = new Dictionary<string, Vector4>(StringComparer.Ordinal);
            if (!properties.TryGetValue(ParamValues, out BinTreeProperty property) ||
                property is not BinTreeContainer parameters)
            {
                return result;
            }

            foreach (BinTreeProperty element in parameters.Elements)
            {
                if (element is not BinTreeStruct parameter ||
                    !TryGetString(parameter, ParameterName, out string name))
                {
                    continue;
                }

                // League writes an omitted parameter value as zeros rather than falling back to the shader default.
                result[name] = parameter.Properties.TryGetValue(ParameterValue, out BinTreeProperty value) &&
                               value is BinTreeVector4 vector
                    ? vector.Value
                    : Vector4.Zero;
            }

            return result;
        }

        private static Dictionary<string, bool> ReadSwitchStates(
            IReadOnlyDictionary<uint, BinTreeProperty> properties)
        {
            var result = new Dictionary<string, bool>(StringComparer.Ordinal);
            if (!properties.TryGetValue(SwitchesProperty, out BinTreeProperty property) ||
                property is not BinTreeContainer switches)
            {
                return result;
            }

            foreach (BinTreeProperty element in switches.Elements)
            {
                if (element is not BinTreeStruct switchDefinition ||
                    !TryGetString(switchDefinition, ParameterName, out string name))
                {
                    continue;
                }

                // An authored switch with no `on` field is enabled by the BIN class default.
                result[name] = !switchDefinition.Properties.TryGetValue(SwitchOn, out BinTreeProperty enabled) ||
                               ReadBool(enabled, true);
            }

            return result;
        }

        private static SknMaterialPassDefinition ReadMaterialPass(
            IReadOnlyDictionary<uint, BinTreeProperty> properties)
        {
            if (!properties.TryGetValue(Techniques, out BinTreeProperty techniquesProperty) ||
                techniquesProperty is not BinTreeContainer techniques)
            {
                return null;
            }

            BinTreeStruct technique = techniques.Elements
                .OfType<BinTreeStruct>()
                .FirstOrDefault(candidate =>
                    TryGetString(candidate, ParameterName, out string name) &&
                    name.Equals("normal", StringComparison.Ordinal)) ??
                techniques.Elements.OfType<BinTreeStruct>().FirstOrDefault();
            if (technique == null ||
                !technique.Properties.TryGetValue(Passes, out BinTreeProperty passesProperty) ||
                passesProperty is not BinTreeContainer passes)
            {
                return null;
            }

            BinTreeStruct pass = passes.Elements.OfType<BinTreeStruct>().FirstOrDefault();
            if (pass == null)
            {
                return null;
            }

            uint shaderHash = pass.Properties.TryGetValue(Shader, out BinTreeProperty shaderProperty) &&
                              shaderProperty is BinTreeObjectLink shader
                ? shader.Value
                : 0;
            return new SknMaterialPassDefinition(
                shaderHash,
                ReadParameters(pass.Properties),
                ReadStringMap(pass.Properties, ShaderMacros),
                ReadOptionalBool(pass.Properties, BlendEnable),
                ReadOptionalUInt(pass.Properties, DestinationColorBlendFactor),
                ReadOptionalBool(pass.Properties, CullEnable),
                ReadOptionalUInt(pass.Properties, WindingToCull),
                ReadOptionalBool(pass.Properties, DepthEnable),
                ReadOptionalUInt(pass.Properties, WriteMask));
        }

        private static Dictionary<string, string> ReadStringMap(
            IReadOnlyDictionary<uint, BinTreeProperty> properties,
            uint propertyHash)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!properties.TryGetValue(propertyHash, out BinTreeProperty property) ||
                property is not BinTreeMap map)
            {
                return result;
            }

            foreach ((BinTreeProperty key, BinTreeProperty value) in map)
            {
                if (key is BinTreeString keyString && value is BinTreeString valueString)
                    result[keyString.Value] = valueString.Value;
            }
            return result;
        }

        private static ModelMaterialWrapMode ReadWrap(
            IReadOnlyDictionary<uint, BinTreeProperty> properties,
            uint propertyHash)
        {
            uint value = ReadOptionalUInt(properties, propertyHash) ?? 0;
            return value switch
            {
                1 => ModelMaterialWrapMode.Clamp,
                2 => ModelMaterialWrapMode.Mirror,
                3 => ModelMaterialWrapMode.Border,
                _ => ModelMaterialWrapMode.Repeat
            };
        }

        private static bool? ReadOptionalBool(
            IReadOnlyDictionary<uint, BinTreeProperty> properties,
            uint propertyHash) =>
            properties.TryGetValue(propertyHash, out BinTreeProperty value)
                ? ReadBool(value, false)
                : null;

        private static bool ReadBool(BinTreeProperty property, bool fallback) =>
            property switch
            {
                BinTreeBool value => value.Value,
                BinTreeBitBool value => value.Value,
                _ => fallback
            };

        private static uint? ReadOptionalUInt(
            IReadOnlyDictionary<uint, BinTreeProperty> properties,
            uint propertyHash)
        {
            if (!properties.TryGetValue(propertyHash, out BinTreeProperty property))
                return null;

            return property switch
            {
                BinTreeU8 value => value.Value,
                BinTreeU16 value => value.Value,
                BinTreeU32 value => value.Value,
                BinTreeU64 value when value.Value <= uint.MaxValue => (uint)value.Value,
                BinTreeI8 value when value.Value >= 0 => (uint)value.Value,
                BinTreeI16 value when value.Value >= 0 => (uint)value.Value,
                BinTreeI32 value when value.Value >= 0 => (uint)value.Value,
                BinTreeI64 value when value.Value >= 0 && value.Value <= uint.MaxValue => (uint)value.Value,
                _ => null
            };
        }

        internal static bool IsNeutralTexturePath(string texturePath) =>
            NormalizeToken(PathUtils.TruncateAtDot(
                Path.GetFileNameWithoutExtension(texturePath ?? string.Empty))) == "black";

        private static IReadOnlyList<string> SplitSubmeshNames(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return Array.Empty<string>();
            return value
                .Split((char[])null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .SelectMany(token => token.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static bool TryGetString(BinTreeStruct value, uint propertyHash, out string result) =>
            TryGetString(value?.Properties, propertyHash, out result);

        private static bool TryGetString(
            IReadOnlyDictionary<uint, BinTreeProperty> properties,
            uint propertyHash,
            out string result)
        {
            if (properties != null &&
                properties.TryGetValue(propertyHash, out BinTreeProperty property) &&
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
            out string result) =>
            TryGetTexturePath(value?.Properties, propertyHash, wadChunkPathResolver, out result);

        private static bool TryGetTexturePath(
            IReadOnlyDictionary<uint, BinTreeProperty> properties,
            uint propertyHash,
            Func<ulong, string> wadChunkPathResolver,
            out string result)
        {
            if (properties == null || !properties.TryGetValue(propertyHash, out BinTreeProperty property))
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
            if (property is BinTreeU64 u64)
            {
                string resolvedPath = wadChunkPathResolver?.Invoke(u64.Value);
                result = PathUtils.ToVirtualPath(
                    string.IsNullOrWhiteSpace(resolvedPath)
                        ? $"{u64.Value:x16}"
                        : resolvedPath);
                return true;
            }

            result = null;
            return false;
        }

        private static string ResolveBinEntryName(uint hash, Func<uint, string> binEntryResolver)
        {
            if (hash == 0 || binEntryResolver == null)
                return null;

            string resolved = binEntryResolver(hash);
            return string.IsNullOrWhiteSpace(resolved) ||
                   resolved.Equals(hash.ToString("x8"), StringComparison.OrdinalIgnoreCase)
                ? null
                : resolved;
        }

        internal static string MatchTextureKey(string texturePath, IReadOnlyList<string> availableKeys)
        {
            if (string.IsNullOrWhiteSpace(texturePath) || availableKeys == null || availableKeys.Count == 0)
            {
                return null;
            }

            string fileName = PathUtils.TruncateAtDot(Path.GetFileNameWithoutExtension(
                texturePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));

            string directMatch = availableKeys.FirstOrDefault(key =>
                key.Equals(fileName, StringComparison.OrdinalIgnoreCase));
            if (directMatch != null)
            {
                return directMatch;
            }

            // If texturePath is a 16-character hex hash from WadChunkLink
            if (fileName.Length == 16 &&
                ulong.TryParse(fileName, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out ulong hashValue))
            {
                string hashMatch = MatchTextureHash(hashValue, availableKeys);
                if (hashMatch != null)
                {
                    return hashMatch;
                }
            }

            return null;
        }

        private static string MatchTextureHash(ulong hashValue, IReadOnlyList<string> availableKeys)
        {
            foreach (string key in availableKeys)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                string[] parts = key.Split('_');
                var candidates = new List<string>(8);
                if (parts.Length >= 2)
                {
                    candidates.Add($"assets/characters/{parts[0]}/themes/{parts[1]}/{key}.tex");
                    candidates.Add($"assets/characters/{parts[0]}/themes/{parts[1]}/{key}.dds");
                    candidates.Add($"assets/characters/{parts[0]}/skins/{parts[1]}/{key}.tex");
                    candidates.Add($"assets/characters/{parts[0]}/skins/{parts[1]}/{key}.dds");
                    candidates.Add($"assets/characters/{parts[0]}/{key}.tex");
                    candidates.Add($"assets/characters/{parts[0]}/{key}.dds");
                }
                candidates.Add($"assets/{key}.tex");
                candidates.Add($"assets/{key}.dds");

                foreach (string candidate in candidates)
                {
                    if (XxHash64Ext.Hash(candidate.ToLowerInvariant()) == hashValue)
                    {
                        return key;
                    }
                }
            }

            return null;
        }

        internal static string FindBaseDiffuseTextureKey(IReadOnlyList<string> availableKeys)
        {
            if (availableKeys == null || availableKeys.Count == 0) return null;

            var filtered = availableKeys
                .Where(k => !IsPresentationTexture(k))
                .Where(k =>
                {
                    string nt = NormalizeToken(k);
                    return !IsNonColorTextureToken(nt) &&
                           !nt.Contains("face") &&
                           !nt.Contains("hair") &&
                           !nt.Contains("tool") &&
                           !nt.Contains("speedline");
                })
                .ToList();

            if (filtered.Count > 0)
            {
                return filtered.FirstOrDefault(k => NormalizeToken(k).EndsWith("txcm")) ?? filtered[0];
            }

            return availableKeys.FirstOrDefault(k => !IsPresentationTexture(k) && !IsNonColorTextureToken(NormalizeToken(k)));
        }

        private static bool IsNonColorTextureToken(string normalizedToken) =>
            normalizedToken.Contains("mask") ||
            normalizedToken.Contains("fresnel") ||
            normalizedToken.Contains("noise") ||
            normalizedToken.Contains("normal") ||
            normalizedToken.Contains("discolor") ||
            normalizedToken.Contains("rough") ||
            normalizedToken.Contains("metal") ||
            normalizedToken.Contains("matcap");

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
            PathUtils.NormalizeSeparators(value).Trim().ToLowerInvariant();
    }
}
