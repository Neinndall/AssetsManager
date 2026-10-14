namespace AssetsManager.Views.Models.Viewer
{
    /// <summary>
    /// Authored MapSSAOSettings for one MAP scene. Presence of this model means SSAO is enabled;
    /// missing fields have already been replaced by current LTK Manager MAIN class defaults.
    /// </summary>
    internal sealed record MapSsaoData(
        uint SampleQuality,
        float SampleRadius,
        float Bias,
        float Power,
        float Intensity,
        float BufferScale,
        bool EdgeAwareBlur)
    {
        internal int SampleCount => SampleQuality == 0 ? 4 : 8;
        internal bool DrawsAnything => Intensity > 0f && SampleRadius > 0f;
    }
}
