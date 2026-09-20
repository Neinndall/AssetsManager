using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Viewer.Resolvers
{
    internal static class SknMaterialEffectResolver
    {
        private const float Epsilon = 0.0001f;
        private static readonly uint ScrollingMaskedDiffuseBloomShader =
            Fnv1a.HashLower("Shaders/SkinnedMesh/ScrollingMaskedDiffuseBloom");
        private static readonly string[] MaterialMaskSamplerNames =
        {
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
            "Scroll_Mask"
        };
        private static readonly string[] IridescenceMaskSamplerNames =
        {
            "Iridescence_Mask",
            "Iridescent_Mask",
            "AdditiveScroll_Mask",
            "Scroll_Tex_Mask",
            "Scroll_Texture_Mask",
            "Scroll_Mask",
            "Pattern_Mask"
        };

        internal static ModelMaterialEffectDefinition Resolve(
            SknMaterialDefinition material,
            string submesh,
            IReadOnlyList<string> textureKeys,
            IEnumerable<string> submeshes)
        {
            ModelMaterialEffectDefinition effect = ModelMaterialEffectDefinition.None;
            effect = ApplyOverlay(effect, material, submesh, textureKeys, submeshes);
            effect = ApplyGradientPulse(effect, material, textureKeys);
            effect = ApplyTransition(effect, material, textureKeys);
            effect = ApplyFresnel(effect, material, textureKeys);
            effect = ApplyDistortion(effect, material, textureKeys);
            effect = ApplyEmission(effect, material, textureKeys);
            effect = ApplyIridescence(effect, material, textureKeys);
            effect = ApplyVertexDeformation(effect, material, textureKeys);
            effect = ApplySimpleWave(effect, material);
            effect = ApplySpecializedBaseColor(effect, material);
            return ApplyTextureSampling(effect, material, textureKeys);
        }

        private static ModelMaterialEffectDefinition ApplySpecializedBaseColor(
            ModelMaterialEffectDefinition effect,
            SknMaterialDefinition material)
        {
            bool hasGlassColor =
                material.Parameters.ContainsKey("Glass_Color1") ||
                material.Parameters.ContainsKey("GlassColor1") ||
                material.Parameters.ContainsKey("Glass_Color") ||
                material.Parameters.ContainsKey("GlassColor");
            bool hasGlassAlpha =
                material.Parameters.ContainsKey("Alpha_Bias") ||
                material.Parameters.ContainsKey("AlphaBias") ||
                material.Parameters.ContainsKey("Glass_Alpha") ||
                material.Parameters.ContainsKey("Transparency");
            if (!hasGlassColor && !hasGlassAlpha)
                return effect;

            Vector4 glassColor = ReadVector4(
                material.Parameters,
                Vector4.One,
                "Glass_Color1",
                "GlassColor1",
                "Glass_Color",
                "GlassColor");
            float alpha = ReadFloat(
                material.Parameters,
                1f,
                "Alpha_Bias",
                "AlphaBias",
                "Glass_Alpha",
                "Transparency");
            float finalAlpha = material.Parameters.ContainsKey("Alpha_Bias") || material.Parameters.ContainsKey("AlphaBias")
                ? Math.Clamp(alpha, 0.01f, 0.95f)
                : (glassColor.W > 0f && glassColor.W < 1f ? glassColor.W : 1f);

            return effect with
            {
                MaterialTint = new Vector4(glassColor.X, glassColor.Y, glassColor.Z, finalAlpha)
            };
        }

        private static ModelMaterialEffectDefinition ApplyOverlay(
            ModelMaterialEffectDefinition effect,
            SknMaterialDefinition material,
            string submesh,
            IReadOnlyList<string> textureKeys,
            IEnumerable<string> submeshes)
        {
            string panningTexture = FindSamplerKey(
                material,
                textureKeys,
                "Panning_Texture",
                "PanningTex");
            if (material.ShaderHash == ScrollingMaskedDiffuseBloomShader &&
                panningTexture != null &&
                HasAnyParameter(
                    material.Parameters,
                    "Panning_Speed",
                    "PanningSpeed",
                    "Panning_Scale",
                    "PanningScale"))
            {
                effect = effect with
                {
                    AdditiveScroll = new ModelTextureLayerDefinition(
                        panningTexture,
                        null,
                        ReadVector2(
                            material.Parameters,
                            Vector2.Zero,
                            "Panning_Speed",
                            "PanningSpeed"),
                        ReadVector2(
                            material.Parameters,
                            Vector2.One,
                            "Panning_Scale",
                            "PanningScale"),
                        Vector4.One,
                        1f)
                };
            }

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
                additiveTexture = null;

            float additiveStrength = ReadFloat(
                material.Parameters,
                1f,
                "AdditiveStrength_R",
                "ScrollStrength_R",
                "ScrollStrength");
            if (effect.AdditiveScroll == null &&
                additiveTexture != null &&
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
                effect = effect with
                {
                    AdditiveScroll = new ModelTextureLayerDefinition(
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
                        0,
                        ResolveSamplerChannel(material, additiveMask, 0))
                };
            }

            string flowTexture = FindSamplerKey(
                material,
                textureKeys,
                "FlowmapTex",
                "FlowMap",
                "Flow_Texture",
                "Flowmap_Texture");
            if (flowTexture != null)
            {
                string flowMask = FindSamplerKey(
                    material,
                    textureKeys,
                    "Mask",
                    "Mask_Texture_red",
                    "Mask_Texture_green",
                    "Mask_Texture_blue",
                    "Mask_Texture",
                    "FlowmapMask",
                    "Pattern_Mask",
                    "Flow_Mask");
                effect = effect with
                {
                    FlowMap = new ModelFlowMapDefinition(
                        flowTexture,
                        flowMask,
                        ReadVector2(
                            material.Parameters,
                            new Vector2(0.1f),
                            "FlowSpeed",
                            "FlowmapSpeed",
                            "Flow_Speed"),
                        ReadVector2(
                            material.Parameters,
                            Vector2.One,
                            "FlowTiling",
                            "Flow_Tiling",
                            "FlowmapTiling"),
                        1f,
                        ReadFloat(
                            material.Parameters,
                            0.1f,
                            "FlowmapIntensity",
                            "FlowIntensity",
                            "Flow_Amount"),
                        ResolveSamplerChannel(material, flowMask, 0))
                };
            }

            return effect;
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
                return effect;

            return effect with
            {
                GradientPulse = new ModelGradientPulseDefinition(
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
                    ReadFloat(parameters, 0f, "Pulse_Rate"),
                    ReadFloat(parameters, 0f, "Pulse_Max"),
                    ReadFloat(parameters, 0f, "Pulse_Offset"),
                    ReadFloat(parameters, 1f, "Gradient_Sharpness"),
                    ReadFloat(parameters, 0f, "Bloom_Intensity"),
                    ReadFloat(parameters, 0f, "Dissolve_Bias", "DissolveBias"),
                    ReadDissolveSoftness(parameters),
                    0,
                    ResolveSamplerChannel(material, maskTexture, 0))
            };
        }

        private static ModelMaterialEffectDefinition ApplyTransition(
            ModelMaterialEffectDefinition effect,
            SknMaterialDefinition material,
            IReadOnlyList<string> textureKeys)
        {
            string patternTexture = FindSamplerKey(
                material,
                textureKeys,
                "Transition_PatternTexture",
                "NoiseDisturb",
                "DissolveTex",
                "Dissolve_Texture",
                "Dissolve_Gradient_Texture",
                "Noise_Texture");
            if (patternTexture == null || !HasAnyParameter(
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

            string stateTexture = FindSamplerKey(material, textureKeys, "Transition_State2");
            string maskTexture = FindSamplerKey(
                material,
                textureKeys,
                "DissolveMask",
                "Transition_Mask",
                "TransitionMask");
            return effect with
            {
                Dissolve = new ModelDissolveDefinition(
                    patternTexture,
                    stateTexture,
                    maskTexture,
                    ReadVector2(
                        material.Parameters,
                        Vector2.Zero,
                        "DissolveSpeed",
                        "Transition_Speed",
                        "NoiseSpeed"),
                    ReadVector2(
                        material.Parameters,
                        Vector2.One,
                        "DissolveTiling",
                        "Dissolve_Tiling",
                        "Transition_Tiling"),
                    dissolveThreshold,
                    ReadDissolveSoftness(material.Parameters),
                    ResolveSamplerChannel(material, patternTexture, 0),
                    ResolveSamplerChannel(material, maskTexture, 0))
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
                "Fresnel_Color_Intensity",
                "Fresnel_Size_Outer");
            if (strength <= Epsilon)
                return effect;

            string maskTexture = FindSamplerKey(
                material,
                textureKeys,
                "FresnelMask",
                "Fresnel_Mask",
                "FresnelMask_Texture") ?? FindMaterialMask(material, textureKeys);
            string inheritedMask = effect.AdditiveScroll?.MaskTextureName ??
                effect.FlowMap?.MaskTextureName ??
                effect.GradientPulse?.MaskTextureName;
            if (inheritedMask == null && maskTexture == null && HasAuthoredBlackMaterialMask(material))
                return effect;

            string noiseTexture = FindSamplerKey(
                material,
                textureKeys,
                "FresnelNoise",
                "Fresnel_Noise",
                "FresnelNoise_Texture",
                "Fresnel_Noise_Texture");
            Vector2 noiseTiling = Vector2.One;
            Vector2 noiseSpeed = Vector2.Zero;
            if (material.Parameters.TryGetValue("Fresnel_Noise_Tiling_Speed", out Vector4 noise))
            {
                noiseTiling = new Vector2(noise.X, noise.Y);
                noiseSpeed = new Vector2(noise.Z, noise.W);
            }

            string resolvedMask = maskTexture ?? inheritedMask;
            return effect with
            {
                Fresnel = new ModelFresnelDefinition(
                    resolvedMask,
                    noiseTexture,
                    ReadVector4(
                        material.Parameters,
                        Vector4.One,
                        "Fresnel_Color",
                        "FresnelColor",
                        "Fresnel_ColorTint",
                        "Glass_Color2",
                        "GlassColor2"),
                    ReadFloat(
                        material.Parameters,
                        2f,
                        "FresnelPower",
                        "Fresnel_Power",
                        "FresnelExponent",
                        "Fresnel_Size_Inner"),
                    strength,
                    noiseTiling,
                    noiseSpeed,
                    ResolveSamplerChannel(material, resolvedMask, 0),
                    ResolveSamplerChannel(material, noiseTexture, 0))
            };
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
            // Generic Mask holds authored coverage when no dedicated iridescence mask exists.
            string iridescenceMask = FindSamplerKey(
                material,
                textureKeys,
                IridescenceMaskSamplerNames) ?? FindMaterialMask(material, textureKeys);
            if (iridescenceMask == null)
            {
                if (HasAuthoredBlackMaterialMask(material))
                {
                    return effect;
                }

                iridescenceMask = FindAuthoredMaskName(material);
            }
            return effect with
            {
                Iridescence = new ModelIridescenceDefinition(
                    iridescenceTexture,
                    iridescenceMask,
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
                    usesLocalizedAlpha,
                    ResolveSamplerChannel(material, iridescenceMask, 0))
            };
        }

        private static ModelMaterialEffectDefinition ApplyDistortion(
            ModelMaterialEffectDefinition effect,
            SknMaterialDefinition material,
            IReadOnlyList<string> textureKeys)
        {
            string distortionTexture = FindSamplerKey(
                material,
                textureKeys,
                "Distortion_Texture",
                "DistortionTex",
                "DistortionMap",
                "Distortion_Texture_Map");

            bool compositeWaterDistortion =
                distortionTexture == null &&
                HasSampler(material, "NoiseDisturb") &&
                HasSampler(material, "FlowmapTex") &&
                HasSampler(material, "WaterShape");
            if (compositeWaterDistortion)
            {
                distortionTexture = FindSamplerKey(material, textureKeys, "NoiseDisturb");
            }

            if (distortionTexture == null)
                return effect;

            string distortionMask = FindSamplerKey(
                material,
                textureKeys,
                "DistortionMask",
                "Distortion_Mask",
                "Flow_Mask",
                "Mask_Texture_red",
                "Mask_Texture_green",
                "Mask_Texture_blue",
                "Mask_Texture",
                "Mask");

            float strength = ReadFloat(
                material.Parameters,
                compositeWaterDistortion ? 0.02f : 0.01f,
                "DistortionStrength",
                "Distortion_Strength",
                "DistortionAmount",
                "Distortion_Amount",
                "RefractionStrength",
                "Refraction_Strength");
            if (!float.IsFinite(strength) || Math.Abs(strength) <= Epsilon)
                return effect;

            return effect with
            {
                Distortion = new ModelDistortionDefinition(
                    distortionTexture,
                    distortionMask,
                    ReadVector2(
                        material.Parameters,
                        Vector2.Zero,
                        "DistortionScrollSpeed",
                        "Distortion_Scroll_Speed",
                        "DistortionSpeed",
                        "NoiseSpeed"),
                    ReadVector2(
                        material.Parameters,
                        Vector2.One,
                        "DistortionTiling",
                        "Distortion_Tiling",
                        "DistortionTile",
                        "NoiseTiling"),
                    Math.Clamp(strength, -0.25f, 0.25f),
                    ResolveSamplerChannel(material, distortionTexture, 0),
                    ResolveSecondaryDistortionChannel(material, distortionTexture),
                    ResolveSamplerChannel(material, distortionMask, 0))
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
                SknMaterialSampler emissionSampler = material.Samplers.FirstOrDefault(sampler =>
                    SknMaterialTextureResolver.MatchTextureKey(sampler.TexturePath, textureKeys) == emissionTexture &&
                    SknMaterialTextureResolver.NormalizeToken(sampler.TextureName) is
                        "emissionrdistortiongtexture" or
                        "emissionrtexture" or
                        "emissiontexture" or
                        "emissivetexture");
                string normalizedEmissionName = SknMaterialTextureResolver.NormalizeToken(
                    emissionSampler?.TextureName ?? string.Empty);
                bool emissionUsesRedChannel =
                    normalizedEmissionName is "emissionrdistortiongtexture" or "emissionrtexture";
                bool containsPackedDistortion = normalizedEmissionName == "emissionrdistortiongtexture";
                string emissionMask = FindSamplerKey(
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
                    "Mask");
                Vector2 scrollSpeed = ReadVector2(
                    material.Parameters,
                    Vector2.Zero,
                    "VFX_ScrollTex_R_UV_Scroll_Speed",
                    "EmissionScrollSpeed",
                    "Emission_Scroll_Speed",
                    "EmissionSpeed");
                Vector2 tiling = ReadVector2(
                    material.Parameters,
                    Vector2.One,
                    "VFX_ScrollTex_R_UV_Tile",
                    "EmissionTexTile",
                    "Emission_Tile",
                    "EmissionTiling");

                effect = effect with
                {
                    Emission = new ModelEmissionDefinition(
                        emissionTexture,
                        emissionMask,
                        scrollSpeed,
                        tiling,
                        ReadVector4(
                            material.Parameters,
                            Vector4.One,
                            "EmissionColor",
                            "EmissiveColor",
                            "VFX_ScrollTex_R_Tint",
                            "Emission_Bloom_Color",
                            "Bloom_Color",
                            "BloomColor"),
                        ReadFloat(
                            material.Parameters,
                            1f,
                            "EmissionR_Strength",
                            "EmissionStrength",
                            "EmissiveStrength",
                            "EmissionValue",
                            "Emissive_Factor",
                            "EmissiveFactor",
                            "All_Additive_Strength"),
                        emissionUsesRedChannel ? 0 : -1,
                        ResolveSamplerChannel(material, emissionMask, 0))
                };

                if (containsPackedDistortion && effect.Distortion == null)
                {
                    effect = effect with
                    {
                        Distortion = new ModelDistortionDefinition(
                            emissionTexture,
                            emissionMask,
                            scrollSpeed,
                            tiling,
                            Math.Clamp(ReadFloat(
                                material.Parameters,
                                0.02f,
                                "DistortionG_Strength",
                                "DistortionStrength",
                                "Distortion_Strength",
                                "DistortionAmount",
                                "Distortion_Amount"), -0.25f, 0.25f),
                            1,
                            -1,
                            ResolveSamplerChannel(material, emissionMask, 0))
                    };
                }
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
            // authored shader. Require an authored color or bloom sampler before adding it.
            if (intensity <= 0.01f || !HasSupportedEmissionSignal(material))
                return effect;

            string bloomMask = FindSamplerKey(
                material,
                textureKeys,
                "BloomMask",
                "BloomMask_Texture",
                "Outline_Bloom_Mask",
                "Mask_Texture_red",
                "Mask_Texture_green",
                "Mask_Texture_blue",
                "Mask_Texture",
                "Mask") ?? FindMaterialMask(material, textureKeys);
            return effect with
            {
                Bloom = new ModelBloomDefinition(
                    bloomMask,
                    ReadVector4(
                        material.Parameters,
                        Vector4.One,
                        "Bloom_Color",
                        "BloomColor",
                        "Emissive_Bloom_Color",
                        "EmissionColor",
                        "EmissiveColor",
                        "Bloom_TintColor",
                        "EdgeBloomColor_RGB"),
                    intensity,
                    ResolveSamplerChannel(material, bloomMask, 0))
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

        private static ModelMaterialEffectDefinition ApplyVertexDeformation(
            ModelMaterialEffectDefinition effect,
            SknMaterialDefinition material,
            IReadOnlyList<string> textureKeys)
        {
            if (!HasComplexVertexDeformation(material))
                return effect;

            string noiseTexture = FindSamplerKey(
                material,
                textureKeys,
                "DeformNoise",
                "VertexDeformNoise",
                "Vertex_Deform_Noise",
                "DeformationNoise");
            string maskTexture = FindSamplerKey(
                material,
                textureKeys,
                "DeformMask",
                "VertexDeformMask",
                "Vertex_Deform_Mask",
                "DeformationMask");
            if (noiseTexture == null ||
                (HasSampler(material, "DeformMask") && maskTexture == null))
            {
                return effect;
            }

            float intensity = ReadFloat(
                material.Parameters,
                0f,
                "VertexDeformFeatureStrength",
                "VertexDeformIntensity",
                "DeformIntensity");
            if (Math.Abs(intensity) <= Epsilon)
                return effect;

            Vector4 direction = ReadVector4(
                material.Parameters,
                new Vector4(0f, 1f, 0f, 0f),
                "DeformDirection",
                "VertexDeformDirection",
                "Anim_Wave_Dir");
            return effect with
            {
                VertexDeformation = new ModelVertexDeformationDefinition(
                    noiseTexture,
                    maskTexture,
                    new Vector3(direction.X, direction.Y, direction.Z),
                    ReadVector2(
                        material.Parameters,
                        Vector2.Zero,
                        "DeformScrollSpeed",
                        "Deform_Scroll_Speed",
                        "VertexDeformScrollSpeed"),
                    ReadVector2(
                        material.Parameters,
                        Vector2.One,
                        "DeformTiling",
                        "Deform_Tiling",
                        "VertexDeformTiling"),
                    ReadFloat(
                        material.Parameters,
                        ReadFloat(material.Parameters, 0f, "Anim_Wave_Speed"),
                        "DeformSpeed",
                        "VertexDeformSpeed"),
                    ReadFloat(
                        material.Parameters,
                        ReadFloat(material.Parameters, 1f, "Anim_Wave_Frequency"),
                        "DeformFrequency",
                        "VertexDeformFrequency"),
                    intensity,
                    ReadFloat(material.Parameters, 0f, "DeformProtection"),
                    ResolveSamplerChannel(material, noiseTexture, 0),
                    ResolveSamplerChannel(material, maskTexture, 0))
            };
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
                return effect;

            Vector4 direction = ReadVector4(
                material.Parameters,
                new Vector4(0f, 1f, 0f, 0f),
                "Anim_Wave_Dir");
            return effect with
            {
                Wave = new ModelWaveDefinition(
                    new Vector3(direction.X, direction.Y, direction.Z),
                    speed,
                    ReadFloat(material.Parameters, 1f, "Anim_Wave_Frequency"),
                    intensity)
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

        private static ModelMaterialEffectDefinition ApplyTextureSampling(
            ModelMaterialEffectDefinition effect,
            SknMaterialDefinition material,
            IReadOnlyList<string> textureKeys)
        {
            var usedTextures = effect.EnumerateTextureNames()
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (usedTextures.Count == 0)
                return effect;

            var sampling = new Dictionary<string, ModelEffectTextureSamplingDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (SknMaterialSampler sampler in material.Samplers ?? Array.Empty<SknMaterialSampler>())
            {
                string textureKey = SknMaterialTextureResolver.MatchTextureKey(sampler.TexturePath, textureKeys);
                if (textureKey == null || !usedTextures.Contains(textureKey) || sampling.ContainsKey(textureKey))
                    continue;

                sampling[textureKey] = new ModelEffectTextureSamplingDefinition(
                    sampler.WrapU,
                    sampler.WrapV);
            }

            return sampling.Count == 0
                ? effect
                : effect with { TextureSampling = sampling };
        }

        private static string FindMaterialMask(
            SknMaterialDefinition material,
            IReadOnlyList<string> textureKeys) =>
            FindSamplerKey(
                material,
                textureKeys,
                MaterialMaskSamplerNames);

        private static string FindAuthoredMaskName(SknMaterialDefinition material)
        {
            foreach (string samplerName in IridescenceMaskSamplerNames.Concat(MaterialMaskSamplerNames))
            {
                string expected = SknMaterialTextureResolver.NormalizeToken(samplerName);
                SknMaterialSampler sampler = material.FindSampler(expected);
                if (sampler != null && !string.IsNullOrWhiteSpace(sampler.TexturePath) &&
                    !SknMaterialTextureResolver.IsNeutralTexturePath(sampler.TexturePath))
                {
                    return PathUtils.TruncateAtDot(Path.GetFileNameWithoutExtension(
                        sampler.TexturePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));
                }
            }

            return null;
        }

        private static bool HasAuthoredBlackMaterialMask(SknMaterialDefinition material) =>
            material.Samplers.Any(sampler =>
                MaterialMaskSamplerNames.Any(name =>
                    SknMaterialTextureResolver.NormalizeToken(name) ==
                    SknMaterialTextureResolver.NormalizeToken(sampler.TextureName)) &&
                SknMaterialTextureResolver.IsNeutralTexturePath(sampler.TexturePath));

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
                .FirstOrDefault(candidate =>
                {
                    string token = SknMaterialTextureResolver.NormalizeToken(candidate);
                    return token.Length > 0 && maskName.Contains(token + "mask", StringComparison.Ordinal);
                });
            return scopedSubmesh == null || scopedSubmesh.Equals(submesh, StringComparison.OrdinalIgnoreCase);
        }

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
                        return textureKey;
                }
            }

            return null;
        }

        private static int ResolveSamplerChannel(
            SknMaterialDefinition material,
            string textureKey,
            int fallback)
        {
            if (string.IsNullOrWhiteSpace(textureKey))
                return fallback;

            foreach (SknMaterialSampler sampler in material.Samplers)
            {
                string matched = SknMaterialTextureResolver.MatchTextureKey(
                    sampler.TexturePath,
                    new[] { textureKey });
                if (!string.Equals(matched, textureKey, StringComparison.OrdinalIgnoreCase))
                    continue;

                string name = SknMaterialTextureResolver.NormalizeToken(sampler.TextureName);
                if (name.Contains("green", StringComparison.Ordinal) ||
                    (name.Contains("mask", StringComparison.Ordinal) && name.EndsWith("g", StringComparison.Ordinal)) ||
                    name.Contains("distortiong", StringComparison.Ordinal))
                {
                    return 1;
                }
                if (name.Contains("blue", StringComparison.Ordinal) ||
                    (name.Contains("mask", StringComparison.Ordinal) && name.EndsWith("b", StringComparison.Ordinal)))
                {
                    return 2;
                }
                if (name.Contains("alpha", StringComparison.Ordinal) ||
                    (name.Contains("mask", StringComparison.Ordinal) && name.EndsWith("a", StringComparison.Ordinal)))
                {
                    return 3;
                }
                if (name.Contains("red", StringComparison.Ordinal) ||
                    name.Contains("emissionr", StringComparison.Ordinal) ||
                    (name.Contains("mask", StringComparison.Ordinal) && name.EndsWith("r", StringComparison.Ordinal)))
                {
                    return 0;
                }
            }

            return fallback;
        }

        private static int ResolveSecondaryDistortionChannel(
            SknMaterialDefinition material,
            string textureKey)
        {
            if (string.IsNullOrWhiteSpace(textureKey))
                return -1;

            foreach (SknMaterialSampler sampler in material.Samplers)
            {
                string matched = SknMaterialTextureResolver.MatchTextureKey(
                    sampler.TexturePath,
                    new[] { textureKey });
                if (!string.Equals(matched, textureKey, StringComparison.OrdinalIgnoreCase))
                    continue;

                string name = SknMaterialTextureResolver.NormalizeToken(sampler.TextureName);
                if (name.Contains("distortiong", StringComparison.Ordinal) ||
                    name.Contains("distortionr", StringComparison.Ordinal) ||
                    name.Contains("distortionb", StringComparison.Ordinal) ||
                    name.Contains("distortiona", StringComparison.Ordinal))
                {
                    return -1;
                }
            }

            // Standalone distortion maps conventionally carry a 2D vector in RG.
            return 1;
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
