using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Utils.Rendering
{
    /// <summary>
    /// Utility functions for evaluating and preparing shader uniform parameters for VFX emitters.
    /// </summary>
    public static class VfxShaderParameterUtils
    {
        private const float LeastFeather = 1e-4f;
        private const float DefaultSliceWidth = 1.5f;

        #region Palette Evaluation

        public static float ResolvePaletteRowNormalized(VfxPaletteDefinition palette)
        {
            if (palette is null || palette.PaletteCount <= 0) return 0f;
            float picked = palette.PaletteSelector.Sample(0f).X;
            return (picked + 0.5f) / palette.PaletteCount;
        }

        public static float SamplePaletteSelectorAtZero(VfxPaletteDefinition palette)
            => palette is null ? 0f : palette.PaletteSelector.Sample(0f).X;

        public static Vector2 ResolvePaletteScroll(VfxPaletteDefinition palette, float phase)
            => new(palette?.ScrollU?.Sample(phase) ?? 0f, palette?.ScrollV?.Sample(phase) ?? 0f);

        public static Vector4 ResolvePaletteSourceMixColor(VfxPaletteDefinition palette)
            => palette?.PaletteSourceMixColor ?? Vector4.Zero;

        public static Vector4 ResolvePaletteSelectMain(VfxPaletteDefinition palette, float phase)
        {
            Vector2 scroll = ResolvePaletteScroll(palette, phase);
            return new Vector4(ResolvePaletteRowNormalized(palette), 0f, scroll.X, scroll.Y);
        }

        #endregion

        #region Alpha Erosion Evaluation

        public static Vector4 ResolveErosionParams(VfxAlphaErosionDefinition erosion)
        {
            float width = erosion?.SliceWidth ?? DefaultSliceWidth;
            float rateIn = 1f / Math.Max(erosion?.FeatherIn ?? 0f, LeastFeather);
            float rateOut = 1f / Math.Max(erosion?.FeatherOut ?? 0f, LeastFeather);
            return new Vector4(0f, width, rateIn, rateOut);
        }

        public static Vector4 ResolveErosionTextureMixer(VfxAlphaErosionDefinition erosion, float phase)
            => erosion?.ChannelMixer?.Sample(phase) ?? Vector4.UnitW;

        #endregion

        #region Reflection & Fresnel Evaluation

        public static Vector4 ResolveFresnel(VfxReflectionDefinition reflection)
            => reflection == null
                ? new Vector4(0f, 0f, 0f, 1f)
                : new Vector4(reflection.FresnelColor.X, reflection.FresnelColor.Y, reflection.FresnelColor.Z, reflection.Fresnel);

        public static Vector4 ResolveReflection(VfxReflectionDefinition reflection)
            => reflection == null
                ? new Vector4(1f, 0f, 1f, 0f)
                : new Vector4(reflection.ReflectionFresnel, reflection.DirectOpacity, reflection.GlancingOpacity, 0f);

        public static Vector4 ResolveReflectionTint(VfxReflectionDefinition reflection)
            => reflection?.ReflectionFresnelColor ?? Vector4.One;

        #endregion

        #region Soft Particles Evaluation

        public static Vector4 ResolveSoftParticleParams(VfxSoftParticleDefinition soft)
        {
            if (soft is null) return Vector4.Zero;
            // Each begin is an independent scene gap. A non-positive beginOut disables fade-out.
            return new Vector4(
                soft.BeginIn,
                soft.BeginOut <= 0f ? 1e8f : soft.BeginOut,
                1f / MathF.Max(soft.DeltaIn, 1e-8f),
                1f / MathF.Max(soft.DeltaOut, 1e-8f));
        }

        public static Vector4 ResolveSoftParticleControl(int target)
            => target switch
            {
                1 => new Vector4(0f, 1f, 1f, 0f), // Color only.
                2 => new Vector4(1f, 0f, 0f, 1f), // Alpha only.
                _ => new Vector4(0f, 1f, 0f, 1f) // Both, including the default and unknown values.
            };

        public static bool ShouldApplySoftFade(VfxEmitterDefinition definition, bool hasDepthTexture)
            => hasDepthTexture && definition?.SoftParticle is not null;

        #endregion

        #region Misc Parameters

        public static Vector4 ResolveAlphaTestReference(VfxEmitterDefinition definition)
            => new((definition?.RenderState?.AlphaReference ?? 0) / 255f, 0f, 0f, 0f);

        public static Vector4 ResolveDistortionPower(VfxEmitterDefinition definition)
            => new(definition?.Distortion?.Strength ?? 0f, 0f, 0f, 0f);

        public static void PopulateNativeParameters(
            IDictionary<string, Vector4> parameters,
            VfxEmitterDefinition definition,
            float phase)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            ArgumentNullException.ThrowIfNull(definition);

            parameters["TEXTURE_INFO"] = new Vector4(1f, 1f, 1f, 0f);
            parameters["TEXTURE_INFO_2"] = new Vector4(1f, 1f, 1f, 0f);
            parameters["PARTICLE_DEPTH_PUSH_PULL"] = new Vector4(definition.DepthPushPull, 0f, 0f, 0f);

            if (!definition.HasResolvedCustomMaterial)
            {
                parameters["AlphaTestReferenceValue"] = ResolveAlphaTestReference(definition);
                parameters["cAlphaErosionParams"] = ResolveErosionParams(definition.AlphaErosion);
                // Particle state selects erosion channels at zero; the emitter clock only drives palette scroll here.
                parameters["cAlphaErosionTextureMixer"] = ResolveErosionTextureMixer(definition.AlphaErosion, 0f);
                parameters["cPaletteSelectMain"] = ResolvePaletteSelectMain(definition.PaletteDefinition, phase);
                parameters["cPaletteSrcMixerMain"] = ResolvePaletteSourceMixColor(definition.PaletteDefinition);
                parameters["kColorFactor"] = Vector4.One;
                parameters["cSoftParticleParams"] = ResolveSoftParticleParams(definition.SoftParticle);
                parameters["cSoftParticleControl"] = ResolveSoftParticleControl(definition.SoftParticle?.Target ?? 0);
                parameters["vFresnel"] = ResolveFresnel(definition.Reflection);
                parameters["vReflection"] = ResolveReflection(definition.Reflection);
                parameters["vReflectionFColor"] = ResolveReflectionTint(definition.Reflection);
                parameters["DistortionPower"] = ResolveDistortionPower(definition);
            }
        }

        #endregion
    }
}
