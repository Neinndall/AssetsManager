using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Viewer
{
    internal sealed class MapGeometryMaterialResolver
    {
        private static readonly uint StaticMaterialDefHash = Fnv1a.HashLower("StaticMaterialDef");
        private static readonly uint SamplerValuesHash = Fnv1a.HashLower("samplerValues");
        private static readonly uint TextureNameHash = Fnv1a.HashLower("textureName");
        private static readonly uint SamplerNameHash = Fnv1a.HashLower("samplerName");
        private static readonly uint TexturePathHash = Fnv1a.HashLower("texturePath");
        private static readonly uint AddressUHash = Fnv1a.HashLower("addressU");
        private static readonly uint AddressVHash = Fnv1a.HashLower("addressV");
        private static readonly uint FilterMinHash = Fnv1a.HashLower("filterMin");
        private static readonly uint FilterMagHash = Fnv1a.HashLower("filterMag");
        private static readonly uint ParamValuesHash = Fnv1a.HashLower("paramValues");
        private static readonly uint NameHash = Fnv1a.HashLower("name");
        private static readonly uint ValueHash = Fnv1a.HashLower("value");
        private static readonly uint TechniquesHash = Fnv1a.HashLower("techniques");
        private static readonly uint PassesHash = Fnv1a.HashLower("passes");
        private static readonly uint ShaderHash = Fnv1a.HashLower("shader");

        private static readonly string[] TintParameterNames =
        {
            "Color",
            "TintColor",
            "Tint_Color",
            "Emissive_Color"
        };

        private readonly BinTree _materials;

        public MapGeometryMaterialResolver(BinTree materials)
        {
            _materials = materials;
        }

        public bool TryResolve(string materialName, out MapGeometryMaterialDefinition definition)
        {
            definition = null;
            if (_materials == null || string.IsNullOrWhiteSpace(materialName))
                return false;

            uint materialHash = Fnv1a.HashLower(materialName.TrimEnd('\0'));
            if (!_materials.Objects.TryGetValue(materialHash, out BinTreeObject materialObject) ||
                materialObject.ClassHash != StaticMaterialDefHash)
            {
                return false;
            }

            var samplers = ReadSamplers(materialObject);
            var parameters = ReadParameters(materialObject);
            definition = new MapGeometryMaterialDefinition(
                materialName,
                samplers,
                SelectPrimarySampler(samplers),
                ReadTintColor(parameters),
                parameters,
                ReadShaderHash(materialObject));
            return true;
        }

        internal static MapGeometryTextureSampler SelectPrimarySampler(
            IReadOnlyList<MapGeometryTextureSampler> samplers)
        {
            MapGeometryTextureSampler selected = samplers?
                .Where(x => !string.IsNullOrWhiteSpace(x.TexturePath))
                .OrderBy(x => GetSamplerPriority(x.TextureName, x.SamplerName))
                .FirstOrDefault();
            return selected != null && GetSamplerPriority(selected.TextureName, selected.SamplerName) < 1000
                ? selected
                : null;
        }

        private static List<MapGeometryTextureSampler> ReadSamplers(BinTreeObject materialObject)
        {
            var result = new List<MapGeometryTextureSampler>();
            if (!materialObject.Properties.TryGetValue(SamplerValuesHash, out BinTreeProperty property) ||
                property is not BinTreeContainer container)
            {
                return result;
            }

            foreach (BinTreeStruct sampler in container.Elements.OfType<BinTreeStruct>())
            {
                string texturePath = ReadString(sampler, TexturePathHash);
                if (string.IsNullOrWhiteSpace(texturePath))
                    continue;

                result.Add(new MapGeometryTextureSampler(
                    ReadString(sampler, TextureNameHash),
                    ReadString(sampler, SamplerNameHash),
                    texturePath,
                    ReadUInt32(sampler, AddressUHash),
                    ReadUInt32(sampler, AddressVHash),
                    ReadUInt32(sampler, FilterMinHash),
                    ReadUInt32(sampler, FilterMagHash)));
            }

            return result;
        }

        private static Dictionary<string, Vector4> ReadParameters(BinTreeObject materialObject)
        {
            var result = new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase);
            if (!materialObject.Properties.TryGetValue(ParamValuesHash, out BinTreeProperty property) ||
                property is not BinTreeContainer parameters)
            {
                return result;
            }

            foreach (BinTreeStruct parameter in parameters.Elements.OfType<BinTreeStruct>())
            {
                string name = ReadString(parameter, NameHash);
                if (!string.IsNullOrWhiteSpace(name) &&
                    parameter.Properties.TryGetValue(ValueHash, out BinTreeProperty value) &&
                    value is BinTreeVector4 vector)
                    result[name] = vector.Value;
            }

            return result;
        }

        private static Vector4? ReadTintColor(IReadOnlyDictionary<string, Vector4> parameters)
        {
            foreach (string parameterName in TintParameterNames)
                if (parameters.TryGetValue(parameterName, out Vector4 value))
                    return value;

            return null;
        }

        private static uint ReadShaderHash(BinTreeObject materialObject)
        {
            if (!materialObject.Properties.TryGetValue(TechniquesHash, out BinTreeProperty property) ||
                property is not BinTreeContainer techniques)
            {
                return 0;
            }

            BinTreeStruct technique = techniques.Elements.OfType<BinTreeStruct>().FirstOrDefault();
            if (technique == null ||
                !technique.Properties.TryGetValue(PassesHash, out BinTreeProperty passesProperty) ||
                passesProperty is not BinTreeContainer passes)
            {
                return 0;
            }

            BinTreeStruct pass = passes.Elements.OfType<BinTreeStruct>().FirstOrDefault();
            return pass != null &&
                   pass.Properties.TryGetValue(ShaderHash, out BinTreeProperty shaderProperty) &&
                   shaderProperty is BinTreeObjectLink shader
                ? shader.Value
                : 0;
        }

        private static int GetSamplerPriority(string textureName, string samplerName)
        {
            string identity = string.IsNullOrWhiteSpace(textureName) ? samplerName ?? string.Empty : textureName;
            string normalized = identity.Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

            return normalized switch
            {
                "diffusetexture" => 0,
                "basecolortexture" => 1,
                "albedotexture" => 2,
                "colortexture" => 3,
                "bottomtexture" => 10,
                "middletexture" => 11,
                "toptexture" => 12,
                "extrastexture" => 13,
                _ when normalized.Contains("diffuse", StringComparison.Ordinal) => 20,
                _ when normalized.Contains("albedo", StringComparison.Ordinal) => 21,
                _ when normalized.Contains("basecolor", StringComparison.Ordinal) => 22,
                _ when IsAuxiliarySampler(normalized) => 1000,
                _ => 100
            };
        }

        private static bool IsAuxiliarySampler(string identity) =>
            identity.Contains("mask", StringComparison.Ordinal) ||
            identity.Contains("normal", StringComparison.Ordinal) ||
            identity.Contains("material", StringComparison.Ordinal) ||
            identity.Contains("specular", StringComparison.Ordinal) ||
            identity.Contains("roughness", StringComparison.Ordinal) ||
            identity.Contains("metal", StringComparison.Ordinal) ||
            identity.Contains("noise", StringComparison.Ordinal) ||
            identity.Contains("depth", StringComparison.Ordinal);

        private static string ReadString(BinTreeStruct value, uint hash) =>
            value.Properties.TryGetValue(hash, out BinTreeProperty property) && property is BinTreeString text
                ? text.Value
                : string.Empty;

        private static uint ReadUInt32(BinTreeStruct value, uint hash) =>
            value.Properties.TryGetValue(hash, out BinTreeProperty property) && property is BinTreeU32 number
                ? number.Value
                : 0;
    }

    internal sealed record MapGeometryMaterialDefinition(
        string Name,
        IReadOnlyList<MapGeometryTextureSampler> Samplers,
        MapGeometryTextureSampler PrimarySampler,
        Vector4? TintColor,
        IReadOnlyDictionary<string, Vector4> Parameters,
        uint ShaderHash);

    internal sealed record MapGeometryTextureSampler(
        string TextureName,
        string SamplerName,
        string TexturePath,
        uint AddressU,
        uint AddressV,
        uint FilterMin,
        uint FilterMag);
}
