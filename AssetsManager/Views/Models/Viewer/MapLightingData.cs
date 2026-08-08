using System.Numerics;

namespace AssetsManager.Views.Models.Viewer
{
    public sealed record MapLightingProfile(
        Vector3 SunDirection,
        Vector3 SunColor,
        Vector3 AmbientColor,
        float LightMapColorScale);

    public sealed record MapLightmapBinding(
        string TextureKey,
        float[] UvCoordinates);
}
