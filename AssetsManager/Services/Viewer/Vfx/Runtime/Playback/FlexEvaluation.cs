using System;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Runtime;

public sealed partial class VfxPlaybackRuntime
{
    internal float FlexChannel(int channel) => channel is >= 0 and < 3 ? FlexChannels[channel] : 0f;
    private float FlexTerm(VfxFlexFloat value, float phase)
        => value is null ? 0f : FlexChannel(value.Channel) * value.Value.Sample(phase);
    private float FlexScale(VfxFlexScale scale)
    {
        float factor = scale is null ? 0f : FlexChannel(scale.Channel) * scale.Value;
        return factor > 1e-5f ? factor : 1f;
    }
    private float FlexBirth(float birth, VfxFlexFloat flex, VfxCurveF baseline, float phase)
    {
        if (flex is null || FlexChannel(flex.Channel) == 0f) return birth;
        float ch = FlexChannel(flex.Channel), term = flex.Value.Sample(phase) * ch;
        bool tables = flex.Value.Prob is { Length: > 0 } p && Array.Exists(p, t => !t.IsEmpty);
        float particle = tables ? BirthScalar(flex.Value, phase) : baseline.Sample(phase) + term;
        return birth + term + ch * particle;
    }
    private Vector3 FlexBirth(Vector3 birth, VfxFlexVector3 flex, VfxCurve3? baseline, float phase)
    {
        if (flex is null || FlexChannel(flex.Channel) == 0f) return birth;
        float ch = FlexChannel(flex.Channel);
        Vector3 term = flex.Value.Sample(phase) * ch;
        bool tables = flex.Value.Prob is { Length: > 0 } p && Array.Exists(p, t => !t.IsEmpty);
        Vector3 particle = tables ? BirthVector(flex.Value, phase) : (baseline?.Sample(phase) ?? Vector3.Zero) + term;
        return birth + term + ch * particle;
    }
    private Vector2 FlexUv(VfxFlexVector2 flex, float phase, float chance)
        => flex is null ? Vector2.Zero : flex.Value.SampleBirth(phase, _rng, chance) * FlexChannel(flex.Channel);
}
