using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Viewer.Semantics
{
    internal sealed record MapMaterialSamplerData(
        string Name,
        MapTextureReference Texture,
        MapTextureWrap WrapU,
        MapTextureWrap WrapV,
        bool UsesShaderDefaultTexture = false);

    internal sealed record MapMaterialPassData(
        uint ShaderHash,
        IReadOnlyDictionary<string, Vector4> Parameters,
        IReadOnlyDictionary<string, string> ShaderMacros,
        bool? BlendEnabled,
        uint? SourceBlendFactor,
        uint? DestinationBlendFactor,
        bool? CullEnabled,
        uint? WindingToCull,
        bool? DepthEnabled,
        uint? WriteMask);

    internal sealed record MapShaderDefinitionData(
        string Path,
        IReadOnlyList<MapMaterialSamplerData> DefaultSamplers,
        IReadOnlyDictionary<string, Vector4> DefaultParameters,
        IReadOnlyDictionary<string, bool> DefaultSwitches,
        IReadOnlyDictionary<string, string> FeatureDefines,
        bool IsDeclared);

    internal static class MapMaterialSemantics
    {
        private const uint DepthWriteMask = 16;
        private const uint DefaultWriteMask = 31;
        private const uint DefaultCullWinding = 1;
        private const float MaskedAlphaCutoff = 0.5f;
        private const string SwitchedShader = "Shaders/SkinnedMesh/AlphaBlend_Additive_Scroll_Packed";
        private static readonly uint SwitchedShaderHash = Fnv1a.HashLower(SwitchedShader);

        private static readonly string[] BaseExactNames =
        {
            "Diffuse_Texture", "DiffuseTexture", "Main_Texture", "Diffuse_Color", "Diffuse",
            "Base_Texture", "Diff_Tex", "_MainTex", "Diffuse_Texture_Primary", "MainItemTexture",
            "TierBaseTexture", "Glass_Diffuse_Texture", "TextureMain", "VoidAlbedo2",
            "BAKED_DIFFUSE_TEXTURE", "Diffuse_Sword_Texture", "Diffuse_Texture_2", "WP_Base_Texture"
        };

        private static readonly string[] TintNames =
        {
            "TintColor", "MainTex_TintColor", "Diffuse_Tint", "BaseMat_Tint", "TintColorBase",
            "Main_Color", "Diffuse_Color_Tint"
        };

        private static readonly string[] OpacityNames =
        {
            "Alpha", "Opacity", "Diffuse_AlphaIntensity", "Master_Alpha"
        };

        private static readonly string[] AlphaTestNames =
        {
            "AlphaTestValue", "AlphaClipValue", "Alpha_Test", "AlphaTest", "Cutoff"
        };

        private static readonly string[] UvRepeatNames =
        {
            "MainTex_Tile", "Diffuse_Tiling", "Base_Tile", "MainTexUV_Tile", "UV_Scale",
            "Diffuse_UV_Scale"
        };

        private static readonly string[] UvScrollNames =
        {
            "ScrollSpeedMainTex", "ScrollSpeedBase", "Diffuse_Scroll_Speed", "Diffuse_ScrollSpeed"
        };

        private static readonly string[] SwitchedAlphaNames =
        {
            "ALPHABLEND_MAIN", "ALPHABLEND_BLENDMAT", "USE_MAINTEXALPHA", "ALPHACLIP_ON"
        };

        private static readonly Regex PlaceholderTexture = new(
            @"(?i)shared[\\/]materials[\\/](black|white|grey|gray|flat_normal|default|transparent|blank)|[\\/]blank\.tex$|alpha-mask\.tex$",
            RegexOptions.Compiled);
        private static readonly Regex BaseLikeName = new(
            @"(?i)(^|_)(diffuse|albedo|main|base|basecolor|diff|color)(_|$|tex|texture)",
            RegexOptions.Compiled);
        private static readonly Regex NotBaseName = new(
            @"(?i)mask|noise|gradient|gredient|ramp|matcap|normal|nrm|distort|flow|dissolve|erosion|scroll|pan|alt|secondary|swap|transition|fresnel|bloom|glow|emiss|lut|remap|outline|shadow|deform|wpo|screen|rim|spec|rma|metal|alpha|opacity|overlay|pattern|tint|blend|hold|lightness|trans_",
            RegexOptions.Compiled);
        private static readonly Regex NeverBaseByPath = new(
            @"(?i)noise|gradient|ramp|matcap|normal|nrm|distort|flow",
            RegexOptions.Compiled);
        private static readonly Regex ColorMapPath = new(
            @"(?i)(^|[_\-])(tx_cm|cm|diffuse|albedo|basecolor)([_\-.\d]|$)",
            RegexOptions.Compiled);
        private static readonly Regex MaskedShader = new(
            @"(?i)alphatest|alpha_test|cutout|masked",
            RegexOptions.Compiled);
        private static readonly Regex AdditiveShader = new(
            @"(?i)additive",
            RegexOptions.Compiled);
        private static readonly Regex DoubledTintShader = new(
            @"(?i)staticmesh/defaultenv",
            RegexOptions.Compiled);

        internal static MapMaterialDefinition Resolve(
            string name,
            uint pathHash,
            bool animated,
            MapMaterialPassData pass,
            MapShaderDefinitionData shader,
            IReadOnlyList<MapMaterialSamplerData> authoredSamplers,
            IReadOnlyDictionary<string, Vector4> authoredParameters,
            IReadOnlyDictionary<string, bool> authoredSwitches,
            IReadOnlyDictionary<string, string> materialMacros,
            IReadOnlyList<string> warnings)
        {
            IReadOnlyList<MapMaterialSamplerData> samplers = MergeSamplers(authoredSamplers, shader);
            IReadOnlyDictionary<string, Vector4> parameters = MergeParameters(
                authoredParameters,
                pass?.Parameters,
                shader);
            IReadOnlyDictionary<string, bool> switches = MergeSwitches(authoredSwitches, shader);
            IReadOnlyDictionary<string, string> macros = MergeMacros(materialMacros, pass?.ShaderMacros, shader);
            string shaderPath = shader?.Path;
            bool switchedShader = pass?.ShaderHash == SwitchedShaderHash || IsSwitchedShader(shaderPath);

            (MapMaterialSamplerData sampler, MapMaterialBaseRule rule) =
                SelectBaseSampler(samplers, switches, switchedShader);
            MapBaseTexture baseTexture = sampler?.Texture?.IsEmpty == false
                ? new MapBaseTexture(sampler.Name, sampler.Texture, rule, sampler.WrapU, sampler.WrapV)
                : null;

            Vector3? tint = ResolveTint(parameters, shaderPath);
            float? opacity = ResolveScalar(parameters, OpacityNames, value => value >= 0f && value <= 1f);
            float? authoredAlphaTest = ResolveScalar(
                parameters,
                AlphaTestNames,
                value => value > 0f && value < 1f);
            float? alphaTest = authoredAlphaTest ?? ResolveInferredAlphaTest(macros, shaderPath);
            Vector2? uvRepeat = ResolveUvRepeat(parameters);
            Vector2? uvScroll = ResolveUvScroll(parameters);

            MapMaterialRenderState renderState = ResolveRenderState(
                pass,
                macros,
                switches,
                shaderPath,
                switchedShader,
                opacity,
                alphaTest,
                authoredAlphaTest);

            return new MapMaterialDefinition(
                name,
                pathHash,
                false,
                animated,
                shaderPath,
                baseTexture,
                tint,
                opacity,
                alphaTest,
                uvRepeat,
                uvScroll,
                renderState,
                warnings ?? Array.Empty<string>());
        }

        internal static MapMaterialDefinition Missing(string name, uint pathHash) => new(
            name,
            pathHash,
            true,
            false,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            MapMaterialRenderState.Default,
            Array.Empty<string>());

        private static IReadOnlyList<MapMaterialSamplerData> MergeSamplers(
            IReadOnlyList<MapMaterialSamplerData> authored,
            MapShaderDefinitionData shader)
        {
            var result = new List<MapMaterialSamplerData>();
            IReadOnlyList<MapMaterialSamplerData> shaderSamplers = shader?.IsDeclared == true
                ? shader.DefaultSamplers ?? Array.Empty<MapMaterialSamplerData>()
                : Array.Empty<MapMaterialSamplerData>();
            var defaults = shaderSamplers
                .ToDictionary(sampler => sampler.Name, StringComparer.Ordinal);

            foreach (MapMaterialSamplerData authoredSampler in authored ?? Array.Empty<MapMaterialSamplerData>())
            {
                MapMaterialSamplerData effective = authoredSampler;
                if (authoredSampler.UsesShaderDefaultTexture &&
                    defaults.TryGetValue(authoredSampler.Name, out MapMaterialSamplerData fallback))
                {
                    effective = authoredSampler with
                    {
                        Texture = fallback.Texture,
                        UsesShaderDefaultTexture = false
                    };
                }
                result.Add(effective);
            }

            var authoredNames = result.Select(sampler => sampler.Name).ToHashSet(StringComparer.Ordinal);
            foreach (MapMaterialSamplerData fallback in shaderSamplers)
                if (!authoredNames.Contains(fallback.Name))
                    result.Add(fallback);

            return result;
        }

        private static IReadOnlyDictionary<string, Vector4> MergeParameters(
            IReadOnlyDictionary<string, Vector4> material,
            IReadOnlyDictionary<string, Vector4> pass,
            MapShaderDefinitionData shader)
        {
            var result = new Dictionary<string, Vector4>(StringComparer.Ordinal);
            if (shader?.IsDeclared == true && shader.DefaultParameters != null)
            {
                foreach ((string key, Vector4 value) in shader.DefaultParameters)
                    result[key] = value;
            }

            bool IsDeclared(string key) =>
                shader?.IsDeclared != true || shader.DefaultParameters.ContainsKey(key);
            foreach ((string key, Vector4 value) in material ?? EmptyVectorMap)
                if (IsDeclared(key))
                    result[key] = value;
            foreach ((string key, Vector4 value) in pass ?? EmptyVectorMap)
                if (IsDeclared(key))
                    result[key] = value;
            return result;
        }

        private static IReadOnlyDictionary<string, bool> MergeSwitches(
            IReadOnlyDictionary<string, bool> material,
            MapShaderDefinitionData shader)
        {
            var result = new Dictionary<string, bool>(StringComparer.Ordinal);
            if (shader?.IsDeclared == true)
            {
                foreach ((string key, bool value) in shader.DefaultSwitches ?? EmptyBoolMap)
                    result[key] = value;
            }
            foreach ((string key, bool value) in material ?? EmptyBoolMap)
                result[key] = value;
            return result;
        }

        private static IReadOnlyDictionary<string, string> MergeMacros(
            IReadOnlyDictionary<string, string> material,
            IReadOnlyDictionary<string, string> pass,
            MapShaderDefinitionData shader)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach ((string key, string value) in material ?? EmptyStringMap)
                result[key] = value;
            if (shader?.IsDeclared == true)
            {
                foreach ((string key, string value) in shader.FeatureDefines ?? EmptyStringMap)
                    result[key] = value;
            }
            foreach ((string key, string value) in pass ?? EmptyStringMap)
                result[key] = value;
            return result;
        }

        private static (MapMaterialSamplerData Sampler, MapMaterialBaseRule Rule) SelectBaseSampler(
            IReadOnlyList<MapMaterialSamplerData> samplers,
            IReadOnlyDictionary<string, bool> switches,
            bool switchedShader)
        {
            if (samplers == null || samplers.Count == 0)
                return (null, default);

            bool IsUsable(MapMaterialSamplerData sampler) =>
                sampler?.Texture?.IsEmpty == false &&
                !IsPlaceholder(sampler.Texture.VirtualPath);

            if (switchedShader && IsEnabled(switches, "MAINTEX_ON"))
            {
                MapMaterialSamplerData main = FindSampler(samplers, "Main_Texture");
                if (IsUsable(main))
                    return (main, MapMaterialBaseRule.SwitchOverride);
            }

            MapMaterialSamplerData placeholder = null;
            foreach (string exact in BaseExactNames)
            {
                MapMaterialSamplerData sampler = FindSampler(samplers, exact);
                if (sampler == null)
                    continue;
                if (IsUsable(sampler))
                    return (sampler, MapMaterialBaseRule.Exact);
                if (sampler.Texture?.IsEmpty == false && placeholder == null)
                    placeholder = sampler;
            }

            if (placeholder != null)
            {
                MapMaterialSamplerData rescued = samplers.FirstOrDefault(sampler =>
                    IsUsable(sampler) &&
                    !NotBaseName.IsMatch(sampler.Name ?? string.Empty) &&
                    IsColorMapPath(sampler.Texture?.VirtualPath));
                return rescued != null
                    ? (rescued, MapMaterialBaseRule.ColorMapOverPlaceholder)
                    : (placeholder, MapMaterialBaseRule.ExactPlaceholder);
            }

            MapMaterialSamplerData byName = samplers.FirstOrDefault(sampler =>
                IsUsable(sampler) &&
                BaseLikeName.IsMatch(sampler.Name ?? string.Empty) &&
                !NotBaseName.IsMatch(sampler.Name ?? string.Empty));
            if (byName != null)
                return (byName, MapMaterialBaseRule.NameLike);

            MapMaterialSamplerData byPath = samplers.FirstOrDefault(sampler =>
                IsUsable(sampler) &&
                !NotBaseName.IsMatch(sampler.Name ?? string.Empty) &&
                IsColorMapPath(sampler.Texture?.VirtualPath));
            if (byPath != null)
                return (byPath, MapMaterialBaseRule.ColorMapPath);

            MapMaterialSamplerData anyColorPath = samplers.FirstOrDefault(sampler =>
                IsUsable(sampler) &&
                IsColorMapPath(sampler.Texture?.VirtualPath) &&
                !NeverBaseByPath.IsMatch(sampler.Name ?? string.Empty));
            return anyColorPath != null
                ? (anyColorPath, MapMaterialBaseRule.ColorMapPathAnyName)
                : (null, default);
        }

        private static Vector3? ResolveTint(
            IReadOnlyDictionary<string, Vector4> parameters,
            string shaderPath)
        {
            if (!TryFirst(parameters, TintNames, out Vector4 value) ||
                value.X is < 0f or > 4f ||
                value.Y is < 0f or > 4f ||
                value.Z is < 0f or > 4f)
            {
                return null;
            }

            float scale = !string.IsNullOrWhiteSpace(shaderPath) && DoubledTintShader.IsMatch(shaderPath)
                ? 2f
                : 1f;
            return new Vector3(value.X * scale, value.Y * scale, value.Z * scale);
        }

        private static float? ResolveScalar(
            IReadOnlyDictionary<string, Vector4> parameters,
            IReadOnlyList<string> names,
            Func<float, bool> valid)
        {
            return TryFirst(parameters, names, out Vector4 value) && valid(value.X)
                ? value.X
                : null;
        }

        private static float? ResolveInferredAlphaTest(
            IReadOnlyDictionary<string, string> macros,
            string shaderPath)
        {
            bool masked = !string.IsNullOrWhiteSpace(shaderPath) && MaskedShader.IsMatch(shaderPath) ||
                          IsMacroEnabled(macros, "FEATURE_MASKED");
            return masked ? MaskedAlphaCutoff : null;
        }

        private static Vector2? ResolveUvRepeat(IReadOnlyDictionary<string, Vector4> parameters)
        {
            if (!TryFirst(parameters, UvRepeatNames, out Vector4 value))
                return null;
            var uv = new Vector2(value.X, value.Y);
            return uv != Vector2.One && uv.X != 0f && uv.Y != 0f ? uv : null;
        }

        private static Vector2? ResolveUvScroll(IReadOnlyDictionary<string, Vector4> parameters)
        {
            if (!TryFirst(parameters, UvScrollNames, out Vector4 value))
                return null;
            var uv = new Vector2(value.X, value.Y);
            return uv != Vector2.Zero ? uv : null;
        }

        private static MapMaterialRenderState ResolveRenderState(
            MapMaterialPassData pass,
            IReadOnlyDictionary<string, string> macros,
            IReadOnlyDictionary<string, bool> switches,
            string shaderPath,
            bool switchedShader,
            float? opacity,
            float? alphaTest,
            float? authoredAlphaTest)
        {
            bool blendEnabled = pass?.BlendEnabled ?? false;
            MapBlendFactor source = BlendFactorOf(pass?.SourceBlendFactor, MapBlendFactor.One);
            MapBlendFactor destination = BlendFactorOf(pass?.DestinationBlendFactor, MapBlendFactor.Zero);
            MapMaterialBlendMode blending = blendEnabled
                ? BlendModeOf(source, destination)
                : MapMaterialBlendMode.Opaque;

            if (IsMacroEnabled(macros, "SKINNED_MATERIAL_ADDITIVE") ||
                blendEnabled && !switchedShader &&
                !string.IsNullOrWhiteSpace(shaderPath) && AdditiveShader.IsMatch(shaderPath))
            {
                blending = MapMaterialBlendMode.Additive;
            }

            if (switchedShader && blending == MapMaterialBlendMode.Normal &&
                IsEnabled(switches, "ADDITIVEALPHA_ON"))
            {
                blending = MapMaterialBlendMode.Additive;
            }

            bool readsAlpha = opacity.HasValue ||
                              alphaTest.HasValue ||
                              switchedShader && SwitchedAlphaNames.Any(name => IsEnabled(switches, name));
            if (blending == MapMaterialBlendMode.Normal && !readsAlpha)
                blending = MapMaterialBlendMode.Opaque;

            uint writeMask = pass?.WriteMask ?? DefaultWriteMask;
            bool depthWrite = (writeMask & DepthWriteMask) != 0;
            bool cutout = blending == MapMaterialBlendMode.Normal &&
                          depthWrite &&
                          authoredAlphaTest.HasValue &&
                          (!opacity.HasValue || opacity.Value >= 1f);

            bool premultiplied = blendEnabled
                ? source == MapBlendFactor.One && destination == MapBlendFactor.OneMinusSourceAlpha
                : IsMacroEnabled(macros, "PREMULTIPLIED_ALPHA");
            bool cullEnabled = pass?.CullEnabled ?? true;
            uint winding = pass?.WindingToCull ?? DefaultCullWinding;

            return new MapMaterialRenderState(
                blending,
                source,
                destination,
                premultiplied,
                cutout,
                !cullEnabled,
                winding != DefaultCullWinding,
                depthWrite,
                pass?.DepthEnabled ?? true);
        }

        private static MapMaterialBlendMode BlendModeOf(
            MapBlendFactor source,
            MapBlendFactor destination) =>
            (source, destination) switch
            {
                (MapBlendFactor.One, MapBlendFactor.Zero) => MapMaterialBlendMode.Opaque,
                (MapBlendFactor.OneMinusSourceColor, MapBlendFactor.Zero) => MapMaterialBlendMode.Modulate,
                (_, MapBlendFactor.One) => MapMaterialBlendMode.Additive,
                _ => MapMaterialBlendMode.Normal
            };

        private static MapBlendFactor BlendFactorOf(uint? value, MapBlendFactor fallback) =>
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

        private static bool TryFirst(
            IReadOnlyDictionary<string, Vector4> values,
            IEnumerable<string> names,
            out Vector4 value)
        {
            foreach (string name in names)
                if (values != null && values.TryGetValue(name, out value))
                    return true;
            value = default;
            return false;
        }

        private static bool IsEnabled(IReadOnlyDictionary<string, bool> switches, string name) =>
            switches != null && switches.TryGetValue(name, out bool enabled) && enabled;

        private static bool IsMacroEnabled(IReadOnlyDictionary<string, string> macros, string name) =>
            macros != null && macros.TryGetValue(name, out string value) && value == "1";

        private static bool IsSwitchedShader(string shaderPath) =>
            !string.IsNullOrWhiteSpace(shaderPath) &&
            shaderPath.EndsWith(SwitchedShader, StringComparison.OrdinalIgnoreCase);

        private static MapMaterialSamplerData FindSampler(
            IEnumerable<MapMaterialSamplerData> samplers,
            string name) =>
            samplers.FirstOrDefault(sampler => string.Equals(sampler.Name, name, StringComparison.Ordinal));

        private static bool IsPlaceholder(string texturePath) =>
            !string.IsNullOrWhiteSpace(texturePath) && PlaceholderTexture.IsMatch(texturePath);

        private static bool IsColorMapPath(string texturePath)
        {
            if (string.IsNullOrWhiteSpace(texturePath))
                return false;
            string file = Path.GetFileName(texturePath.Replace('\\', '/'));
            return ColorMapPath.IsMatch(file);
        }

        private static readonly IReadOnlyDictionary<string, Vector4> EmptyVectorMap =
            new Dictionary<string, Vector4>(StringComparer.Ordinal);
        private static readonly IReadOnlyDictionary<string, bool> EmptyBoolMap =
            new Dictionary<string, bool>(StringComparer.Ordinal);
        private static readonly IReadOnlyDictionary<string, string> EmptyStringMap =
            new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
