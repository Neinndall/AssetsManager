using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Semantics
{
    /// <summary>
    /// Layer and framing semantics shared by the MAPGEO renderer and scene runtime.
    /// Mirrors LTK Manager 1.20.0 mapBuffer.ts.
    /// </summary>
    internal static class MapGeometrySemantics
    {
        private const int SampleStride = 9;
        private const float GroundRadius = 400f;

        internal static Vector3? CalculateOrigin(
            MapGeometryData geometry,
            int layer = MapGeometryData.DefaultLayer)
        {
            ArgumentNullException.ThrowIfNull(geometry);
            List<Vector3> points = SampleDrawnPoints(geometry, layer);
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
        {
            ArgumentNullException.ThrowIfNull(geometry);
            return geometry.Meshes.Where(mesh => mesh.IsVisibleOnLayer(layer));
        }

        private static List<Vector3> SampleDrawnPoints(MapGeometryData geometry, int layer)
        {
            var points = new List<Vector3>();
            foreach (MapGeometryMeshData mesh in DrawnMeshes(geometry, layer))
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
