using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Semantics
{
    /// <summary>
    /// Layer and framing semantics shared by the MAPGEO renderer and scene runtime.
    /// Mirrors the current LTK Manager MAIN map-buffer semantics.
    /// </summary>
    internal static class MapGeometrySemantics
    {
        private const int SampleStride = 9;
        private const float GroundRadius = 400f;

        internal static Vector3? CalculateOrigin(
            MapGeometryData geometry,
            int layer = MapGeometryData.DefaultLayer)
            => CalculateOriginForFlags(geometry, 1 << layer);

        internal static Vector3? CalculateOriginForFlags(
            MapGeometryData geometry,
            int flags)
        {
            ArgumentNullException.ThrowIfNull(geometry);
            List<Vector3> points = SampleDrawnPointsForFlags(geometry, flags);
            if (points.Count == 0)
                return null;

            float x = Median(points.Select(point => (double)point.X));
            float z = Median(points.Select(point => (double)point.Z));
            float reachSquared = GroundRadius * GroundRadius;
            var nearY = new List<double>();
            foreach (Vector3 point in points)
            {
                float dx = point.X - x;
                float dz = point.Z - z;
                if (dx * dx + dz * dz <= reachSquared)
                    nearY.Add(point.Y);
            }

            float y = nearY.Count == 0
                ? Median(points.Select(point => (double)point.Y))
                : Median(nearY);
            return new Vector3(x, y, z);
        }

        internal static IEnumerable<MapGeometryMeshData> DrawnMeshes(
            MapGeometryData geometry,
            int layer = MapGeometryData.DefaultLayer)
            => DrawnMeshesForFlags(geometry, 1 << layer);

        internal static IEnumerable<MapGeometryMeshData> DrawnMeshesForFlags(
            MapGeometryData geometry,
            int flags)
        {
            ArgumentNullException.ThrowIfNull(geometry);
            return geometry.Meshes.Where(mesh => mesh.IsVisibleForFlags(flags));
        }

        internal static IReadOnlyList<MapGeometryLayerData> Layers(MapGeometryData geometry)
        {
            ArgumentNullException.ThrowIfNull(geometry);
            var triangles = new int[MapGeometryData.LayerCount];
            int named = 0;
            foreach (MapGeometryMeshData mesh in geometry.Meshes)
            {
                named |= mesh.Visibility;
                int drawn = MeshTriangles(geometry, mesh);
                for (int layer = 0; layer < MapGeometryData.LayerCount; layer++)
                {
                    if (mesh.IsVisibleOnLayer(layer))
                        triangles[layer] = checked(triangles[layer] + drawn);
                }
            }

            var layers = new List<MapGeometryLayerData>();
            for (int layer = 0; layer < MapGeometryData.LayerCount; layer++)
            {
                if ((named & (1 << layer)) != 0)
                    layers.Add(new MapGeometryLayerData(layer, triangles[layer]));
            }
            return layers;
        }

        internal static int OpeningFlags(MapGeometryData geometry)
        {
            ArgumentNullException.ThrowIfNull(geometry);
            IReadOnlyList<MapGeometryLayerData> layers = Layers(geometry);
            int total = geometry.Meshes.Sum(mesh => MeshTriangles(geometry, mesh));
            MapGeometryLayerData baseLayer = layers.FirstOrDefault(layer => layer.Index == MapGeometryData.DefaultLayer);
            if (baseLayer != null && baseLayer.Triangles * 2 >= total)
                return 1 << MapGeometryData.DefaultLayer;

            MapGeometryLayerData fullest = null;
            foreach (MapGeometryLayerData layer in layers)
            {
                if (fullest == null || layer.Triangles > fullest.Triangles)
                    fullest = layer;
            }
            return fullest?.Flag ?? 0;
        }

        private static int MeshTriangles(MapGeometryData geometry, MapGeometryMeshData mesh)
        {
            int indices = 0;
            int end = Math.Min(mesh.FirstSubmesh + mesh.SubmeshCount, geometry.Submeshes.Count);
            for (int at = Math.Max(mesh.FirstSubmesh, 0); at < end; at++)
                indices = checked(indices + Math.Max(geometry.Submeshes[at].IndexCount, 0));
            return indices / 3;
        }

        private static List<Vector3> SampleDrawnPointsForFlags(MapGeometryData geometry, int flags)
        {
            var points = new List<Vector3>();
            foreach (MapGeometryMeshData mesh in DrawnMeshesForFlags(geometry, flags))
            {
                int submeshEnd = Math.Min(
                    mesh.FirstSubmesh + mesh.SubmeshCount,
                    geometry.Submeshes.Count);
                for (int at = Math.Max(mesh.FirstSubmesh, 0); at < submeshEnd; at++)
                {
                    MapGeometrySubmeshData run = geometry.Submeshes[at];
                    int end = Math.Min(run.StartIndex + run.IndexCount, geometry.Indices.Length);
                    for (int index = Math.Max(run.StartIndex, 0); index < end; index += SampleStride)
                    {
                        uint vertex = geometry.Indices[index];
                        if (vertex < geometry.Positions.Length)
                            points.Add(geometry.Positions[vertex]);
                    }
                }
            }
            return points;
        }

        private static float Median(IEnumerable<double> values)
        {
            double[] sorted = values.OrderBy(value => value).ToArray();
            return sorted.Length == 0 ? 0f : (float)sorted[sorted.Length >> 1];
        }
    }
}
