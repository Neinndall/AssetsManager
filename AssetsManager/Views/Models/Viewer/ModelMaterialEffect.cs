using System;
using System.Numerics;

namespace AssetsManager.Views.Models.Viewer
{
    public sealed record ModelIridescenceDefinition(
        string LutTextureName,
        string MaskTextureName,
        Vector4 Control,
        Vector2 PulseSpeedMin,
        Vector2 FresnelAlphaMinMax,
        float DiffuseFadeMaskValue,
        bool UsesPulse,
        bool UsesLocalizedAlpha)
    {
        public bool RequiresAlphaBlend =>
            UsesLocalizedAlpha ||
            (DiffuseFadeMaskValue > 0.0001f && FresnelAlphaMinMax.X >= 0.1f && (FresnelAlphaMinMax.X < 0.999f || FresnelAlphaMinMax.Y < 0.999f));
    }

    [Flags]
    public enum ModelMaterialEffectKind
    {
        None,
        AdditiveScroll = 1,
        FlowMap = 2,
        Fresnel = 4,
        Dissolve = 8,
        Bloom = 16,
        AnimatedWave = 32,
        FresnelNoise = 64,
        Emission = 128,
        GradientPulse = 256,
        Iridescence = 512
    }

    public sealed record ModelMaterialEffectDefinition(
        ModelMaterialEffectKind Kind,
        string TextureName,
        string MaskTextureName,
        Vector2 ScrollSpeed,
        Vector2 Tiling,
        Vector4 Color,
        float Strength,
        float FlowIntensity)
    {
        public Vector4 FresnelColor { get; init; } = Vector4.One;
        public float FresnelPower { get; init; } = 2f;
        public float FresnelStrength { get; init; }
        public float DissolveThreshold { get; init; } = 0.5f;
        public float DissolveSoftness { get; init; } = 0.05f;
        public Vector4 BloomColor { get; init; } = Vector4.One;
        public float BloomIntensity { get; init; }
        public float PulseRate { get; init; }
        public float PulseMax { get; init; }
        public float PulseOffset { get; init; }
        public float GradientSharpness { get; init; } = 1f;
        public Vector3 WaveDirection { get; init; } = Vector3.UnitY;
        public float WaveSpeed { get; init; }
        public float WaveFrequency { get; init; } = 1f;
        public float WaveIntensity { get; init; }
        public Vector2 FresnelNoiseTiling { get; init; } = Vector2.One;
        public Vector2 FresnelNoiseSpeed { get; init; }
        public string EmissionTextureName { get; init; }
        public string EmissionMaskTextureName { get; init; }
        public Vector2 EmissionScrollSpeed { get; init; }
        public Vector2 EmissionTiling { get; init; } = Vector2.One;
        public Vector4 EmissionColor { get; init; } = Vector4.One;
        public float EmissionStrength { get; init; }
        public int EmissionChannel { get; init; } = -1;
        public ModelIridescenceDefinition Iridescence { get; init; }

        public bool RequiresAlphaBlend =>
            Iridescence?.RequiresAlphaBlend == true;

        public static ModelMaterialEffectDefinition None { get; } = new(
            ModelMaterialEffectKind.None,
            null,
            null,
            Vector2.Zero,
            Vector2.One,
            Vector4.One,
            0f,
            0f);
    }
}
