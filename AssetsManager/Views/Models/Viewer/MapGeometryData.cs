using System;
using System.Collections.Generic;
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
        uint RegionHash)
    {
        public bool IsVisibleOnLayer(int layer)
        {
            if (layer is < 0 or > 7)
                return false;

            return (Visibility & (1 << layer)) != 0;
        }
    }

    internal sealed record MapGeometrySubmeshData(
        int StartIndex,
        int IndexCount,
        int MaterialIndex);

    internal sealed class MapGeometryData
    {
        public const int DefaultLayer = 0;

        public Vector3[] Positions { get; }
        public Vector3[] Normals { get; }
        public Vector2[] Uv0 { get; }
        public Vector2[] Uv1 { get; }
        public uint[] Indices { get; }
        public IReadOnlyList<MapGeometryMeshData> Meshes { get; }
        public IReadOnlyList<MapGeometrySubmeshData> Submeshes { get; }
        public IReadOnlyList<string> Materials { get; }
        public bool HasUv1 => Uv1 != null;

        public MapGeometryData(
            Vector3[] positions,
            Vector3[] normals,
            Vector2[] uv0,
            Vector2[] uv1,
            uint[] indices,
            IReadOnlyList<MapGeometryMeshData> meshes,
            IReadOnlyList<MapGeometrySubmeshData> submeshes,
            IReadOnlyList<string> materials)
        {
            Positions = positions ?? throw new ArgumentNullException(nameof(positions));
            Normals = normals ?? throw new ArgumentNullException(nameof(normals));
            Uv0 = uv0 ?? throw new ArgumentNullException(nameof(uv0));
            Uv1 = uv1;
            Indices = indices ?? throw new ArgumentNullException(nameof(indices));
            Meshes = meshes ?? throw new ArgumentNullException(nameof(meshes));
            Submeshes = submeshes ?? throw new ArgumentNullException(nameof(submeshes));
            Materials = materials ?? throw new ArgumentNullException(nameof(materials));
        }
    }
}
