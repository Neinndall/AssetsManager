using System;
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
    /// Environment assets one MapSkin binds to the map shaders: its grass tints and the
    /// environment cube reflective materials sample (<c>ENV_CUBE</c>).
    /// </summary>
    internal sealed record MapSkinEnvironmentData(
        string Name,
        MapTextureReference GrassTint,
        IReadOnlyList<MapGrassTintAlternate> GrassTintAlternates,
        MapTextureReference EnvironmentCube)
    {
        /// <summary>The alternate tint of the first active flag that declares one, if any.</summary>
        public MapGrassTintAlternate AlternateFor(int flags) =>
            GrassTintAlternates?.FirstOrDefault(alternate => (alternate.Flag & flags) != 0);

        /// <summary>The grass tint the state draws: its alternate when it declares one, else the base tint.</summary>
        public MapTextureReference GrassTintFor(int flags) => AlternateFor(flags)?.Texture ?? GrassTint;
    }

    /// <summary>
    /// Terrain-wide inputs of the map shaders: the world rectangle the terrain maps cover
    /// (<c>TERRAIN_XFORM</c>), the container's terrain paint (<c>TERRAIN_BLEND</c>), the environment
    /// assets of every MapSkin drawing the container and the transition time of each primary flag.
    /// </summary>
    internal sealed record MapTerrainData(
        Vector2 BoundsMin,
        Vector2 BoundsMax,
        IReadOnlyList<MapSkinEnvironmentData> Skins,
        IReadOnlyDictionary<int, float> TransitionSeconds,
        MapTextureReference TerrainPaint = null)
    {
        internal const string TerrainPaintKey = "terrain:paint";

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

        /// <summary>
        /// The skin Variant selected, or the opening skin (Default, then authored order) when the
        /// name is unknown or not given.
        /// </summary>
        public MapSkinEnvironmentData SkinFor(string name)
        {
            if (Skins == null || Skins.Count == 0)
                return null;
            return (name == null ? null : Skins.FirstOrDefault(skin => string.Equals(skin.Name, name, StringComparison.OrdinalIgnoreCase))) ??
                   Skins.FirstOrDefault(skin => string.Equals(skin.Name, "default", StringComparison.OrdinalIgnoreCase)) ??
                   Skins[0];
        }

        /// <summary>
        /// Seconds a state change takes, from the flags that switch on or off: the game fades the
        /// terrain over the TransitionTime of the element being entered or left.
        /// </summary>
        public float TransitionSecondsFor(int fromFlags, int toFlags)
        {
            int changed = fromFlags ^ toFlags;
            return TransitionSeconds?
                .Where(pair => (pair.Key & changed) != 0)
                .Select(pair => pair.Value)
                .DefaultIfEmpty(0f)
                .Max() ?? 0f;
        }

        /// <summary>Program texture key of a grass tint; skins sharing one texture share the load.</summary>
        internal static string GrassTintKey(MapTextureReference texture) =>
            texture == null
                ? null
                : "terrain:grass-tint:" + (texture.PathHash != 0
                    ? texture.PathHash.ToString("x16")
                    : texture.VirtualPath?.ToLowerInvariant());

        /// <summary>
        /// Terrain paint and grass tint loads of every skin, keyed like program textures so they share the
        /// preview/full waves.
        /// </summary>
        public IEnumerable<KeyValuePair<string, MapTextureReference>> TextureRequests
        {
            get
            {
                if (TerrainPaint?.IsEmpty == false)
                    yield return new(TerrainPaintKey, TerrainPaint);

                IEnumerable<MapTextureReference> tints = (Skins ?? Array.Empty<MapSkinEnvironmentData>())
                    .SelectMany(skin => (skin.GrassTintAlternates ?? Array.Empty<MapGrassTintAlternate>())
                        .Select(alternate => alternate.Texture)
                        .Prepend(skin.GrassTint))
                    .Where(texture => texture?.IsEmpty == false)
                    .Distinct();
                foreach (MapTextureReference texture in tints)
                    yield return new(GrassTintKey(texture), texture);
            }
        }

        /// <summary>Distinct environment cubes of every skin.</summary>
        public IEnumerable<MapTextureReference> EnvironmentCubes =>
            (Skins ?? Array.Empty<MapSkinEnvironmentData>())
            .Select(skin => skin.EnvironmentCube)
            .Where(texture => texture?.IsEmpty == false)
            .Distinct();
    }
}
