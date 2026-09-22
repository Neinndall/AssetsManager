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
    /// Reads the generic StaticMaterialDef preview contract used by LTK Manager 1.20.0.
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
        private static readonly uint Name = Fnv1a.HashLower("name");
        private static readonly uint Value = Fnv1a.HashLower("value");
        private static readonly uint On = Fnv1a.HashLower("on");
        private static readonly uint Passes = Fnv1a.HashLower("passes");
        private static readonly uint Shader = Fnv1a.HashLower("shader");
        private static readonly uint BlendEnable = Fnv1a.HashLower("blendEnable");
        private static readonly uint SourceColorBlendFactor = Fnv1a.HashLower("srcColorBlendFactor");
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

        private readonly HashResolverService _hashResolver;

        public MapMaterialParser(HashResolverService hashResolver = null)
        {
            _hashResolver = hashResolver;
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
            MapMaterialPassData pass = ReadFirstPass(material.Properties, warnings);
            MapShaderDefinitionData shader = ReadShaderDefinition(shaders, pass?.ShaderHash ?? 0, warnings);

            return MapMaterialSemantics.Resolve(
                name,
                pathHash,
                material.Properties.TryGetValue(DynamicMaterial, out BinTreeProperty dynamicValue) &&
                    dynamicValue is BinTreeStruct,
                pass,
                shader,
                ReadMaterialSamplers(material.Properties, shader, warnings),
                ReadParameters(material.Properties, shader, warnings),
                ReadSwitches(material.Properties, shader, warnings),
                ReadStringMap(material.Properties, ShaderMacros),
                warnings);
        }

        private MapMaterialPassData ReadFirstPass(
            IReadOnlyDictionary<uint, BinTreeProperty> properties,
            ICollection<string> warnings)
        {
            if (!properties.TryGetValue(Techniques, out BinTreeProperty techniquesProperty) ||
                techniquesProperty is not BinTreeContainer techniques)
            {
                warnings.Add("NoPass");
                return null;
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
                return null;
            }

            BinTreeStruct[] authoredPasses = passes.Elements.OfType<BinTreeStruct>().ToArray();
            if (authoredPasses.Length == 0)
            {
                warnings.Add("NoPass");
                return null;
            }
            if (authoredPasses.Length > 1)
                warnings.Add("SecondPass");

            IReadOnlyDictionary<uint, BinTreeProperty> pass = authoredPasses[0].Properties;
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
                ReadOptionalUInt(pass, WriteMask));
        }

        private MapShaderDefinitionData ReadShaderDefinition(
            BinTree shaders,
            uint shaderHash,
            ICollection<string> warnings)
        {
            if (shaderHash == 0)
                return null;

            if (shaders?.Objects == null)
            {
                warnings.Add("NoShaderDefs");
                return new MapShaderDefinitionData(
                    ResolveBinEntry(shaderHash),
                    Array.Empty<MapMaterialSamplerData>(),
                    EmptyVectorMap,
                    EmptyBoolMap,
                    EmptyStringMap);
            }

            if (!shaders.Objects.TryGetValue(shaderHash, out BinTreeObject shaderObject))
            {
                warnings.Add($"UnresolvedShader:{shaderHash:x8}");
                return new MapShaderDefinitionData(
                    ResolveBinEntry(shaderHash),
                    Array.Empty<MapMaterialSamplerData>(),
                    EmptyVectorMap,
                    EmptyBoolMap,
                    EmptyStringMap);
            }

            IReadOnlyDictionary<uint, BinTreeProperty> fields = shaderObject.Properties;
            string shaderPath = TryReadString(fields, ObjectPath, out string authoredPath)
                ? authoredPath
                : ResolveBinEntry(shaderHash);

            return new MapShaderDefinitionData(
                shaderPath,
                ReadShaderSamplers(fields),
                ReadShaderParameters(fields),
                ReadShaderSwitches(fields),
                ReadStringMap(fields, FeatureDefines));
        }

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
            HashSet<string> declared = shader?.DefaultSamplers
                ?.Select(sampler => sampler.Name)
                .ToHashSet(StringComparer.Ordinal);

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

                var parsed = new MapMaterialSamplerData(
                    name,
                    texture,
                    ReadWrap(sampler.Properties, AddressU),
                    ReadWrap(sampler.Properties, AddressV),
                    shaderDefault);
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
                var sampler = new MapMaterialSamplerData(
                    name,
                    ReadAsset(pathProperty),
                    MapTextureWrap.Repeat,
                    MapTextureWrap.Repeat);
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
            if (shader == null)
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
                if (shader != null && !shader.DefaultSwitches.ContainsKey(name))
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
            IReadOnlyDictionary<uint, BinTreeProperty> properties)
        {
            var result = new Dictionary<string, bool>(StringComparer.Ordinal);
            if (!properties.TryGetValue(StaticSwitches, out BinTreeProperty property) ||
                property is not BinTreeContainer switches)
            {
                return result;
            }

            foreach (BinTreeStruct item in switches.Elements.OfType<BinTreeStruct>())
            {
                if (!TryReadString(item.Properties, Name, out string name))
                    continue;
                result[name] = item.Properties.TryGetValue(OnByDefault, out BinTreeProperty enabled) &&
                    ReadBool(enabled, fallback: false);
            }
            return result;
        }

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
            if (_hashResolver == null || hash == 0)
                return null;

            string resolved = _hashResolver.ResolveHash(hash);
            if (string.IsNullOrWhiteSpace(resolved) ||
                resolved.Equals(hash.ToString("x16"), StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return PathUtils.ToVirtualPath(resolved);
        }

        private string ResolveBinEntry(uint hash)
        {
            if (_hashResolver == null || hash == 0)
                return null;

            string resolved = _hashResolver.ResolveBinEntry(hash);
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
