using System.Numerics;

namespace AssetsManager.Views.Models.Viewer
{
    /// <summary>
    /// Authored MapSunProperties values from one map container before viewport normalization.
    /// </summary>
    internal sealed record MapSunData(
        Vector3 Direction,
        Vector4 Color,
        float Intensity,
        Vector4 SkyColor,
        Vector4 GroundColor,
        Vector4 HorizonColor,
        float SkyScale,
        float LightMapColorScale,
        bool FogEnabled,
        Vector4 FogColor,
        Vector4 FogAlternateColor,
        Vector2 FogStartEnd,
        float FogEmissiveRemap)
    {
        public MapSunData(
            Vector3 direction,
            Vector4 color,
            float intensity,
            Vector4 skyColor,
            Vector4 groundColor,
            float skyScale)
            : this(
                direction,
                color,
                intensity,
                skyColor,
                groundColor,
                new Vector4(0.4f, 0.4f, 0.4f, 1f),
                skyScale,
                1f,
                true,
                new Vector4(0.2f, 0.2f, 0.4f, 1f),
                new Vector4(0.1f, 0.1f, 0.2f, 1f),
                new Vector2(0f, -2000f),
                1.9f)
        {
        }
    }
}
