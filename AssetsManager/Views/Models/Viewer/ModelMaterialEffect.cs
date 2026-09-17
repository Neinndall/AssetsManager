using System;
using System.Collections.Generic;
using System.Numerics;

namespace AssetsManager.Views.Models.Viewer
{
    public sealed record ModelEffectTextureSamplingDefinition(
        ModelMaterialWrapMode WrapU,
        ModelMaterialWrapMode WrapV);

    public sealed record ModelTextureLayerDefinition(
        string TextureName,
        string MaskTextureName,
        Vector2 ScrollSpeed,
        Vector2 Tiling,
        Vector4 Color,
        float Strength,
        int TextureChannel = -1,
        int MaskChannel = 0);

    public sealed record ModelFlowMapDefinition(
        string TextureName,
        string MaskTextureName,
        Vector2 ScrollSpeed,
        Vector2 Tiling,
        float Strength,
        float Intensity,
        int MaskChannel = 0);

    public sealed record ModelGradientPulseDefinition(
        string TextureName,
        string MaskTextureName,
        Vector2 ScrollSpeed,
        Vector2 Tiling,
        Vector4 Color,
        float Strength,
        float PulseRate,
        float PulseMax,
        float PulseOffset,
        float Sharpness,
        float BloomIntensity,
        float MaskThreshold,
        float MaskSoftness,
        int TextureChannel = 0,
        int MaskChannel = 0);

    public sealed record ModelDissolveDefinition(
        string PatternTextureName,
        string StateTextureName,
        string MaskTextureName,
        Vector2 ScrollSpeed,
        Vector2 Tiling,
        float Threshold,
        float Softness,
        int PatternChannel = 0,
        int MaskChannel = 0);

    public sealed record ModelFresnelDefinition(
        string MaskTextureName,
        string NoiseTextureName,
        Vector4 Color,
        float Power,
        float Strength,
        Vector2 NoiseTiling,
        Vector2 NoiseSpeed,
        int MaskChannel = 0,
        int NoiseChannel = 0);

    public sealed record ModelBloomDefinition(
        string MaskTextureName,
        Vector4 Color,
        float Intensity,
        int MaskChannel = 0);

    public sealed record ModelEmissionDefinition(
        string TextureName,
        string MaskTextureName,
        Vector2 ScrollSpeed,
        Vector2 Tiling,
        Vector4 Color,
        float Strength,
        int TextureChannel = -1,
        int MaskChannel = 0);

    public sealed record ModelDistortionDefinition(
        string TextureName,
        string MaskTextureName,
        Vector2 ScrollSpeed,
        Vector2 Tiling,
        float Strength,
        int ChannelX,
        int ChannelY = -1,
        int MaskChannel = 0);

    public sealed record ModelWaveDefinition(
        Vector3 Direction,
        float Speed,
        float Frequency,
        float Intensity);

    public sealed record ModelVertexDeformationDefinition(
        string NoiseTextureName,
        string MaskTextureName,
        Vector3 Direction,
        Vector2 ScrollSpeed,
        Vector2 Tiling,
        float Speed,
        float Frequency,
        float Intensity,
        float Protection,
        int NoiseChannel = 0,
        int MaskChannel = 0);

    public sealed record ModelIridescenceDefinition(
        string LutTextureName,
        string MaskTextureName,
        Vector4 Control,
        Vector2 PulseSpeedMin,
        Vector2 FresnelAlphaMinMax,
        float DiffuseFadeMaskValue,
        bool UsesPulse,
        bool UsesLocalizedAlpha,
        int MaskChannel = 0)
    {
        public bool RequiresAlphaBlend =>
            UsesLocalizedAlpha ||
            (DiffuseFadeMaskValue > 0.0001f && FresnelAlphaMinMax.X >= 0.1f &&
             (FresnelAlphaMinMax.X < 0.999f || FresnelAlphaMinMax.Y < 0.999f));
    }

