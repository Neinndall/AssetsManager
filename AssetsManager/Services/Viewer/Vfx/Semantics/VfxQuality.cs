using System;
using System.Runtime.CompilerServices;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Semantics;

public static class VfxQuality
{
    private sealed class Variants { internal readonly VfxEmitterDefinition[] Values = new VfxEmitterDefinition[5]; }
    private static readonly ConditionalWeakTable<VfxEmitterDefinition, Variants> Cached = new();
    private static readonly int[] Rates = [15, 15, 20, 30, 30];
    private static readonly int[] Reach = [0, 1, 2, 3, 3];
    private static readonly float[][] Scales =
    [
        [.05f, .1f, .2f, 1, 1, 1], [.1f, .2f, .4f, 1, 1, 1],
        [.2f, .4f, .6f, 1, 1, 1], [.5f, 1, 1, 1, 1, 1], [1, 1, 1, 1, 1, 1]
    ];
    private static readonly int[] CullMasks = [0b100011, 0b100011, 0b000011, 0b010001, 0b010000];
    public static int StepRate(VfxEffectsQuality quality) => Rates[(int)quality];
    public static int SmoothingReach(VfxEffectsQuality quality) => Reach[(int)quality];
    public static float RateScale(VfxEffectsQuality quality, int importance) => importance is >= 0 and < 6 ? Scales[(int)quality][importance] : 1f;
    public static bool Culled(VfxEffectsQuality quality, int importance) => importance is >= 0 and < 6 && (CullMasks[(int)quality] & (1 << importance)) != 0;
    public static bool Distorts(VfxEffectsQuality quality) => quality >= VfxEffectsQuality.Medium;
    public static bool Shadows(VfxEffectsQuality quality) => quality >= VfxEffectsQuality.High;

    internal static VfxEmitterDefinition Apply(VfxEmitterDefinition source, VfxEffectsQuality quality)
    {
        if (source.Shimmer != null) return source;
        VfxCullReason culled = source.Culled;
        if (culled is VfxCullReason.None or VfxCullReason.Importance or VfxCullReason.Colorblind or VfxCullReason.Never)
            culled = Culled(quality, source.Importance) ? VfxCullReason.Importance
                : !source.IsSimpleEmitter && source.ColorblindVisibility == 2 ? VfxCullReason.Colorblind
                : !source.IsSimpleEmitter && source.ColorblindVisibility > 2 ? VfxCullReason.Never : VfxCullReason.None;
        bool disabled = (source.Disabled && source.Culled == VfxCullReason.None) || culled != VfxCullReason.None;
        if (source.EffectsQuality == quality && source.Culled == culled && source.Disabled == disabled) return source;
        var variants = Cached.GetValue(source, static _ => new Variants());
        lock (variants)
            return variants.Values[(int)quality] ??= source with { EffectsQuality = quality, Culled = culled, Disabled = disabled };
    }
}
