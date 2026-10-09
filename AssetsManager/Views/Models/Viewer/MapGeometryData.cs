using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LeagueToolkit.Core.Environment;

namespace AssetsManager.Views.Models.Viewer
{
    [Flags]
    internal enum MapGeometryMeshFlags : byte
    {
        None = 0,
        CullDisabled = 1 << 0,
        RegionAnchored = 1 << 1
    }

    internal sealed record MapGeometryLightChannelData(
        string Texture,
        Vector2 Scale,
        Vector2 Bias)
    {
        public bool IsEmpty => string.IsNullOrWhiteSpace(Texture);

        public static MapGeometryLightChannelData From(EnvironmentAssetChannel channel) =>
            new(channel.Texture, channel.Scale, channel.Bias);
    }

    internal sealed record MapGeometryMeshData(
        Vector3 Min,
        Vector3 Max,
        byte Visibility,
        byte Quality,
        MapGeometryMeshFlags Flags,
        int FirstSubmesh,
        int SubmeshCount,
        EnvironmentAssetMeshRenderFlags RenderFlags,
        uint VisibilityControllerPathHash,
        uint RegionHash,
        MapGeometryLightChannelData BakedLight = null,
        MapGeometryLightChannelData StationaryLight = null)
    {
        public bool IsVisibleOnLayer(int layer)
        {
            if (layer is < 0 or > 7)
                return false;

            return IsVisibleForFlags(1 << layer);
        }

        public bool IsVisibleForFlags(int flags) => Visibility == 0xff || (Visibility & flags) != 0;
    }

    internal sealed record MapGeometrySubmeshData(
        int StartIndex,
        int IndexCount,
        int MaterialIndex);

    internal sealed record MapGeometryLayerData(
        int Index,
        int Triangles)
    {
        public int Flag => 1 << Index;
    }

    internal sealed class MapGeometryData
    {
        public const int DefaultLayer = 0;
        public const int LayerCount = 8;

        public Vector3[] Positions { get; }
        public Vector3[] Normals { get; }
        public Vector2[] Uv0 { get; }
        public Vector2[] Uv1 { get; }

        /// <summary>
        /// Vertex colour (RGBA 0..1) for game shaders that read COLOR0, such as the grass deform weight.
        /// Null when no mesh carries one; meshes without it hold white.
        /// </summary>
        public Vector4[] Colors { get; }

        /// <summary>
        /// TEXCOORD5 in world space: the per-blade pivot VertexDeform bends grass around. Null when no
        /// mesh carries one; meshes without it hold their own position.
        /// </summary>
        public Vector3[] Pivots { get; }

        public uint[] Indices { get; }
        public IReadOnlyList<MapGeometryMeshData> Meshes { get; }
        public IReadOnlyList<MapGeometrySubmeshData> Submeshes { get; }
        public IReadOnlyList<string> Materials { get; }
        public bool HasUv1 => Uv1 != null;
        public bool HasColors => Colors != null;
        public bool HasPivots => Pivots != null;

        public IReadOnlyList<string> Lightmaps => Meshes
            .SelectMany(mesh => new[] { mesh.BakedLight, mesh.StationaryLight })
            .Where(channel => channel?.IsEmpty == false)
            .Select(channel => channel.Texture)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        public MapGeometryData(
            Vector3[] positions,
            Vector3[] normals,
            Vector2[] uv0,
            Vector2[] uv1,
            uint[] indices,
            IReadOnlyList<MapGeometryMeshData> meshes,
            IReadOnlyList<MapGeometrySubmeshData> submeshes,
            IReadOnlyList<string> materials,
            Vector4[] colors = null,
            Vector3[] pivots = null)
        {
            Positions = positions ?? throw new ArgumentNullException(nameof(positions));
            Normals = normals ?? throw new ArgumentNullException(nameof(normals));
            Uv0 = uv0 ?? throw new ArgumentNullException(nameof(uv0));
            Uv1 = uv1;
            Indices = indices ?? throw new ArgumentNullException(nameof(indices));
            Meshes = meshes ?? throw new ArgumentNullException(nameof(meshes));
            Submeshes = submeshes ?? throw new ArgumentNullException(nameof(submeshes));
            Materials = materials ?? throw new ArgumentNullException(nameof(materials));
            Colors = colors;
            Pivots = pivots;
        }
    }
}
