using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Rendering
{
    /// <summary>Camera-visible map groups, nearest first for solids and farthest first for blending.</summary>
    internal sealed class MapGeometryDrawQueue
    {
        internal const float CullMargin = 150f;
        private float[] _depths = Array.Empty<float>();
        private readonly Comparison<MapGeometryRenderer.DrawGroup> _opaqueOrder;
        private readonly Comparison<MapGeometryRenderer.DrawGroup> _transparentOrder;
        internal List<MapGeometryRenderer.DrawGroup> Opaque { get; } = new();
        internal List<MapGeometryRenderer.DrawGroup> Transparent { get; } = new();

        internal MapGeometryDrawQueue()
        {
            _opaqueOrder = (a, b) => Compare(a, b, false);
            _transparentOrder = (a, b) => Compare(a, b, true);
        }

        internal void Prepare(MapGeometryData geometry, MapGeometryRenderer.DrawPlan plan,
            Matrix4x4 viewProjection, Vector3 eye)
        {
            if (_depths.Length != geometry.Meshes.Count) _depths = new float[geometry.Meshes.Count];
            // Backdrop vertices are mirrored in the shaders; culling stays in authored map space.
            Matrix4x4 clip = Matrix4x4.CreateScale(-1f, 1f, 1f) * viewProjection;
            Vector3 mapEye = new(-eye.X, eye.Y, eye.Z);
            Span<Vector4> planes = stackalloc Vector4[6]
            {
                new(clip.M14 + clip.M11, clip.M24 + clip.M21, clip.M34 + clip.M31, clip.M44 + clip.M41),
                new(clip.M14 - clip.M11, clip.M24 - clip.M21, clip.M34 - clip.M31, clip.M44 - clip.M41),
                new(clip.M14 + clip.M12, clip.M24 + clip.M22, clip.M34 + clip.M32, clip.M44 + clip.M42),
                new(clip.M14 - clip.M12, clip.M24 - clip.M22, clip.M34 - clip.M32, clip.M44 - clip.M42),
                new(clip.M14 + clip.M13, clip.M24 + clip.M23, clip.M34 + clip.M33, clip.M44 + clip.M43),
                new(clip.M14 - clip.M13, clip.M24 - clip.M23, clip.M34 - clip.M33, clip.M44 - clip.M43)
            };
            for (int i = 0; i < geometry.Meshes.Count; i++)
            {
                MapGeometryMeshData mesh = geometry.Meshes[i];
                Vector3 min = mesh.Min - new Vector3(CullMargin);
                Vector3 max = mesh.Max + new Vector3(CullMargin);
                float distance = Vector3.Distance(mapEye, Vector3.Clamp(mapEye, min, max));
                // Invalid bounds cannot safely reject a mesh.
                _depths[i] = !float.IsFinite(distance) || min.X > max.X || min.Y > max.Y || min.Z > max.Z
                    ? 0f : InView(min, max, planes) ? distance : float.PositiveInfinity;
            }
            Fill(plan.OpaqueGroups, Opaque, _opaqueOrder);
            Fill(plan.TransparentGroups, Transparent, _transparentOrder);
        }

        private static bool InView(Vector3 min, Vector3 max, ReadOnlySpan<Vector4> planes)
        {
            foreach (Vector4 plane in planes)
            {
                Vector3 farthest = new(plane.X >= 0 ? max.X : min.X,
                    plane.Y >= 0 ? max.Y : min.Y, plane.Z >= 0 ? max.Z : min.Z);
                if (Vector3.Dot(new Vector3(plane.X, plane.Y, plane.Z), farthest) + plane.W < 0f)
                    return false;
            }
            return true;
        }

        private float Depth(MapGeometryRenderer.DrawGroup group)
            => group.MeshIndex >= 0 && group.MeshIndex < _depths.Length ? _depths[group.MeshIndex] : 0f;

        private int Compare(MapGeometryRenderer.DrawGroup a, MapGeometryRenderer.DrawGroup b, bool transparent)
        {
            int depth = Depth(a).CompareTo(Depth(b));
            int order = depth != 0 ? depth : a.Order.CompareTo(b.Order);
            return transparent ? -order : order;
        }

        private void Fill(IReadOnlyList<MapGeometryRenderer.DrawGroup> source,
            List<MapGeometryRenderer.DrawGroup> target, Comparison<MapGeometryRenderer.DrawGroup> compare)
        {
            target.Clear();
            foreach (var group in source)
                if (!float.IsPositiveInfinity(Depth(group))) target.Add(group);
            target.Sort(compare);
        }
    }
}
