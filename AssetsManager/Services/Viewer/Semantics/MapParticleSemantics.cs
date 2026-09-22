using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Viewer.Semantics
{
    /// <summary>
    /// Scene-side MapParticle rules shared by parsing, simulation and culling.
    /// </summary>
    internal static class MapParticleSemantics
    {
        internal const float SystemReach = 1500f;
        internal const float LongestStepSeconds = 0.1f;
        private static readonly Matrix4x4 ViewportMirror = Matrix4x4.CreateScale(-1f, 1f, 1f);

        public static IReadOnlyList<MapParticleData> PlayedOnLayer(
            IEnumerable<MapParticleData> particles,
            int layer)
        {
            if (particles == null)
                return Array.Empty<MapParticleData>();

            return particles
                .Where(particle =>
                    particle != null &&
                    particle.Placeable.IsVisibleOnLayer(layer) &&
                    !particle.Transitional &&
                    !particle.StartDisabled &&
                    !particle.VisibilityController.HasValue)
                .ToArray();
        }

        public static IReadOnlyList<MapParticleGroupData> GroupBySystem(
            IEnumerable<MapParticleData> particles)
        {
            if (particles == null)
                return Array.Empty<MapParticleGroupData>();

            var order = new List<uint>();
            var groups = new Dictionary<uint, List<MapParticleData>>();
            foreach (MapParticleData particle in particles)
            {
                if (particle == null)
                    continue;
                if (!groups.TryGetValue(particle.SystemHash, out List<MapParticleData> held))
                {
                    held = new List<MapParticleData>();
                    groups.Add(particle.SystemHash, held);
                    order.Add(particle.SystemHash);
                }
                held.Add(particle);
            }

            return order
                .Select(system => new MapParticleGroupData(system, groups[system]))
                .ToArray();
        }

        public static uint Seed(string name) => Fnv1a.HashLower(name ?? string.Empty);

        /// <summary>
        /// Removes authored placement scale while preserving its orientation and translation,
        /// matching LTK's particleAnchor basis normalization.
        /// </summary>
        public static Matrix4x4 RigidTransform(Matrix4x4 transform)
        {
            Vector3 right = NormalizeOr(Vector3.TransformNormal(Vector3.UnitX, transform), Vector3.UnitX);
            Vector3 up = NormalizeOr(Vector3.TransformNormal(Vector3.UnitY, transform), Vector3.UnitY);
            Vector3 forward = NormalizeOr(Vector3.TransformNormal(Vector3.UnitZ, transform), Vector3.UnitZ);

            return new Matrix4x4(
                right.X, right.Y, right.Z, 0f,
                up.X, up.Y, up.Z, 0f,
                forward.X, forward.Y, forward.Z, 0f,
                transform.M41, transform.M42, transform.M43, 1f);
        }

        public static float ClampFrameStep(float deltaSeconds)
        {
            if (!float.IsFinite(deltaSeconds) || deltaSeconds <= 0f)
                return 0f;
            return MathF.Min(deltaSeconds, LongestStepSeconds);
        }

        internal static Vector3 ViewportPosition(Vector3 enginePosition) =>
            new(-enginePosition.X, enginePosition.Y, enginePosition.Z);

        internal static Matrix4x4 ViewportView(Matrix4x4 view) => ViewportMirror * view;

        internal static Matrix4x4 ViewportViewProjection(Matrix4x4 viewProjection) =>
            ViewportMirror * viewProjection;

        internal static bool IsVisible(Matrix4x4 viewProjection, Vector3 engineCenter, float radius)
        {
            if (!float.IsFinite(radius) || radius < 0f)
                return true;

            Vector3 center = ViewportPosition(engineCenter);
            Span<Vector4> planes = stackalloc Vector4[6]
            {
                new(viewProjection.M11 + viewProjection.M14, viewProjection.M21 + viewProjection.M24, viewProjection.M31 + viewProjection.M34, viewProjection.M41 + viewProjection.M44),
                new(-viewProjection.M11 + viewProjection.M14, -viewProjection.M21 + viewProjection.M24, -viewProjection.M31 + viewProjection.M34, -viewProjection.M41 + viewProjection.M44),
                new(viewProjection.M12 + viewProjection.M14, viewProjection.M22 + viewProjection.M24, viewProjection.M32 + viewProjection.M34, viewProjection.M42 + viewProjection.M44),
                new(-viewProjection.M12 + viewProjection.M14, -viewProjection.M22 + viewProjection.M24, -viewProjection.M32 + viewProjection.M34, -viewProjection.M42 + viewProjection.M44),
                new(viewProjection.M13, viewProjection.M23, viewProjection.M33, viewProjection.M43),
                new(-viewProjection.M13 + viewProjection.M14, -viewProjection.M23 + viewProjection.M24, -viewProjection.M33 + viewProjection.M34, -viewProjection.M43 + viewProjection.M44)
            };

            foreach (Vector4 plane in planes)
            {
                float normalLength = MathF.Sqrt(plane.X * plane.X + plane.Y * plane.Y + plane.Z * plane.Z);
                if (!float.IsFinite(normalLength) || normalLength <= 1e-8f)
                    continue;
                float distance = (plane.X * center.X + plane.Y * center.Y + plane.Z * center.Z + plane.W) / normalLength;
                if (distance < -radius)
                    return false;
            }
            return true;
        }

        private static Vector3 NormalizeOr(Vector3 value, Vector3 fallback) =>
            value.LengthSquared() > 1e-12f ? Vector3.Normalize(value) : fallback;
    }
}
