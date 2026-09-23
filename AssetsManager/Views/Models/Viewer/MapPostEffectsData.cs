using System.Numerics;

namespace AssetsManager.Views.Models.Viewer
{
    internal sealed record MapFogData(
        bool Enabled,
        Vector4 Color,
        float Start,
        float End,
        float MaxIntensity);

    internal sealed record MapDepthOfFieldData(
        bool Enabled,
        float FocalDistance,
        float InFocusWidth,
        float Coc);

    /// <summary>
    /// Authored PostEffectOptions for one MAP scene. Missing fields have already been
    /// replaced by the class defaults used by current LTK Manager MAIN.
    /// </summary>
    internal sealed record MapPostEffectsData(
        MapFogData DepthFog,
        MapFogData HeightFog,
        MapDepthOfFieldData DepthOfField)
    {
        internal bool DrawsAnything =>
            DepthFog?.Enabled == true ||
            HeightFog?.Enabled == true ||
            DepthOfField?.Enabled == true;
    }
}
