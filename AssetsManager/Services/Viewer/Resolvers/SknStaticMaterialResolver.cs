using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Viewer.Resolvers
{
    /// <summary>
    /// Resolves the generic StaticMaterialDef semantics used by the League client and LTK Manager.
    /// Real shader effects are handled natively by GameShaderRuntime when Shaders is enabled.
    /// </summary>
    internal static class SknStaticMaterialResolver
    {
        private const uint BlendFactorZero = 0;
        private const uint BlendFactorOne = 1;
        private const uint BlendFactorOneMinusSrcColor = 3;
        private const uint BlendFactorSrcAlpha = 6;
        private const uint BlendFactorOneMinusSrcAlpha = 7;
        private const uint DepthWriteMask = 16;
        private const uint DefaultWriteMask = 31;
        private const uint DefaultCullWinding = 1;
        private const float MaskedAlphaCutoff = 0.5f;

        private static readonly uint SwitchedShaderHash =
            Fnv1a.HashLower("Shaders/SkinnedMesh/AlphaBlend_Additive_Scroll_Packed");

        private static readonly string[] BaseExactNames =
        {
            "Diffuse_Texture",
            "DiffuseTexture",
            "Main_Texture",
            "Diffuse_Color",
            "Diffuse",
            "Base_Texture",
            "Diff_Tex",
            "_MainTex",
            "Diffuse_Texture_Primary",
            "MainItemTexture",
            "TierBaseTexture",
            "Glass_Diffuse_Texture",
            "TextureMain",
            "VoidAlbedo2",
            "BAKED_DIFFUSE_TEXTURE",
            "Diffuse_Sword_Texture",
            "Diffuse_Texture_2",
            "WP_Base_Texture"
        };

        private static readonly string[] TintNames =
        {
            "TintColor",
            "MainTex_TintColor",
            "Diffuse_Tint",
            "BaseMat_Tint",
            "TintColorBase",
            "Main_Color",
            "Diffuse_Color_Tint"
        };

        private static readonly string[] OpacityNames =
        {
            "Alpha",
            "Opacity",
            "Diffuse_AlphaIntensity",
            "Master_Alpha"
        };

        private static readonly string[] AlphaTestNames =
        {
            "AlphaTestValue",
            "AlphaClipValue",
            "Alpha_Test",
            "AlphaTest",
            "Cutoff"
        };

        private static readonly string[] UvRepeatNames =
        {
            "MainTex_Tile",
            "Diffuse_Tiling",
            "Base_Tile",
            "MainTexUV_Tile",
            "Diffuse_UV_Scale"
        };

        private static readonly string[] UvScrollNames =
        {
            "ScrollSpeedMainTex",
            "ScrollSpeedBase",
            "Diffuse_Scroll_Speed",
            "Diffuse_ScrollSpeed"
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
            @"(?i)staticmesh[\\/]defaultenv",
            RegexOptions.Compiled);

        internal static ModelMaterialDefinition Resolve(
            SknMaterialDefinition material,
            SknShaderDefinition shader,
            IReadOnlyList<string> textureKeys,
            string fallbackTextureKey) =>
            ResolveCore(
                material,
                shader,
                path => SknMaterialTextureResolver.MatchTextureKey(path, textureKeys),
                fallbackTextureKey,
                missingAsTextureOnly: true);

        /// <summary>
        /// Resolves the same StaticMaterialDef preview contract while retaining the authored
        /// base texture path. VFX resource loading needs the path rather than a skin texture key.
        /// </summary>
        internal static ModelMaterialDefinition ResolveAuthoredPreview(
            SknMaterialDefinition material,
            SknShaderDefinition shader) =>
            ResolveCore(
                material,
                shader,
                static path => path,
                fallbackTextureKey: null,
                missingAsTextureOnly: false);

        private static ModelMaterialDefinition ResolveCore(
            SknMaterialDefinition material,
            SknShaderDefinition shader,
            Func<string, string> resolveTexture,
            string fallbackTextureKey,
            bool missingAsTextureOnly)
        {
            if (material == null)
            {
                return missingAsTextureOnly
                    ? ModelMaterialDefinition.TextureOnly(fallbackTextureKey)
                    : ModelMaterialDefinition.Missing;
            }

            IReadOnlyList<SknMaterialSampler> samplers = MergeSamplers(material, shader);
            IReadOnlyDictionary<string, Vector4> parameters = MergeParameters(material, shader);
            IReadOnlyDictionary<string, bool> switches = MergeSwitches(material, shader);
            IReadOnlyDictionary<string, string> macros = MergeMacros(material, shader);
            string shaderPath = shader?.Path ?? material.ShaderPath;
            bool switchedShader = material.ShaderHash == SwitchedShaderHash ||
                (!string.IsNullOrWhiteSpace(shaderPath) &&
                 shaderPath.EndsWith(
                     "Shaders/SkinnedMesh/AlphaBlend_Additive_Scroll_Packed",
                     StringComparison.OrdinalIgnoreCase));

            (SknMaterialSampler baseSampler, ModelMaterialBaseRule baseRule) =
                SelectBaseSampler(samplers, switches, switchedShader);
            string baseTextureKey = baseSampler == null
                ? null
                : resolveTexture(baseSampler.TexturePath);

            (Vector4 color, bool hasOpacity, bool hasTint) = ResolveColor(parameters, shaderPath);
            bool hasAuthoredAlphaTest = TryFirst(parameters, AlphaTestNames, out Vector4 authoredAlphaTest) &&
                authoredAlphaTest.X > 0f && authoredAlphaTest.X < 1f;
            float alphaCutoff = ResolveAlphaCutoff(parameters, macros, shaderPath);
            Vector2 uvRepeat = baseSampler == null ? Vector2.One : ResolveUvRepeat(parameters, shaderPath);
            Vector2 uvScroll = ResolveUvScroll(parameters, shaderPath);
            ModelMaterialRenderState renderState = ResolveRenderState(
                material,
                shaderPath,
                macros,
                switches,
                switchedShader,
                hasOpacity,
                color.W,
                hasAuthoredAlphaTest,
                alphaCutoff);

            // LTK falls back to the skin texture only when the material names no base sampler at all.
            // A selected sampler whose asset is missing remains missing instead of silently drawing another texture.
            if (baseSampler == null && renderState.Blending == ModelMaterialBlendMode.Opaque)
            {
                baseTextureKey = fallbackTextureKey;
            }

            return new ModelMaterialDefinition(
                baseTextureKey,
                baseRule,
                color,
                alphaCutoff,
                uvRepeat,
                uvScroll,
                baseSampler?.WrapU ?? ModelMaterialWrapMode.Clamp,
                baseSampler?.WrapV ?? ModelMaterialWrapMode.Clamp,
                renderState,
                ModelMaterialBindingKind.Authored,
                material.IsAnimated,
                shaderPath)
            {
                HasAuthoredTint = hasTint,
                Program = material.Program
            };
        }

        private static IReadOnlyList<SknMaterialSampler> MergeSamplers(
            SknMaterialDefinition material,
            SknShaderDefinition shader)
        {
            var result = new List<SknMaterialSampler>();
            IReadOnlyList<SknMaterialSampler> shaderSamplers = shader?.DefaultSamplers ?? Array.Empty<SknMaterialSampler>();
            var shaderByName = shaderSamplers.ToDictionary(sampler => sampler.TextureName, StringComparer.Ordinal);

            foreach (SknMaterialSampler authoredSampler in material.Samplers ?? Array.Empty<SknMaterialSampler>())
            {
                SknMaterialSampler sampler = authoredSampler;
                if (authoredSampler.UsesShaderDefaultTexture &&
                    shaderByName.TryGetValue(authoredSampler.TextureName, out SknMaterialSampler shaderSampler))
                {
                    sampler = authoredSampler with
                    {
                        TexturePath = shaderSampler.TexturePath,
                        UsesShaderDefaultTexture = false
                    };
                }

                result.Add(sampler);
            }

            var authoredNames = result
                .Select(sampler => sampler.TextureName)
                .ToHashSet(StringComparer.Ordinal);
            foreach (SknMaterialSampler shaderSampler in shaderSamplers)
            {
                if (!authoredNames.Contains(shaderSampler.TextureName))
                    result.Add(shaderSampler);
            }
            return result;
        }

        private static IReadOnlyDictionary<string, Vector4> MergeParameters(
            SknMaterialDefinition material,
            SknShaderDefinition shader)
        {
            var result = new Dictionary<string, Vector4>(StringComparer.Ordinal);
            if (shader?.DefaultParameters != null)
            {
                foreach ((string name, Vector4 value) in shader.DefaultParameters)
                    result[name] = value;
            }

            bool HasDeclaration(string name) => shader == null || shader.DefaultParameters.ContainsKey(name);
            foreach ((string name, Vector4 value) in material.Pass?.Parameters ?? new Dictionary<string, Vector4>())
            {
                if (HasDeclaration(name))
                    result[name] = value;
            }
            foreach ((string name, Vector4 value) in material.Parameters ?? new Dictionary<string, Vector4>())
            {
                if (HasDeclaration(name))
                    result[name] = value;
            }
            return result;
        }

        private static IReadOnlyDictionary<string, bool> MergeSwitches(
            SknMaterialDefinition material,
            SknShaderDefinition shader)
        {
            var result = new Dictionary<string, bool>(StringComparer.Ordinal);
            if (shader?.DefaultSwitches != null)
            {
                foreach ((string name, bool value) in shader.DefaultSwitches)
                    result[name] = value;
            }

            // LTK records undeclared switches as diagnostics, but the authored state still
            // participates in the preview semantics (notably the packed switched shader).
            if (material.SwitchStates != null && material.SwitchStates.Count > 0)
            {
                foreach ((string name, bool value) in material.SwitchStates)
                    result[name] = value;
            }
            else
            {
                foreach (string name in material.Switches ?? new HashSet<string>())
                    result[name] = true;
            }
            return result;
        }

        private static IReadOnlyDictionary<string, string> MergeMacros(
            SknMaterialDefinition material,
            SknShaderDefinition shader)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach ((string name, string value) in material.ShaderMacros ?? new Dictionary<string, string>())
                result[name] = value;
            foreach ((string name, string value) in shader?.FeatureDefines ?? new Dictionary<string, string>())
                result[name] = value;
            foreach ((string name, string value) in material.Pass?.ShaderMacros ?? new Dictionary<string, string>())
                result[name] = value;
            return result;
        }

        private static (SknMaterialSampler Sampler, ModelMaterialBaseRule Rule) SelectBaseSampler(
            IReadOnlyList<SknMaterialSampler> samplers,
            IReadOnlyDictionary<string, bool> switches,
            bool switchedShader)
        {
            if (samplers == null || samplers.Count == 0)
                return (null, ModelMaterialBaseRule.None);

            bool IsUsable(SknMaterialSampler sampler) =>
                sampler != null &&
                !string.IsNullOrWhiteSpace(sampler.TexturePath) &&
                !IsPlaceholder(sampler.TexturePath);

            if (switchedShader && IsEnabled(switches, "MAINTEX_ON"))
            {
                SknMaterialSampler main = FindSampler(samplers, "Main_Texture");
                if (IsUsable(main))
                    return (main, ModelMaterialBaseRule.SwitchOverride);
            }

            SknMaterialSampler firstPlaceholder = null;
            foreach (string exactName in BaseExactNames)
            {
                SknMaterialSampler exact = FindSampler(samplers, exactName);
                if (exact == null)
                    continue;

                if (IsUsable(exact))
                    return (exact, ModelMaterialBaseRule.Exact);

                if (!string.IsNullOrWhiteSpace(exact.TexturePath) && firstPlaceholder == null)
                    firstPlaceholder = exact;
            }

            if (firstPlaceholder != null)
            {
                SknMaterialSampler rescued = samplers.FirstOrDefault(sampler =>
                    IsUsable(sampler) &&
                    !NotBaseName.IsMatch(sampler.TextureName ?? string.Empty) &&
                    IsColorMapPath(sampler.TexturePath));
                return rescued != null
                    ? (rescued, ModelMaterialBaseRule.ColorMapOverPlaceholder)
                    : (firstPlaceholder, ModelMaterialBaseRule.ExactPlaceholder);
            }

            SknMaterialSampler byName = samplers.FirstOrDefault(sampler =>
                IsUsable(sampler) &&
                BaseLikeName.IsMatch(sampler.TextureName ?? string.Empty) &&
                !NotBaseName.IsMatch(sampler.TextureName ?? string.Empty));
            if (byName != null)
                return (byName, ModelMaterialBaseRule.NameLike);

            SknMaterialSampler byPath = samplers.FirstOrDefault(sampler =>
                IsUsable(sampler) &&
                !NotBaseName.IsMatch(sampler.TextureName ?? string.Empty) &&
                IsColorMapPath(sampler.TexturePath));
            if (byPath != null)
                return (byPath, ModelMaterialBaseRule.ColorMapPath);

            SknMaterialSampler byAnyColorPath = samplers.FirstOrDefault(sampler =>
                IsUsable(sampler) &&
                IsColorMapPath(sampler.TexturePath) &&
                !NeverBaseByPath.IsMatch(sampler.TextureName ?? string.Empty));
            return byAnyColorPath != null
                ? (byAnyColorPath, ModelMaterialBaseRule.ColorMapPathAnyName)
                : (null, ModelMaterialBaseRule.None);
        }

        private static (Vector4 Color, bool HasOpacity, bool HasTint) ResolveColor(
            IReadOnlyDictionary<string, Vector4> parameters,
            string shaderPath)
        {
            Vector4 tint = Vector4.One;
            bool hasTint = TryFirst(parameters, TintNames, out Vector4 tintValue) &&
                tintValue.X >= 0f && tintValue.X <= 4f &&
                tintValue.Y >= 0f && tintValue.Y <= 4f &&
                tintValue.Z >= 0f && tintValue.Z <= 4f;
            if (hasTint)
            {
                float scale = !string.IsNullOrWhiteSpace(shaderPath) && DoubledTintShader.IsMatch(shaderPath)
                    ? 2f
                    : 1f;
                tint = new Vector4(
                    tintValue.X * scale,
                    tintValue.Y * scale,
                    tintValue.Z * scale,
                    1f);
            }

            bool hasOpacity = TryFirst(parameters, OpacityNames, out Vector4 opacityValue) &&
                opacityValue.X >= 0f && opacityValue.X <= 1f;
            if (hasOpacity)
                tint.W = opacityValue.X;

            return (tint, hasOpacity, hasTint);
        }

        private static float ResolveAlphaCutoff(
            IReadOnlyDictionary<string, Vector4> parameters,
            IReadOnlyDictionary<string, string> macros,
            string shaderPath)
        {
            if (TryFirst(parameters, AlphaTestNames, out Vector4 value) && value.X > 0f && value.X < 1f)
                return value.X;

            bool masked = (!string.IsNullOrWhiteSpace(shaderPath) && MaskedShader.IsMatch(shaderPath)) ||
                IsMacroEnabled(macros, "FEATURE_MASKED");
            return masked ? MaskedAlphaCutoff : 0f;
        }

        private static Vector2 ResolveUvRepeat(
            IReadOnlyDictionary<string, Vector4> parameters,
            string shaderPath)
        {
            if (IsShader(shaderPath, "Shaders/SkinnedMesh/Diffuse_Scrolling") &&
                parameters.TryGetValue("UV_Scale", out Vector4 diffuseScrollingScale))
            {
                var authored = new Vector2(diffuseScrollingScale.X, diffuseScrollingScale.Y);
                if (MathF.Abs(authored.X) > 0f && MathF.Abs(authored.Y) > 0f)
                    return authored;
            }

            if (!TryFirst(parameters, UvRepeatNames, out Vector4 value))
                return Vector2.One;

            var uv = new Vector2(value.X, value.Y);
            return uv != Vector2.One && MathF.Abs(uv.X) > 0f && MathF.Abs(uv.Y) > 0f
                ? uv
                : Vector2.One;
        }

        private static Vector2 ResolveUvScroll(
            IReadOnlyDictionary<string, Vector4> parameters,
            string shaderPath)
        {
            if (IsShader(shaderPath, "Shaders/SkinnedMesh/Diffuse_Scrolling"))
            {
                float x = parameters.TryGetValue("XScroll_Rate", out Vector4 xRate) ? xRate.X : 0f;
                float y = parameters.TryGetValue("YScroll_Rate", out Vector4 yRate) ? yRate.X : 0f;
                if (MathF.Abs(x) > 0f || MathF.Abs(y) > 0f)
                    return new Vector2(x, y);
            }

            if (!TryFirst(parameters, UvScrollNames, out Vector4 value))
                return Vector2.Zero;

            var uv = new Vector2(value.X, value.Y);
            return MathF.Abs(uv.X) > 0f || MathF.Abs(uv.Y) > 0f
                ? uv
                : Vector2.Zero;
        }

        private static bool IsShader(string shaderPath, string expected) =>
            string.Equals(shaderPath, expected, StringComparison.OrdinalIgnoreCase);

        private static ModelMaterialRenderState ResolveRenderState(
            SknMaterialDefinition material,
            string shaderPath,
            IReadOnlyDictionary<string, string> macros,
            IReadOnlyDictionary<string, bool> switches,
            bool switchedShader,
            bool hasOpacity,
            float opacity,
            bool hasAuthoredAlphaTest,
            float alphaCutoff)
        {
            SknMaterialPassDefinition pass = material.Pass;
            bool blendEnabled = pass?.BlendEnabled ?? false;
            uint sourceBlendFactor = pass?.SourceColorBlendFactor ?? BlendFactorOne;
            uint destinationBlendFactor = pass?.DestinationColorBlendFactor ?? BlendFactorZero;

            bool additive =
                (blendEnabled && destinationBlendFactor == BlendFactorOne) ||
                IsMacroEnabled(macros, "SKINNED_MATERIAL_ADDITIVE") ||
                (blendEnabled && !switchedShader &&
                 !string.IsNullOrWhiteSpace(shaderPath) &&
                 AdditiveShader.IsMatch(shaderPath));
            ModelMaterialBlendMode blending = additive
                ? ModelMaterialBlendMode.Additive
                : !blendEnabled
                    ? ModelMaterialBlendMode.Opaque
                    : sourceBlendFactor == BlendFactorOneMinusSrcColor &&
                      destinationBlendFactor == BlendFactorZero
                        ? ModelMaterialBlendMode.Modulate
                        : sourceBlendFactor == BlendFactorOne &&
                          destinationBlendFactor == BlendFactorZero
                            ? ModelMaterialBlendMode.Opaque
                            : ModelMaterialBlendMode.Normal;

            if (switchedShader && blending == ModelMaterialBlendMode.Normal &&
                IsEnabled(switches, "ADDITIVEALPHA_ON"))
            {
                blending = ModelMaterialBlendMode.Additive;
            }

            // Preserve authored SrcAlpha/OneMinusSrcAlpha coverage even when there is no scalar
            // Opacity slot. Some character materials (for example Seraphine skin69's cape) encode
            // the actual coverage gradient in the base texture alpha channel.
            uint writeMask = pass?.WriteMask ?? DefaultWriteMask;

            bool cullEnabled = pass?.CullEnabled ?? true;
            uint winding = pass?.WindingToCull ?? DefaultCullWinding;
            bool depthWrite = (writeMask & DepthWriteMask) != 0;
            bool cutout = blending == ModelMaterialBlendMode.Normal &&
                depthWrite &&
                hasAuthoredAlphaTest &&
                (!hasOpacity || opacity >= 1f);
            bool premultiplied = blendEnabled
                ? sourceBlendFactor == BlendFactorOne && destinationBlendFactor == BlendFactorOneMinusSrcAlpha
                : IsMacroEnabled(macros, "PREMULTIPLIED_ALPHA");

            return new ModelMaterialRenderState(
                blending,
                premultiplied,
                cutout,
                !cullEnabled,
                winding != DefaultCullWinding,
                depthWrite,
                pass?.DepthEnabled ?? true);
        }

        private static bool TryFirst(
            IReadOnlyDictionary<string, Vector4> parameters,
            IEnumerable<string> names,
            out Vector4 value)
        {
            foreach (string name in names)
            {
                if (parameters.TryGetValue(name, out value))
                    return true;
            }
            value = default;
            return false;
        }

        private static bool IsEnabled(IReadOnlyDictionary<string, bool> switches, string name) =>
            switches != null && switches.TryGetValue(name, out bool enabled) && enabled;

        private static bool IsMacroEnabled(IReadOnlyDictionary<string, string> macros, string name) =>
            macros != null && macros.TryGetValue(name, out string value) && value == "1";

        private static SknMaterialSampler FindSampler(
            IEnumerable<SknMaterialSampler> samplers,
            string name) =>
            samplers.FirstOrDefault(sampler =>
                string.Equals(sampler.TextureName, name, StringComparison.Ordinal));

        private static bool IsPlaceholder(string texturePath) =>
            !string.IsNullOrWhiteSpace(texturePath) && PlaceholderTexture.IsMatch(texturePath);

        private static bool IsColorMapPath(string texturePath)
        {
            if (string.IsNullOrWhiteSpace(texturePath))
                return false;

            string normalized = texturePath.Replace('\\', '/');
            string fileName = normalized[(normalized.LastIndexOf('/') + 1)..];
            return ColorMapPath.IsMatch(fileName);
        }
    }
}
