using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Resolvers
{
    internal static class SknMaterialEffectResolver
    {
        private const float Epsilon = 0.0001f;

        internal static ModelMaterialEffectDefinition Resolve(
            SknMaterialDefinition material,
            string submesh,
            IReadOnlyList<string> textureKeys,
            IEnumerable<string> submeshes)
        {
            if (IsCompositeOnsenMaterial(material))
            {
                return ApplyMaterialTint(ModelMaterialEffectDefinition.None, material);
            }

            ModelMaterialEffectDefinition effect = ResolveOverlay(
                material,
                submesh,
                textureKeys,
                submeshes);
            effect = ApplyGradientPulse(effect, material, textureKeys);
            effect = ApplyTransition(effect, material, textureKeys);
            effect = ApplyFresnel(effect, material, textureKeys);
            effect = ApplyEmission(effect, material, textureKeys);
            effect = ApplyIridescence(effect, material, textureKeys);
            return ApplyMaterialTint(ApplySimpleWave(effect, material), material);
        }

        private static ModelMaterialEffectDefinition ApplyMaterialTint(
            ModelMaterialEffectDefinition effect,
            SknMaterialDefinition material) =>
            effect with
            {
                MaterialTint = ReadVector4(
                    material.Parameters,
                    Vector4.One,
                    "TintColor",
                    "MaterialTint",
                    "ColorTint")
            };

        private static ModelMaterialEffectDefinition ResolveOverlay(
            SknMaterialDefinition material,
            string submesh,
            IReadOnlyList<string> textureKeys,
            IEnumerable<string> submeshes)
        {
            string additiveTexture = FindSamplerKey(
                material,
                textureKeys,
                "AdditiveScrollTex",
                "Scroll_Tex",
                "Scroll_Texture");
            string additiveMask = FindSamplerKey(
                material,
                textureKeys,
                "AdditiveScroll_Mask",
                "Scroll_Tex_Mask",
                "Scroll_Texture_Mask",
                "Scroll_Mask");
            // Riot uses a white neutral source in some iridescent graphs.
            // Without authored scroll/tint controls it is not an additive layer.
            if (additiveMask != null && IsWhiteIridescentPlaceholder(material))
            {
                additiveTexture = null;
            }

            float additiveStrength = ReadFloat(
                material.Parameters,
                1f,
                "AdditiveStrength_R",
                "ScrollStrength_R",
                "ScrollStrength");
            if (additiveTexture != null &&
                additiveMask != null &&
                additiveStrength > Epsilon &&
                HasAnyParameter(
                    material.Parameters,
                    "AdditiveTexScrollSpeed_R",
                    "AdditiveTexTile",
                    "ScrollSpeed_R",
                    "Scroll_Speed",
                    "ScrollSpeed",
                    "ScrollTexTile") &&
                IsEffectMaskApplicable(material.Samplers, submesh, submeshes))
            {
                return new ModelMaterialEffectDefinition(
                    ModelMaterialEffectKind.AdditiveScroll,
                    additiveTexture,
                    additiveMask,
                    ReadVector2(
                        material.Parameters,
                        Vector2.Zero,
                        "AdditiveTexScrollSpeed_R",
                        "ScrollSpeed_R",
                        "Scroll_Speed",
                        "ScrollSpeed"),
                    ReadVector2(
                        material.Parameters,
                        Vector2.One,
                        "AdditiveTexTile",
                        "ScrollTexTile",
                        "UV_Scale"),
                    ReadVector4(
                        material.Parameters,
                        Vector4.One,
                        "AdditiveScroll_ColorTint_R",
                        "AdditiveScroll_ColorTint",
                        "Scroll_Color_Tint_R",
                        "ScrollColor",
                        "ScrollColorTint"),
                    additiveStrength,
                    0f);
            }

            string flowTexture = FindSamplerKey(
                material,
                textureKeys,
                "FlowmapTex",
                "FlowMap",
                "Flow_Texture",
                "Flowmap_Texture");
            if (flowTexture == null)
            {
                return ModelMaterialEffectDefinition.None;
            }

            return new ModelMaterialEffectDefinition(
                ModelMaterialEffectKind.FlowMap,
                flowTexture,
                FindSamplerKey(
                    material,
                    textureKeys,
                    "Mask",
                    "Mask_Texture_red",
                    "Mask_Texture",
                    "FlowmapMask",
                    "Pattern_Mask",
                    "Flow_Mask"),
                ReadVector2(
                    material.Parameters,
                    new Vector2(0.1f),
                    "FlowSpeed",
                    "FlowmapSpeed",
                    "Flow_Speed"),
                Vector2.One,
                Vector4.One,
                1f,
                ReadFloat(
                    material.Parameters,
                    0.1f,
                    "FlowmapIntensity",
                    "FlowIntensity",
                    "Flow_Amount"));
        }

        private static bool IsWhiteIridescentPlaceholder(SknMaterialDefinition material)
        {
            SknMaterialSampler sampler = material.FindSampler("additivescrolltex") ??
                material.FindSampler("scrolltex") ??
                material.FindSampler("scrolltexture");
            string sourceName = SknMaterialTextureResolver.NormalizeToken(
                Path.GetFileNameWithoutExtension(sampler?.TexturePath ?? string.Empty));
            bool hasIridescence = HasSampler(material, "iridescentTex") ||
                HasSampler(material, "IridescentTex") ||
                HasSampler(material, "Iridescent_Texture") ||
                HasSampler(material, "IridescenceTex");
            return sampler != null &&
                sourceName.Equals("white", StringComparison.Ordinal) &&
                hasIridescence &&
                !HasEffectiveScrollSpeed(
                    material.Parameters,
                    "AdditiveTexScrollSpeed_R",
                    "ScrollSpeed_R",
                    "Scroll_Speed",
                    "ScrollSpeed") &&
                !HasAnyParameter(
                    material.Parameters,
                    "AdditiveScroll_ColorTint_R",
                    "AdditiveScroll_ColorTint",
                    "Scroll_Color_Tint_R",
                    "ScrollColor",
                    "ScrollColorTint");
        }

        private static ModelMaterialEffectDefinition ApplyGradientPulse(
            ModelMaterialEffectDefinition effect,
            SknMaterialDefinition material,
            IReadOnlyList<string> textureKeys)
        {
            if (effect.Kind != ModelMaterialEffectKind.None)
            {
                return effect;
            }

            string gradientTexture = FindSamplerKey(
                material,
                textureKeys,
                "Gradient_Texture",
                "Gradient",
                "GradientMap");
            string maskTexture = FindMaterialMask(material, textureKeys);
            IReadOnlyDictionary<string, Vector4> parameters = material.Parameters;
            bool hasGradientDriver = HasAnyParameter(
                parameters,
                "Pulse_Rate",
                "Pulse_Max",
                "Pulse_Offset") ||
                (material.HasSwitch("USE_ADDATIVE", "USE_ADDITIVE") &&
                 HasAnyParameter(parameters, "Scrolling_Rate", "Scrolling_Scale"));

            if (gradientTexture == null || maskTexture == null || !hasGradientDriver)
            {
                return effect;
            }

            return new ModelMaterialEffectDefinition(
                ModelMaterialEffectKind.GradientPulse,
                gradientTexture,
                maskTexture,
                ReadVector2(
                    parameters,
                    Vector2.Zero,
                    "Scrolling_Rate",
                    "Scroll_Speed"),
                ReadVector2(
                    parameters,
                    Vector2.One,
                    "Scrolling_Scale",
                    "UV_Scale"),
                ReadVector4(
                    parameters,
                    Vector4.One,
                    "Color",
                    "Gradient_Color"),
                ReadFloat(parameters, 1f, "Mask_Intensity"),
                0f)
            {
                PulseRate = ReadFloat(parameters, 0f, "Pulse_Rate"),
                PulseMax = ReadFloat(parameters, 0f, "Pulse_Max"),
                PulseOffset = ReadFloat(parameters, 0f, "Pulse_Offset"),
                GradientSharpness = ReadFloat(
                    parameters,
                    1f,
                    "Gradient_Sharpness"),
                BloomIntensity = ReadFloat(
                    parameters,
                    0f,
                    "Bloom_Intensity"),
                DissolveThreshold = ReadFloat(
                    parameters,
                    0f,
                    "Dissolve_Bias",
                    "DissolveBias"),
                DissolveSoftness = ReadDissolveSoftness(parameters)
            };
        }

        private static ModelMaterialEffectDefinition ApplyTransition(
            ModelMaterialEffectDefinition effect,
            SknMaterialDefinition material,
            IReadOnlyList<string> textureKeys)
        {
            if (effect.Kind != ModelMaterialEffectKind.None)
            {
                return effect;
            }

            string texture = FindSamplerKey(
                material,
                textureKeys,
                "Transition_PatternTexture",
                "NoiseDisturb",
                "DissolveTex",
                "Dissolve_Texture",
                "Dissolve_Gradient_Texture",
                "Noise_Texture");
            if (texture == null || !HasAnyParameter(
                    material.Parameters,
                    "Dissolve",
                    "DissolveAmount",
                    "DissolveThreshold",
                    "DissolveValue",
                    "DissolveBias",
                    "Dissolve_Bias",
                    "DissolveWidth",
                    "Dissolve_SmoothStep",
                    "Transition",
                    "TransitionAmount"))
            {
                return effect;
            }

            float dissolveThreshold = ReadFloat(
                material.Parameters,
                0.5f,
                "DissolveThreshold",
                "DissolveAmount",
                "Dissolve",
                "DissolveValue",
                "DissolveBias",
                "Dissolve_Bias",
                "TransitionAmount");
            if (!float.IsFinite(dissolveThreshold) ||
                dissolveThreshold < 0f ||
                dissolveThreshold > 1f)
            {
                return effect;
            }

            return new ModelMaterialEffectDefinition(
                ModelMaterialEffectKind.Dissolve,
                texture,
                FindSamplerKey(
                    material,
                    textureKeys,
                    "Transition_State2",
                    "DissolveMask",
                    "Mask"),
                ReadVector2(
                    material.Parameters,
                    Vector2.Zero,
                    "DissolveSpeed",
                    "Transition_Speed",
                    "NoiseSpeed"),
                Vector2.One,
                Vector4.One,
                1f,
                0f)
            {
                DissolveThreshold = dissolveThreshold,
                DissolveSoftness = ReadDissolveSoftness(material.Parameters)
            };
        }

        private static ModelMaterialEffectDefinition ApplyFresnel(
            ModelMaterialEffectDefinition effect,
            SknMaterialDefinition material,
            IReadOnlyList<string> textureKeys)
        {
            float strength = ReadFloat(
                material.Parameters,
                0f,
                "FresnelIntensity",
                "Fresnel_Strength",
                "Fresnel",
                "Fresnel_Color_Intensity");
            if (strength <= Epsilon)
            {
                return effect;
            }

            effect = effect with
            {
                Kind = effect.Kind | ModelMaterialEffectKind.Fresnel,
                MaskTextureName = effect.MaskTextureName ?? FindMaterialMask(material, textureKeys),
                FresnelColor = ReadVector4(
                    material.Parameters,
                    Vector4.One,
                    "Fresnel_Color",
                    "FresnelColor",
                    "Fresnel_ColorTint"),
                FresnelPower = ReadFloat(
                    material.Parameters,
                    2f,
                    "FresnelPower",
                    "Fresnel_Power",
                    "FresnelExponent"),
                FresnelStrength = strength
            };

            if (material.Parameters.TryGetValue("Fresnel_Noise_Tiling_Speed", out Vector4 noise))
            {
                effect = effect with
                {
                    Kind = effect.Kind | ModelMaterialEffectKind.FresnelNoise,
                    FresnelNoiseTiling = new Vector2(noise.X, noise.Y),
                    FresnelNoiseSpeed = new Vector2(noise.Z, noise.W)
                };
            }

            return effect;
        }

        private static ModelMaterialEffectDefinition ApplyIridescence(
            ModelMaterialEffectDefinition effect,
            SknMaterialDefinition material,
            IReadOnlyList<string> textureKeys)
        {
            string iridescenceTexture = FindSamplerKey(
                material,
                textureKeys,
                "iridescentTex",
                "IridescentTex",
                "Iridescent_Texture",
                "IridescenceTex");
            if (iridescenceTexture == null)
            {
                return effect;
            }

            Vector4 control = ReadVector4(
                material.Parameters,
                new Vector4(1f, 1f, 1f, 0f),
                "IridescentControl",
                "IridescenceControl");
            bool usesPulse = material.HasSwitch("IRIDESCENCE_PULSE");
            bool usesLocalizedAlpha = material.HasSwitch(
                "ALPHA_BLEND_ON",
                "USE_FRESNEL_ALPHA");
            return effect with
            {
                Kind = effect.Kind | ModelMaterialEffectKind.Iridescence,
                Iridescence = new ModelIridescenceDefinition(
                    iridescenceTexture,
                    FindSamplerKey(
                        material,
                        textureKeys,
                        "Iridescence_Mask",
                        "Iridescent_Mask",
                        "AdditiveScroll_Mask",
                        "Scroll_Tex_Mask",
                        "Scroll_Texture_Mask",
                        "Scroll_Mask",
                        "Pattern_Mask"),
                    control,
                    ReadVector2(
                        material.Parameters,
                        Vector2.Zero,
                        "Iridescence_Pulse_Speed_Min",
                        "IridescencePulseSpeedMin"),
                    ReadVector2(
                        material.Parameters,
                        Vector2.One,
                        "fresnelAlpha_minmax",
                        "Iridescence_Alpha_MinMax",
                        "IridescenceAlphaMinMax"),
                    ReadFloat(
                        material.Parameters,
                        0f,
                        "Diffuse_Fade_Mask_Value",
                        "DiffuseFadeMaskValue"),
                    usesPulse,
                    usesLocalizedAlpha)
            };
        }

        private static ModelMaterialEffectDefinition ApplyEmission(
            ModelMaterialEffectDefinition effect,
            SknMaterialDefinition material,
            IReadOnlyList<string> textureKeys)
        {
            string emissionTexture = FindSamplerKey(
                material,
                textureKeys,
                "EmissionR_DistortionG_Texture",
                "EmissionR_Texture",
                "Emission_Texture",
                "Emissive_Texture");
            if (emissionTexture != null)
            {
                bool emissionUsesRedChannel = material.Samplers.Any(sampler =>
                {
                    string normalized = SknMaterialTextureResolver.NormalizeToken(sampler.TextureName);
                    return (normalized is "emissionrdistortiongtexture" or "emissionrtexture") &&
                           SknMaterialTextureResolver.MatchTextureKey(sampler.TexturePath, textureKeys) == emissionTexture;
                });
                effect = effect with
                {
                    Kind = effect.Kind | ModelMaterialEffectKind.Emission,
                    EmissionTextureName = emissionTexture,
                    EmissionMaskTextureName = FindSamplerKey(
                        material,
                        textureKeys,
                        "EmissionMask",
                        "EmissiveMask",
                        "BloomMask",
                        "BloomMask_Texture",
                        "Outline_Bloom_Mask",
                        "Mask_Texture_red",
                        "Mask_Texture_green",
                        "Mask_Texture_blue",
                        "Mask_Texture",
                        "Mask"),
                    EmissionScrollSpeed = ReadVector2(
                        material.Parameters,
                        Vector2.Zero,
                        "VFX_ScrollTex_R_UV_Scroll_Speed",
                        "EmissionScrollSpeed",
                        "Emission_Scroll_Speed",
                        "EmissionSpeed"),
                    EmissionTiling = ReadVector2(
                        material.Parameters,
                        Vector2.One,
                        "VFX_ScrollTex_R_UV_Tile",
                        "EmissionTexTile",
                        "Emission_Tile",
                        "EmissionTiling"),
                    EmissionColor = ReadVector4(
                        material.Parameters,
                        Vector4.One,
                        "EmissionColor",
                        "EmissiveColor",
                        "VFX_ScrollTex_R_Tint",
                        "Emission_Bloom_Color",
                        "Bloom_Color",
                        "BloomColor"),
                    EmissionStrength = ReadFloat(
                        material.Parameters,
                        1f,
                        "EmissionR_Strength",
                        "EmissionStrength",
                        "EmissiveStrength",
                        "EmissionValue",
                        "Emissive_Factor",
                        "EmissiveFactor",
                        "All_Additive_Strength"),
                    EmissionChannel = emissionUsesRedChannel ? 0 : -1
                };
            }

            float intensity = ReadFloat(
                material.Parameters,
                0f,
                "Bloom_Intensity",
                "BloomStrength",
                "Bloom",
                "BloomColorIntensity",
                "BloomValue",
                "BloomIntensity");
            if (intensity <= Epsilon && emissionTexture == null)
            {
                intensity = ReadFloat(
                    material.Parameters,
                    0f,
                    "Emissive_Bloom_Strength",
                    "EmissiveFactor",
                    "Emissive_Factor",
                    "EmissionValue");
            }
            // A BIN parameter called Bloom_Intensity is not enough to reproduce the
            // authored shader. Aatrox's gradient/dissolve material, for example,
            // exposes that parameter but has no generic bloom color; treating it as
            // white emission is what washed out the wings and sword.
            return intensity <= 0.01f || !HasSupportedEmissionSignal(material)
                ? effect
                : effect with
                {
                    Kind = effect.Kind | ModelMaterialEffectKind.Bloom,
                    MaskTextureName = effect.MaskTextureName ?? FindMaterialMask(material, textureKeys),
                    BloomColor = ReadVector4(
                        material.Parameters,
                        Vector4.One,
                        "Bloom_Color",
                        "BloomColor",
                        "Emissive_Bloom_Color",
                        "EmissionColor",
                        "EmissiveColor",
                        "Bloom_TintColor",
                        "EdgeBloomColor_RGB"),
                    BloomIntensity = intensity
                };
        }

        private static bool HasSupportedEmissionSignal(SknMaterialDefinition material) =>
            HasAnyParameter(
                material.Parameters,
                "Bloom_Color",
                "BloomColor",
                "Emissive_Bloom_Color",
                "EmissionColor",
                "EmissiveColor",
                "Bloom_TintColor",
                "EdgeBloomColor_RGB") ||
            material.Samplers.Any(sampler =>
            {
                string name = SknMaterialTextureResolver.NormalizeToken(sampler.TextureName);
                // The generic renderer can consume a bloom mask, but it cannot
                // reproduce arbitrary emission/distortion samplers. Do not turn
                // those names into white bloom by inference.
                return !SknMaterialTextureResolver.IsNeutralTexturePath(sampler.TexturePath) &&
                       name.Contains("bloom");
            });

        private static float ReadDissolveSoftness(IReadOnlyDictionary<string, Vector4> parameters)
        {
            float explicitSoftness = ReadFloat(
                parameters,
                -1f,
                "DissolveSoftness",
                "DissolveEdge",
                "DissolveWidth");
            if (explicitSoftness >= 0f)
            {
                return explicitSoftness;
            }

            if (parameters.TryGetValue("Dissolve_SmoothStep", out Vector4 smoothStep))
            {
                return Math.Max(Math.Abs(smoothStep.Y - smoothStep.X) * 0.5f, 0.001f);
            }

            return 0.05f;
        }

        private static ModelMaterialEffectDefinition ApplySimpleWave(
            ModelMaterialEffectDefinition effect,
            SknMaterialDefinition material)
        {
            if (HasComplexVertexDeformation(material) || !HasAllParameters(
                    material.Parameters,
                    "Anim_Wave_Speed",
                    "Anim_Wave_Dir",
                    "Anim_Wave_Frequency",
                    "Anim_Wave_Dir_Intensity"))
            {
                return effect;
            }

            float speed = ReadFloat(material.Parameters, 0f, "Anim_Wave_Speed");
            float intensity = ReadFloat(material.Parameters, 0f, "Anim_Wave_Dir_Intensity");
            if (Math.Abs(speed) <= Epsilon || Math.Abs(intensity) <= Epsilon)
            {
                return effect;
            }

            Vector4 direction = ReadVector4(
                material.Parameters,
                new Vector4(0f, 1f, 0f, 0f),
                "Anim_Wave_Dir");
            return effect with
            {
                Kind = effect.Kind | ModelMaterialEffectKind.AnimatedWave,
                WaveDirection = new Vector3(direction.X, direction.Y, direction.Z),
                WaveSpeed = speed,
                WaveFrequency = ReadFloat(material.Parameters, 1f, "Anim_Wave_Frequency"),
                WaveIntensity = intensity
            };
        }

        private static bool HasComplexVertexDeformation(SknMaterialDefinition material) =>
            HasAnyParameter(
                material.Parameters,
                "VertexDeformFeatureStrength",
                "VertexDeformIntensity",
                "DeformIntensity",
                "DeformProtection") ||
            HasSampler(material, "DeformNoise") ||
            HasSampler(material, "DeformMask");

        private static string FindMaterialMask(
            SknMaterialDefinition material,
            IReadOnlyList<string> textureKeys) =>
            FindSamplerKey(
                material,
                textureKeys,
                "Mask",
                "Mask_Texture_red",
                "Mask_Texture_green",
                "Mask_Texture_blue",
                "Mask_Texture",
                "MaskTex",
                "FresnelMask",
                "BloomMask",
                "BloomMask_Texture",
                "Outline_Bloom_Mask",
                "Pattern_Mask",
                "Flow_Mask",
                "Scroll_Tex_Mask",
                "Scroll_Texture_Mask",
                "Scroll_Mask");

        private static bool IsEffectMaskApplicable(
            IReadOnlyList<SknMaterialSampler> samplers,
            string submesh,
            IEnumerable<string> submeshes)
        {
            SknMaterialSampler mask = samplers.FirstOrDefault(sampler =>
            {
                string normalized = SknMaterialTextureResolver.NormalizeToken(sampler.TextureName);
                return normalized is "additivescrollmask" or
                    "scrolltexmask" or
                    "scrolltexturemask" or
                    "scrollmask";
            });
            if (mask == null)
            {
                return true;
            }

            string maskName = SknMaterialTextureResolver.NormalizeToken(
                Path.GetFileNameWithoutExtension(mask.TexturePath));
            string scopedSubmesh = submeshes
                .Where(candidate => !candidate.Equals(submesh, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(candidate => maskName.Contains(candidate + "mask", StringComparison.Ordinal));
            return scopedSubmesh == null || scopedSubmesh.Equals(submesh, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCompositeOnsenMaterial(SknMaterialDefinition material) =>
            HasSampler(material, "NoiseDisturb") &&
            HasSampler(material, "FlowmapTex") &&
            HasSampler(material, "WaterShape") &&
            HasSampler(material, "Transition_State2");

        private static bool HasSampler(
            SknMaterialDefinition material,
            string samplerName)
        {
            string expected = SknMaterialTextureResolver.NormalizeToken(samplerName);
            return material.FindSampler(expected) != null;
        }

        private static string FindSamplerKey(
            SknMaterialDefinition material,
            IReadOnlyList<string> textureKeys,
            params string[] samplerNames)
        {
            foreach (string samplerName in samplerNames)
            {
                string expected = SknMaterialTextureResolver.NormalizeToken(samplerName);
                SknMaterialSampler sampler = material.FindSampler(expected);
                if (sampler != null && !SknMaterialTextureResolver.IsNeutralTexturePath(sampler.TexturePath))
                {
                    string textureKey = SknMaterialTextureResolver.MatchTextureKey(sampler.TexturePath, textureKeys);
                    if (textureKey != null)
                    {
                        return textureKey;
                    }
                }
            }

            return null;
        }

        private static bool HasAnyParameter(
            IReadOnlyDictionary<string, Vector4> parameters,
            params string[] names) =>
            names.Any(parameters.ContainsKey);

        private static bool HasEffectiveScrollSpeed(
            IReadOnlyDictionary<string, Vector4> parameters,
            params string[] names) =>
            names.Any(name =>
                parameters.TryGetValue(name, out Vector4 value) &&
                (MathF.Abs(value.X) > Epsilon || MathF.Abs(value.Y) > Epsilon));

        private static bool HasAllParameters(
            IReadOnlyDictionary<string, Vector4> parameters,
            params string[] names) =>
            names.All(parameters.ContainsKey);

        private static Vector2 ReadVector2(
            IReadOnlyDictionary<string, Vector4> parameters,
            Vector2 fallback,
            params string[] names)
        {
            // Parameter lookup ignores case but intentionally preserves underscores.
            foreach (string name in names)
            {
                if (parameters.TryGetValue(name, out Vector4 value))
                {
                    return new Vector2(value.X, value.Y);
                }
            }

            return fallback;
        }

        private static Vector4 ReadVector4(
            IReadOnlyDictionary<string, Vector4> parameters,
            Vector4 fallback,
            params string[] names)
        {
            foreach (string name in names)
            {
                if (parameters.TryGetValue(name, out Vector4 value))
                {
                    return value;
                }
            }

            return fallback;
        }

        private static float ReadFloat(
            IReadOnlyDictionary<string, Vector4> parameters,
            float fallback,
            params string[] names)
        {
            foreach (string name in names)
            {
                if (parameters.TryGetValue(name, out Vector4 value))
                {
                    return value.X;
                }
            }

            return fallback;
        }
    }
}