    [Flags]
    public enum ModelMaterialEffectKind
    {
        None = 0,
        AdditiveScroll = 1,
        FlowMap = 2,
        Fresnel = 4,
        Dissolve = 8,
        Bloom = 16,
        AnimatedWave = 32,
        FresnelNoise = 64,
        Emission = 128,
        GradientPulse = 256,
        Iridescence = 512,
        Distortion = 1024,
        VertexDeformation = 2048
    }

    public sealed record ModelMaterialEffectDefinition
    {
        public ModelTextureLayerDefinition AdditiveScroll { get; init; }
        public ModelFlowMapDefinition FlowMap { get; init; }
        public ModelGradientPulseDefinition GradientPulse { get; init; }
        public ModelDissolveDefinition Dissolve { get; init; }
        public ModelFresnelDefinition Fresnel { get; init; }
        public ModelBloomDefinition Bloom { get; init; }
        public ModelEmissionDefinition Emission { get; init; }
        public ModelDistortionDefinition Distortion { get; init; }
        public ModelWaveDefinition Wave { get; init; }
        public ModelVertexDeformationDefinition VertexDeformation { get; init; }
        public ModelIridescenceDefinition Iridescence { get; init; }
        public IReadOnlyDictionary<string, ModelEffectTextureSamplingDefinition> TextureSampling { get; init; } =
            new Dictionary<string, ModelEffectTextureSamplingDefinition>(StringComparer.OrdinalIgnoreCase);
        public Vector4 MaterialTint { get; init; } = Vector4.One;

        public ModelMaterialEffectKind Kind
        {
            get
            {
                ModelMaterialEffectKind kind = ModelMaterialEffectKind.None;
                if (AdditiveScroll != null) kind |= ModelMaterialEffectKind.AdditiveScroll;
                if (FlowMap != null) kind |= ModelMaterialEffectKind.FlowMap;
                if (Fresnel != null)
                {
                    kind |= ModelMaterialEffectKind.Fresnel;
                    if (!string.IsNullOrWhiteSpace(Fresnel.NoiseTextureName) ||
                        Fresnel.NoiseSpeed != Vector2.Zero)
                    {
                        kind |= ModelMaterialEffectKind.FresnelNoise;
                    }
                }
                if (Dissolve != null) kind |= ModelMaterialEffectKind.Dissolve;
                if (Bloom != null) kind |= ModelMaterialEffectKind.Bloom;
                if (Wave != null) kind |= ModelMaterialEffectKind.AnimatedWave;
                if (Emission != null) kind |= ModelMaterialEffectKind.Emission;
                if (GradientPulse != null) kind |= ModelMaterialEffectKind.GradientPulse;
                if (Iridescence != null) kind |= ModelMaterialEffectKind.Iridescence;
                if (Distortion != null) kind |= ModelMaterialEffectKind.Distortion;
                if (VertexDeformation != null) kind |= ModelMaterialEffectKind.VertexDeformation;
                return kind;
            }
        }

        public bool RequiresAlphaBlend =>
            MaterialTint.W < 0.999f ||
            Iridescence?.RequiresAlphaBlend == true;

        public IEnumerable<string> EnumerateTextureNames()
        {
            yield return AdditiveScroll?.TextureName;
            yield return AdditiveScroll?.MaskTextureName;
            yield return FlowMap?.TextureName;
            yield return FlowMap?.MaskTextureName;
            yield return GradientPulse?.TextureName;
            yield return GradientPulse?.MaskTextureName;
            yield return Dissolve?.PatternTextureName;
            yield return Dissolve?.StateTextureName;
            yield return Dissolve?.MaskTextureName;
            yield return Fresnel?.MaskTextureName;
            yield return Fresnel?.NoiseTextureName;
            yield return Bloom?.MaskTextureName;
            yield return Emission?.TextureName;
            yield return Emission?.MaskTextureName;
            yield return Distortion?.TextureName;
            yield return Distortion?.MaskTextureName;
            yield return VertexDeformation?.NoiseTextureName;
            yield return VertexDeformation?.MaskTextureName;
            yield return Iridescence?.LutTextureName;
            yield return Iridescence?.MaskTextureName;
        }

        public static ModelMaterialEffectDefinition None { get; } = new();
    }
}
