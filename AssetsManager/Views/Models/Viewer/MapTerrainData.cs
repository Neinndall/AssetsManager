using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace AssetsManager.Views.Models.Viewer
{
    /// <summary>
    /// Grass tint of one map state: the MapSkin <c>mAlternateAssets</c> entry whose
    /// <c>mVisibilityFlagName</c> names a primary visibility flag (Infernal, Ocean...).
    /// </summary>
    internal sealed record MapGrassTintAlternate(int Flag, MapTextureReference Texture);

    /// <summary>
    /// Terrain-wide inputs of the map shaders: the world rectangle the terrain maps cover
    /// (<c>TERRAIN_XFORM</c>) and the MapSkin grass tint maps sampled across it.
    /// </summary>
    internal sealed record MapTerrainData(
        Vector2 BoundsMin,
        Vector2 BoundsMax,
        MapTextureReference GrassTint,
        IReadOnlyList<MapGrassTintAlternate> GrassTintAlternates)
    {
        internal const string GrassTintKey = "terrain:grass-tint";

        internal static string AlternateGrassTintKey(int flag) => $"terrain:grass-tint:{flag}";

        /// <summary>
        /// World XZ → terrain UV scale (xy) and offset (zw); the shaders flip V themselves.
        /// </summary>
        public Vector4 TerrainTransform
        {
            get
            {
                Vector2 size = BoundsMax - BoundsMin;
                if (size.X <= 0f || size.Y <= 0f)
                    return Vector4.Zero;
                return new Vector4(
                    1f / size.X,
                    1f / size.Y,
                    -BoundsMin.X / size.X,
                    -BoundsMin.Y / size.Y);
            }
        }

        /// <summary>The alternate tint of the first active flag that declares one, if any.</summary>
        public MapGrassTintAlternate AlternateFor(int flags) =>
            GrassTintAlternates?.FirstOrDefault(alternate => (alternate.Flag & flags) != 0);

        /// <summary>Texture loads keyed like program textures, so they share the preview/full waves.</summary>
        public IEnumerable<KeyValuePair<string, MapTextureReference>> TextureRequests
        {
            get
            {
                if (GrassTint?.IsEmpty == false)
                    yield return new(GrassTintKey, GrassTint);
                foreach (MapGrassTintAlternate alternate in GrassTintAlternates ?? System.Array.Empty<MapGrassTintAlternate>())
                    if (alternate.Texture?.IsEmpty == false)
                        yield return new(AlternateGrassTintKey(alternate.Flag), alternate.Texture);
            }
        }
    }
}
