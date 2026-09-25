using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Viewer.Parsing
{
    /// <summary>
    /// Reads the generic StaticMaterialDef preview contract used by current LTK Manager MAIN.
    /// </summary>
    internal sealed class MapMaterialParser
    {
        private const string NormalTechnique = "normal";

        private static readonly uint SamplerValues = Fnv1a.HashLower("samplerValues");
        private static readonly uint ParamValues = Fnv1a.HashLower("paramValues");
        private static readonly uint Switches = Fnv1a.HashLower("switches");
        private static readonly uint ShaderMacros = Fnv1a.HashLower("shaderMacros");
        private static readonly uint Techniques = Fnv1a.HashLower("techniques");
        private static readonly uint DynamicMaterial = Fnv1a.HashLower("dynamicMaterial");
        private static readonly uint TextureName = Fnv1a.HashLower("TextureName");
        private static readonly uint TexturePath = Fnv1a.HashLower("texturePath");
        private static readonly uint AddressU = Fnv1a.HashLower("addressU");
        private static readonly uint AddressV = Fnv1a.HashLower("addressV");
        private static readonly uint AddressW = Fnv1a.HashLower("addressW");
        private static readonly uint FilterMin = Fnv1a.HashLower("filterMin");
        private static readonly uint FilterMag = Fnv1a.HashLower("filterMag");
        private static readonly uint Name = Fnv1a.HashLower("name");
        private static readonly uint Value = Fnv1a.HashLower("value");
        private static readonly uint On = Fnv1a.HashLower("on");
        private static readonly uint Passes = Fnv1a.HashLower("passes");
        private static readonly uint Shader = Fnv1a.HashLower("shader");
        private static readonly uint BlendEnable = Fnv1a.HashLower("blendEnable");
        private static readonly uint SourceColorBlendFactor = Fnv1a.HashLower("srcColorBlendFactor");
        private static readonly uint DestinationColorBlendFactor = Fnv1a.HashLower("dstColorBlendFactor");
        private static readonly uint SourceAlphaBlendFactor = Fnv1a.HashLower("srcAlphaBlendFactor");
        private static readonly uint DestinationAlphaBlendFactor = Fnv1a.HashLower("dstAlphaBlendFactor");
        private static readonly uint CullEnable = Fnv1a.HashLower("cullEnable");
        private static readonly uint WindingToCull = Fnv1a.HashLower("windingToCull");
        private static readonly uint DepthEnable = Fnv1a.HashLower("depthEnable");
        private static readonly uint DepthCompareFunc = Fnv1a.HashLower("depthCompareFunc");
        private static readonly uint WriteMask = Fnv1a.HashLower("writeMask");
        private static readonly uint MaterialType = Fnv1a.HashLower("type");

        private static readonly uint ObjectPath = Fnv1a.HashLower("objectPath");
        private static readonly uint ShaderTextures = Fnv1a.HashLower("textures");
        private static readonly uint ShaderParameters = Fnv1a.HashLower("parameters");
        private static readonly uint StaticSwitches = Fnv1a.HashLower("staticSwitches");
        private static readonly uint FeatureDefines = Fnv1a.HashLower("featureDefines");
        private static readonly uint DefaultTexturePath = Fnv1a.HashLower("defaultTexturePath");
        private static readonly uint ParameterData = Fnv1a.HashLower("data");
        private static readonly uint LogicalParameters = Fnv1a.HashLower("logicalParameters");
        private static readonly uint LogicalFields = Fnv1a.HashLower("fields");
        private static readonly uint OnByDefault = Fnv1a.HashLower("onByDefault");
        private static readonly uint RuntimeSwitch = 0x066e669c;
        private static readonly uint SamplerName = Fnv1a.HashLower("samplerName");

        private readonly Func<ulong, string> _wadChunkPathResolver;
        private readonly Func<uint, string> _binEntryResolver;

        public MapMaterialParser(HashResolverService hashResolver = null)
            : this(
                hashResolver == null ? null : hashResolver.ResolveHash,
                hashResolver == null ? null : hashResolver.ResolveBinEntry)
        {
        }

        internal MapMaterialParser(
            Func<ulong, string> wadChunkPathResolver,
            Func<uint, string> binEntryResolver)
        {
            _wadChunkPathResolver = wadChunkPathResolver;
            _binEntryResolver = binEntryResolver;
        }

        public IReadOnlyList<MapMaterialDefinition> Parse(
            BinTree materials,
            BinTree shaders,
            IReadOnlyList<string> materialNames)
        {
            if (materialNames == null || materialNames.Count == 0)
                return Array.Empty<MapMaterialDefinition>();

            var result = new MapMaterialDefinition[materialNames.Count];
            for (int index = 0; index < materialNames.Count; index++)
                result[index] = ParseOne(materials, shaders, materialNames[index]);
            return result;
        }

        internal MapMaterialDefinition ParseOne(
            BinTree materials,
            BinTree shaders,
            string materialName)
        {
            string name = (materialName ?? string.Empty).TrimEnd('\0');
            uint pathHash = Fnv1a.HashLower(name);
            if (materials?.Objects == null ||
                !materials.Objects.TryGetValue(pathHash, out BinTreeObject material))
            {
                return MapMaterialSemantics.Missing(name, pathHash);
            }

            var warnings = new List<string>();
            IReadOnlyList<MapMaterialPassData> passes = ReadPasses(material.Properties, warnings);
            MapMaterialPassData pass = passes.FirstOrDefault();
            MapShaderDefinitionData shader = ReadShaderDefinition(shaders, pass?.ShaderHash ?? 0, warnings);
            bool animated = material.Properties.TryGetValue(DynamicMaterial, out BinTreeProperty dynamicValue) &&
                            dynamicValue is BinTreeStruct;

            MapMaterialDefinition preview = MapMaterialSemantics.Resolve(
                name,
                pathHash,
                animated,
                pass,
                shader,
                ReadMaterialSamplers(material.Properties, shader, warnings),
                ReadParameters(material.Properties, shader, warnings),
                ReadSwitches(material.Properties, shader, warnings),
                ReadStringMap(material.Properties, ShaderMacros),
                warnings);

            GameMaterialProgram program = ResolveProgram(
                material.Properties,
                shaders == null ? Array.Empty<BinTree>() : new[] { shaders },
                passes,
                animated,
                warnings);
            return preview with
            {
                Program = program,
                Warnings = warnings.Distinct(StringComparer.Ordinal).ToArray()
            };
        }

        private IReadOnlyList<MapMaterialPassData> ReadPasses(
            IReadOnlyDictionary<uint, BinTreeProperty> properties,
            ICollection<string> warnings)
        {
            if (!properties.TryGetValue(Techniques, out BinTreeProperty techniquesProperty) ||
                techniquesProperty is not BinTreeContainer techniques)
            {
                warnings.Add("NoPass");
                return Array.Empty<MapMaterialPassData>();
            }

            BinTreeStruct technique = techniques.Elements
                .OfType<BinTreeStruct>()
                .FirstOrDefault(candidate =>
                    TryReadString(candidate.Properties, Name, out string techniqueName) &&
                    techniqueName == NormalTechnique) ??
                techniques.Elements.OfType<BinTreeStruct>().FirstOrDefault();
            if (technique == null ||
                !technique.Properties.TryGetValue(Passes, out BinTreeProperty passesProperty) ||
                passesProperty is not BinTreeContainer passes)
            {
                warnings.Add("NoPass");
                return Array.Empty<MapMaterialPassData>();
            }

            BinTreeStruct[] authoredPasses = passes.Elements.OfType<BinTreeStruct>().ToArray();
            if (authoredPasses.Length == 0)
            {
                warnings.Add("NoPass");
                return Array.Empty<MapMaterialPassData>();
            }

            return authoredPasses.Select(item => ReadPass(item.Properties, warnings)).ToArray();
        }

        private MapMaterialPassData ReadPass(
            IReadOnlyDictionary<uint, BinTreeProperty> pass,
            ICollection<string> warnings)
        {
            uint shaderHash = pass.TryGetValue(Shader, out BinTreeProperty shaderProperty) &&
                              shaderProperty is BinTreeObjectLink shaderLink
                ? shaderLink.Value
                : 0;
            if (shaderHash == 0)
                warnings.Add("UnresolvedShader:00000000");

            return new MapMaterialPassData(
                shaderHash,
                ReadParametersRaw(pass),
                ReadStringMap(pass, ShaderMacros),
                ReadOptionalBool(pass, BlendEnable),
                ReadOptionalUInt(pass, SourceColorBlendFactor),
                ReadOptionalUInt(pass, DestinationColorBlendFactor),
                ReadOptionalBool(pass, CullEnable),
                ReadOptionalUInt(pass, WindingToCull),
                ReadOptionalBool(pass, DepthEnable),
                ReadOptionalUInt(pass, WriteMask),
                ReadOptionalUInt(pass, SourceAlphaBlendFactor),
                ReadOptionalUInt(pass, DestinationAlphaBlendFactor),
                ReadOptionalUInt(pass, DepthCompareFunc));
        }

        private MapShaderDefinitionData ReadShaderDefinition(
            BinTree shaders,
            uint shaderHash,
            ICollection<string> warnings) =>
            ReadShaderDefinition(
                shaders == null ? Array.Empty<BinTree>() : new[] { shaders },
                shaderHash,
                warnings);

        private MapShaderDefinitionData ReadShaderDefinition(
            IEnumerable<BinTree> shaderTrees,
            uint shaderHash,
            ICollection<string> warnings)
        {
            if (shaderHash == 0)
                return null;

            BinTree[] trees = (shaderTrees ?? Enumerable.Empty<BinTree>())
                .Where(tree => tree?.Objects != null)
                .ToArray();
            if (trees.Length == 0)
            {
                warnings.Add("NoShaderDefs");
                return UndeclaredShader(shaderHash);
            }

            BinTreeObject shaderObject = trees
                .Select(tree => tree.Objects.TryGetValue(shaderHash, out BinTreeObject candidate) ? candidate : null)
                .FirstOrDefault(candidate => candidate != null);
            if (shaderObject == null)
            {
                warnings.Add($"UnresolvedShader:{shaderHash:x8}");
                return UndeclaredShader(shaderHash);
            }

            IReadOnlyDictionary<uint, BinTreeProperty> fields = shaderObject.Properties;
            string shaderPath = TryReadString(fields, ObjectPath, out string authoredPath)
                ? authoredPath
                : ResolveBinEntry(shaderHash);

            IReadOnlyDictionary<string, MapShaderSwitchData> switchDeclarations = ReadShaderSwitchDeclarations(fields);
            return new MapShaderDefinitionData(
                shaderPath,
                ReadShaderSamplers(fields),
                ReadShaderParameters(fields),
                switchDeclarations.ToDictionary(pair => pair.Key, pair => pair.Value.OnByDefault, StringComparer.Ordinal),
                ReadStringMap(fields, FeatureDefines),
                true,
                ReadShaderPhysicalParameters(fields),
                switchDeclarations);
        }

        private MapShaderDefinitionData UndeclaredShader(uint shaderHash) => new(
            ResolveBinEntry(shaderHash),
            Array.Empty<MapMaterialSamplerData>(),
            EmptyVectorMap,
            EmptyBoolMap,
            EmptyStringMap,
            false);

        private IReadOnlyList<MapMaterialSamplerData> ReadMaterialSamplers(
            IReadOnlyDictionary<uint, BinTreeProperty> properties,
            MapShaderDefinitionData shader,
            ICollection<string> warnings)
        {
            if (!properties.TryGetValue(SamplerValues, out BinTreeProperty property) ||
                property is not BinTreeContainer samplers)
            {
                return Array.Empty<MapMaterialSamplerData>();
            }

            var result = new List<MapMaterialSamplerData>();
            var indices = new Dictionary<string, int>(StringComparer.Ordinal);
            HashSet<string> declared = shader?.IsDeclared == true
                ? (shader.DefaultSamplers ?? Array.Empty<MapMaterialSamplerData>())
                    .Select(sampler => sampler.Name)
                    .ToHashSet(StringComparer.Ordinal)
                : null;

            foreach (BinTreeStruct sampler in samplers.Elements.OfType<BinTreeStruct>())
            {
                if (!TryReadString(sampler.Properties, TextureName, out string name))
                    continue;
                if (declared != null && !declared.Contains(name))
                    warnings.Add($"UndeclaredSampler:{name}");

                MapTextureReference texture = null;
                bool shaderDefault = false;
                if (sampler.Properties.TryGetValue(TexturePath, out BinTreeProperty textureProperty))
                {
                    if (textureProperty is BinTreeString text && !string.IsNullOrEmpty(text.Value))
                    {
                        warnings.Add($"StringTexturePath:{name}:{text.Value}");
                        shaderDefault = true;
                    }
                    else
                    {
                        texture = ReadAsset(textureProperty);
                    }
                }

                MapMaterialSamplerData declaredSampler = shader?.DefaultSamplers?.FirstOrDefault(item => item.Name == name);
                GameMaterialTextureSource source = texture != null
                    ? GameMaterialTextureSource.Material
                    : declaredSampler?.Texture != null
                        ? GameMaterialTextureSource.ShaderDefault
                        : GameMaterialTextureSource.Fallback;
                var parsed = new MapMaterialSamplerData(
                    name,
                    texture,
                    ReadWrap(sampler.Properties, AddressU),
                    ReadWrap(sampler.Properties, AddressV),
                    shaderDefault || texture == null,
                    ReadWrap(sampler.Properties, AddressW),
                    (ReadOptionalUInt(sampler.Properties, FilterMin) ?? 1) == 1,
                    (ReadOptionalUInt(sampler.Properties, FilterMag) ?? 1) == 1,
                    declaredSampler?.SharedSampler,
                    source);
                if (indices.TryGetValue(name, out int existing))
                    result[existing] = parsed;
                else
                {
                    indices[name] = result.Count;
                    result.Add(parsed);
                }
            }
            return result;
        }

        private IReadOnlyList<MapMaterialSamplerData> ReadShaderSamplers(
            IReadOnlyDictionary<uint, BinTreeProperty> properties)
        {
            if (!properties.TryGetValue(ShaderTextures, out BinTreeProperty property) ||
                property is not BinTreeContainer textures)
            {
                return Array.Empty<MapMaterialSamplerData>();
            }

            var result = new List<MapMaterialSamplerData>();
            var indices = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (BinTreeStruct texture in textures.Elements.OfType<BinTreeStruct>())
            {
                if (!TryReadString(texture.Properties, Name, out string name))
                    continue;

                texture.Properties.TryGetValue(DefaultTexturePath, out BinTreeProperty pathProperty);
                MapTextureReference defaultTexture = ReadAsset(pathProperty);
                TryReadString(texture.Properties, SamplerName, out string sharedSampler);
                var sampler = new MapMaterialSamplerData(
                    name,
                    defaultTexture,
                    MapTextureWrap.Repeat,
                    MapTextureWrap.Repeat,
                    false,
                    MapTextureWrap.Repeat,
                    true,
                    true,
                    sharedSampler,
                    defaultTexture != null
                        ? GameMaterialTextureSource.ShaderDefault
                        : GameMaterialTextureSource.Fallback);
                if (indices.TryGetValue(name, out int existing))
                    result[existing] = sampler;
                else
                {
                    indices[name] = result.Count;
                    result.Add(sampler);
                }
            }
            return result;
        }

        private IReadOnlyDictionary<string, Vector4> ReadParameters(
            IReadOnlyDictionary<uint, BinTreeProperty> properties,
            MapShaderDefinitionData shader,
            ICollection<string> warnings)
        {
            Dictionary<string, Vector4> parameters = ReadParametersRaw(properties);
            if (shader?.IsDeclared != true)
                return parameters;

            foreach (string name in parameters.Keys)
                if (!shader.DefaultParameters.ContainsKey(name))
                    warnings.Add($"UndeclaredParam:{name}");
            return parameters;
        }

        private static Dictionary<string, Vector4> ReadParametersRaw(
            IReadOnlyDictionary<uint, BinTreeProperty> properties)
        {
            var result = new Dictionary<string, Vector4>(StringComparer.Ordinal);
            if (!properties.TryGetValue(ParamValues, out BinTreeProperty property) ||
                property is not BinTreeContainer parameters)
            {
                return result;
            }

            foreach (BinTreeStruct parameter in parameters.Elements.OfType<BinTreeStruct>())
            {
                if (!TryReadString(parameter.Properties, Name, out string name))
                    continue;
                result[name] = parameter.Properties.TryGetValue(Value, out BinTreeProperty value) &&
                               value is BinTreeVector4 vector
                    ? vector.Value
                    : Vector4.Zero;
            }
            return result;
        }

        private IReadOnlyDictionary<string, bool> ReadSwitches(
            IReadOnlyDictionary<uint, BinTreeProperty> properties,
            MapShaderDefinitionData shader,
            ICollection<string> warnings)
        {
            var result = new Dictionary<string, bool>(StringComparer.Ordinal);
            if (!properties.TryGetValue(Switches, out BinTreeProperty property) ||
                property is not BinTreeContainer switches)
            {
                return result;
            }

            foreach (BinTreeStruct item in switches.Elements.OfType<BinTreeStruct>())
            {
                if (!TryReadString(item.Properties, Name, out string name))
                    continue;
                if (shader?.IsDeclared == true && !shader.DefaultSwitches.ContainsKey(name))
                    warnings.Add($"UndeclaredSwitch:{name}");
                result[name] = item.Properties.TryGetValue(On, out BinTreeProperty enabled)
                    ? ReadBool(enabled, fallback: true)
                    : true;
            }
            return result;
        }

        private static IReadOnlyDictionary<string, Vector4> ReadShaderParameters(
            IReadOnlyDictionary<uint, BinTreeProperty> properties)
        {
            var result = new Dictionary<string, Vector4>(StringComparer.Ordinal);
            if (!properties.TryGetValue(ShaderParameters, out BinTreeProperty property) ||
                property is not BinTreeContainer parameters)
            {
                return result;
            }

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
                        if (TryReadString(logical.Properties, Name, out string logicalName))
                            result[logicalName] = data;
                }

                if (TryReadString(parameter.Properties, Name, out string physicalName))
                    result[physicalName] = data;
            }
            return result;
        }

        private static IReadOnlyDictionary<string, bool> ReadShaderSwitches(
            IReadOnlyDictionary<uint, BinTreeProperty> properties) =>
            ReadShaderSwitchDeclarations(properties)
                .ToDictionary(pair => pair.Key, pair => pair.Value.OnByDefault, StringComparer.Ordinal);

        private static IReadOnlyDictionary<string, MapShaderSwitchData> ReadShaderSwitchDeclarations(
            IReadOnlyDictionary<uint, BinTreeProperty> properties)
        {
            var result = new Dictionary<string, MapShaderSwitchData>(StringComparer.Ordinal);
            if (!properties.TryGetValue(StaticSwitches, out BinTreeProperty property) ||
                property is not BinTreeContainer switches)
            {
                return result;
            }

            foreach (BinTreeStruct item in switches.Elements.OfType<BinTreeStruct>())
            {
                if (!TryReadString(item.Properties, Name, out string name))
                    continue;
                bool onByDefault = item.Properties.TryGetValue(OnByDefault, out BinTreeProperty enabled) &&
                                   ReadBool(enabled, fallback: false);
                bool runtime = item.Properties.TryGetValue(RuntimeSwitch, out BinTreeProperty runtimeValue) &&
                               ReadBool(runtimeValue, fallback: false);
                result[name] = new MapShaderSwitchData(onByDefault, runtime);
            }
            return result;
        }

        private static IReadOnlyList<MapShaderPhysicalParameterData> ReadShaderPhysicalParameters(
            IReadOnlyDictionary<uint, BinTreeProperty> properties)
        {
            var result = new List<MapShaderPhysicalParameterData>();
            if (!properties.TryGetValue(ShaderParameters, out BinTreeProperty property) ||
                property is not BinTreeContainer parameters)
            {
                return result;
            }

            foreach (BinTreeStruct parameter in parameters.Elements.OfType<BinTreeStruct>())
            {
                if (!TryReadString(parameter.Properties, Name, out string physicalName))
                    continue;
                Vector4 data = parameter.Properties.TryGetValue(ParameterData, out BinTreeProperty dataValue) &&
                               dataValue is BinTreeVector4 vector
                    ? vector.Value
                    : Vector4.Zero;
                var logical = new List<MapShaderLogicalParameterData>();
                if (parameter.Properties.TryGetValue(LogicalParameters, out BinTreeProperty logicalProperty) &&
                    logicalProperty is BinTreeContainer logicalParameters)
                {
                    foreach (BinTreeStruct entry in logicalParameters.Elements.OfType<BinTreeStruct>())
                    {
                        if (!TryReadString(entry.Properties, Name, out string logicalName))
                            continue;
                        logical.Add(new MapShaderLogicalParameterData(
                            logicalName,
                            ReadOptionalUInt(entry.Properties, LogicalFields) ?? 0));
                    }
                }
                result.Add(new MapShaderPhysicalParameterData(physicalName, data, logical));
            }
            return result;
        }

        internal GameMaterialProgram ParseProgram(
            BinTreeObject material,
            IEnumerable<BinTree> shaderTrees)
        {
            if (material == null)
                return null;

            var warnings = new List<string>();
            IReadOnlyList<MapMaterialPassData> passes = ReadPasses(material.Properties, warnings);
            bool animated = material.Properties.TryGetValue(DynamicMaterial, out BinTreeProperty dynamicValue) &&
                            dynamicValue is BinTreeStruct;
            return ResolveProgram(material.Properties, shaderTrees, passes, animated, warnings);
        }

        private GameMaterialProgram ResolveProgram(
            IReadOnlyDictionary<uint, BinTreeProperty> material,
            IEnumerable<BinTree> shaderTrees,
            IReadOnlyList<MapMaterialPassData> passes,
            bool animated,
            ICollection<string> warnings)
        {
            IReadOnlyDictionary<string, Vector4> materialParameters = ReadParametersRaw(material);
            IReadOnlyDictionary<string, string> materialMacros = ReadStringMap(material, ShaderMacros);
            var resolved = new List<GameMaterialPass>();

            foreach (MapMaterialPassData pass in passes ?? Array.Empty<MapMaterialPassData>())
            {
                MapShaderDefinitionData shader = ReadShaderDefinition(shaderTrees, pass.ShaderHash, warnings);
                IReadOnlyList<MapMaterialSamplerData> authoredSamplers = ReadMaterialSamplers(material, shader, warnings);
                IReadOnlyDictionary<string, bool> switches = ResolveProgramSwitches(material, shader, warnings);
                resolved.Add(new GameMaterialPass(
                    pass.ShaderHash,
                    shader?.Path,
                    ResolveProgramDefines(materialMacros, pass.ShaderMacros, shader, switches),
                    ResolveRuntimeSwitches(shader, switches),
                    ResolveProgramTextures(authoredSamplers, shader),
                    ResolveProgramParameters(materialParameters, pass.Parameters, shader, warnings),
                    ResolveProgramState(pass)));
            }

            return new GameMaterialProgram(
                ResolveMaterialKind(ReadOptionalUInt(material, MaterialType)),
                animated,
                resolved);
        }

        private IReadOnlyDictionary<string, bool> ResolveProgramSwitches(
            IReadOnlyDictionary<uint, BinTreeProperty> material,
            MapShaderDefinitionData shader,
            ICollection<string> warnings)
        {
            var result = new Dictionary<string, bool>(StringComparer.Ordinal);
            if (shader?.SwitchDeclarations != null)
            {
                foreach ((string name, MapShaderSwitchData declaration) in shader.SwitchDeclarations)
                    result[name] = declaration.OnByDefault;
            }
            foreach ((string name, bool enabled) in ReadSwitches(material, shader, warnings))
                result[name] = enabled;
            return result;
        }

        private static IReadOnlyList<GameMaterialDefine> ResolveProgramDefines(
            IReadOnlyDictionary<string, string> material,
            IReadOnlyDictionary<string, string> pass,
            MapShaderDefinitionData shader,
            IReadOnlyDictionary<string, bool> switches)
        {
            var result = new Dictionary<string, GameMaterialDefine>(StringComparer.Ordinal);
            void Set(string name, string value, GameMaterialDefineSource source)
            {
                if (!string.IsNullOrWhiteSpace(name))
                    result[name] = new GameMaterialDefine(name, value ?? string.Empty, source);
            }

            foreach ((string name, string value) in material ?? EmptyStringMap)
                Set(name, value, GameMaterialDefineSource.Material);
            foreach ((string name, string value) in shader?.FeatureDefines ?? EmptyStringMap)
                Set(name, value, GameMaterialDefineSource.Feature);
            if (shader?.SwitchDeclarations != null)
            {
                foreach ((string name, MapShaderSwitchData declaration) in shader.SwitchDeclarations)
                {
                    if (!declaration.Runtime)
                        Set(name, switches.TryGetValue(name, out bool on) && on ? "1" : "0", GameMaterialDefineSource.Switch);
                }
            }
            foreach ((string name, string value) in pass ?? EmptyStringMap)
                Set(name, value, GameMaterialDefineSource.Pass);

            return result.Values.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
        }

        private static IReadOnlyList<KeyValuePair<string, bool>> ResolveRuntimeSwitches(
            MapShaderDefinitionData shader,
            IReadOnlyDictionary<string, bool> switches)
        {
            if (shader?.SwitchDeclarations == null)
                return Array.Empty<KeyValuePair<string, bool>>();

            return shader.SwitchDeclarations
                .Where(pair => pair.Value.Runtime)
                .Select(pair => new KeyValuePair<string, bool>(
                    pair.Key,
                    switches.TryGetValue(pair.Key, out bool on) ? on : pair.Value.OnByDefault))
                .ToArray();
        }

        private static IReadOnlyList<GameMaterialTexture> ResolveProgramTextures(
            IReadOnlyList<MapMaterialSamplerData> authored,
            MapShaderDefinitionData shader)
        {
            var merged = new Dictionary<string, MapMaterialSamplerData>(StringComparer.Ordinal);
            foreach (MapMaterialSamplerData sampler in authored ?? Array.Empty<MapMaterialSamplerData>())
                merged[sampler.Name] = sampler;

            foreach (MapMaterialSamplerData fallback in shader?.DefaultSamplers ?? Array.Empty<MapMaterialSamplerData>())
            {
                if (!merged.TryGetValue(fallback.Name, out MapMaterialSamplerData sampler))
                {
                    merged[fallback.Name] = fallback;
                    continue;
                }
                if (sampler.Texture == null && sampler.UsesShaderDefaultTexture)
                {
                    merged[fallback.Name] = sampler with
                    {
                        Texture = fallback.Texture,
                        SharedSampler = fallback.SharedSampler,
                        TextureSource = fallback.Texture != null
                            ? GameMaterialTextureSource.ShaderDefault
                            : GameMaterialTextureSource.Fallback
                    };
                }
            }

            IEnumerable<string> names = shader?.IsDeclared == true
                ? (shader.DefaultSamplers ?? Array.Empty<MapMaterialSamplerData>()).Select(item => item.Name)
                : merged.Keys;
            return names
                .Where(merged.ContainsKey)
                .Select(name =>
                {
                    MapMaterialSamplerData sampler = merged[name];
                    return new GameMaterialTexture(
                        name,
                        sampler.Texture,
                        sampler.TextureSource,
                        new GameMaterialSamplerState(
                            sampler.SharedSampler,
                            sampler.WrapU,
                            sampler.WrapV,
                            sampler.WrapW,
                            sampler.FilterMin,
                            sampler.FilterMag));
                })
                .ToArray();
        }

        private static IReadOnlyList<GameMaterialParameter> ResolveProgramParameters(
            IReadOnlyDictionary<string, Vector4> material,
            IReadOnlyDictionary<string, Vector4> pass,
            MapShaderDefinitionData shader,
            ICollection<string> warnings)
        {
            if (shader?.IsDeclared != true || shader.PhysicalParameters == null)
            {
                var values = new Dictionary<string, GameMaterialParameter>(StringComparer.Ordinal);
                foreach ((string name, Vector4 value) in pass ?? EmptyVectorMap)
                    values[name] = new GameMaterialParameter(name, value, GameMaterialParamSource.Pass);
                foreach ((string name, Vector4 value) in material ?? EmptyVectorMap)
                    values[name] = new GameMaterialParameter(name, value, GameMaterialParamSource.Material);
                return values.Values.ToArray();
            }

            var resolved = shader.PhysicalParameters
                .Select(item => new GameMaterialParameter(item.Name, item.Data, GameMaterialParamSource.ShaderDefault))
                .ToArray();
            ApplyProgramParameters(resolved, pass, GameMaterialParamSource.Pass, shader, warnings);
            ApplyProgramParameters(resolved, material, GameMaterialParamSource.Material, shader, warnings);
            return resolved;
        }

        private static void ApplyProgramParameters(
            GameMaterialParameter[] target,
            IReadOnlyDictionary<string, Vector4> values,
            GameMaterialParamSource source,
            MapShaderDefinitionData shader,
            ICollection<string> warnings)
        {
            foreach ((string name, Vector4 value) in values ?? EmptyVectorMap)
            {
                if (!TryFindParameterTarget(shader.PhysicalParameters, name, out int index, out uint mask))
                {
                    warnings.Add($"UndeclaredParam:{name}");
                    continue;
                }

                Vector4 current = target[index].Value;
                if (!Scatter(ref current, mask, value))
                    continue;
                target[index] = target[index] with { Value = current, Source = source };
            }
        }

        private static bool TryFindParameterTarget(
            IReadOnlyList<MapShaderPhysicalParameterData> physical,
            string name,
            out int index,
            out uint mask)
        {
            for (int i = 0; i < physical.Count; i++)
            {
                MapShaderLogicalParameterData logical = physical[i].LogicalParameters?.FirstOrDefault(item => item.Name == name);
                if (logical != null)
                {
                    index = i;
                    mask = logical.Fields;
                    return true;
                }
            }
            for (int i = 0; i < physical.Count; i++)
            {
                if (physical[i].Name == name)
                {
                    index = i;
                    mask = 0b1111;
                    return true;
                }
            }
            index = -1;
            mask = 0;
            return false;
        }

        private static bool Scatter(ref Vector4 target, uint mask, Vector4 input)
        {
            float[] destination = { target.X, target.Y, target.Z, target.W };
            float[] source = { input.X, input.Y, input.Z, input.W };
            int next = 0;
            bool wrote = false;
            for (int component = 0; component < 4 && next < 4; component++)
            {
                if ((mask & (1u << component)) == 0)
                    continue;
                destination[component] = source[next++];
                wrote = true;
            }
            target = new Vector4(destination[0], destination[1], destination[2], destination[3]);
            return wrote;
        }

        private static GameMaterialPassState ResolveProgramState(MapMaterialPassData pass) =>
            new(
                pass?.BlendEnabled ?? false,
                ToBlendFactor(pass?.SourceBlendFactor, MapBlendFactor.One),
                ToBlendFactor(pass?.DestinationBlendFactor, MapBlendFactor.Zero),
                ToBlendFactor(pass?.SourceAlphaBlendFactor, MapBlendFactor.One),
                ToBlendFactor(pass?.DestinationAlphaBlendFactor, MapBlendFactor.Zero),
                pass?.CullEnabled ?? true,
                (pass?.WindingToCull ?? 1) == 0
                    ? GameMaterialWinding.Clockwise
                    : GameMaterialWinding.CounterClockwise,
                pass?.DepthEnabled ?? true,
                pass?.DepthCompareFunc ?? 3,
                pass?.WriteMask ?? 31);

        private static MapBlendFactor ToBlendFactor(uint? value, MapBlendFactor fallback) =>
            value switch
            {
                0 => MapBlendFactor.Zero,
                1 => MapBlendFactor.One,
                2 => MapBlendFactor.SourceColor,
                3 => MapBlendFactor.OneMinusSourceColor,
                4 => MapBlendFactor.DestinationColor,
                5 => MapBlendFactor.OneMinusDestinationColor,
                6 => MapBlendFactor.SourceAlpha,
                7 => MapBlendFactor.OneMinusSourceAlpha,
                _ => fallback
            };

        private static GameMaterialKind ResolveMaterialKind(uint? value) =>
            value switch
            {
                0 => GameMaterialKind.StaticMesh,
                null or 1 => GameMaterialKind.SkinnedMesh,
                2 => GameMaterialKind.Particles,
                3 => GameMaterialKind.Ui,
                4 => GameMaterialKind.PostProcess,
                _ => GameMaterialKind.Unknown
            };

        private MapTextureReference ReadAsset(BinTreeProperty property)
        {
            return property switch
            {
                BinTreeString text when !string.IsNullOrWhiteSpace(text.Value) =>
                    new MapTextureReference(PathUtils.ToVirtualPath(text.Value), 0),
                BinTreeWadChunkLink link when link.Value != 0 =>
                    new MapTextureReference(ResolveWadPath(link.Value), link.Value),
                _ => null
            };
        }

        private string ResolveWadPath(ulong hash)
        {
            if (_wadChunkPathResolver == null || hash == 0)
                return null;

            string resolved = _wadChunkPathResolver(hash);
            if (string.IsNullOrWhiteSpace(resolved) ||
                resolved.Equals(hash.ToString("x16"), StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return PathUtils.ToVirtualPath(resolved);
        }

        private string ResolveBinEntry(uint hash)
        {
            if (_binEntryResolver == null || hash == 0)
                return null;

            string resolved = _binEntryResolver(hash);
            return string.IsNullOrWhiteSpace(resolved) ||
                   resolved.Equals(hash.ToString("x8"), StringComparison.OrdinalIgnoreCase)
                ? null
                : resolved;
        }

        private static IReadOnlyDictionary<string, string> ReadStringMap(
            IReadOnlyDictionary<uint, BinTreeProperty> properties,
            uint hash)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!properties.TryGetValue(hash, out BinTreeProperty property) ||
                property is not BinTreeMap map)
            {
                return result;
            }

            foreach ((BinTreeProperty key, BinTreeProperty value) in map)
                if (key is BinTreeString keyText && value is BinTreeString valueText)
                    result[keyText.Value] = valueText.Value;
            return result;
        }

        private static bool TryReadString(
            IReadOnlyDictionary<uint, BinTreeProperty> properties,
            uint hash,
            out string value)
        {
            if (properties.TryGetValue(hash, out BinTreeProperty property) &&
                property is BinTreeString text &&
                !string.IsNullOrWhiteSpace(text.Value))
            {
                value = text.Value;
                return true;
            }

            value = null;
            return false;
        }

        private static MapTextureWrap ReadWrap(
            IReadOnlyDictionary<uint, BinTreeProperty> properties,
            uint hash) =>
            ReadOptionalUInt(properties, hash) switch
            {
                1 => MapTextureWrap.Clamp,
                2 => MapTextureWrap.Mirror,
                3 => MapTextureWrap.Border,
                _ => MapTextureWrap.Repeat
            };

        private static bool? ReadOptionalBool(
            IReadOnlyDictionary<uint, BinTreeProperty> properties,
            uint hash) =>
            properties.TryGetValue(hash, out BinTreeProperty property)
                ? ReadBool(property, fallback: false)
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
            uint hash)
        {
            if (!properties.TryGetValue(hash, out BinTreeProperty property))
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

        private static readonly IReadOnlyDictionary<string, Vector4> EmptyVectorMap =
            new Dictionary<string, Vector4>(StringComparer.Ordinal);
        private static readonly IReadOnlyDictionary<string, bool> EmptyBoolMap =
            new Dictionary<string, bool>(StringComparer.Ordinal);
        private static readonly IReadOnlyDictionary<string, string> EmptyStringMap =
            new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
