using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Session;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Rendering
{
    /// <summary>Definition-space VFX bounds used for deterministic viewport framing.</summary>
    internal readonly record struct VfxDefinitionBounds(Vector3 Min, Vector3 Max)
    {
        internal Vector3 Center => (Min + Max) * 0.5f;
        internal float Radius => MathF.Max((Max - Min).Length() * 0.5f, 1f);
    }

    internal readonly record struct VfxCameraFrame(Vector3 Position, Vector3 Target);
    internal readonly record struct VfxOrthographicFrame(Vector3 Position, Vector3 Target, float Width);

    /// <summary>
    /// Mirrors LTK's definition-based framing: rig reach and authored root-emitter spawn geometry
    /// determine the box, so framing does not fluctuate with the current particle simulation.
    /// </summary>
    internal static class VfxSystemBounds
    {
        internal const float StandingReach = VfxRigMotion.ChampionHeight * 0.5f;
        private const float CameraMargin = 1.15f;
        private const float ShapeTick = 8f;
        private const int RingSegments = 24;

        internal static VfxDefinitionBounds Calculate(
            VfxSystemDefinition system,
            VfxRigPreset rigPreset)
            => Calculate(system, VfxRigSettings.ForPreset(rigPreset));

        internal static VfxDefinitionBounds Calculate(
            VfxSystemDefinition system,
            VfxRigSettings rigSettings)
        {
            ArgumentNullException.ThrowIfNull(system);

            Matrix4x4 world = system.Transform.GetValueOrDefault(Matrix4x4.Identity);
            Matrix4x4 worldBasis = world;
            worldBasis.M41 = 0f;
            worldBasis.M42 = 0f;
            worldBasis.M43 = 0f;
            Vector3 min = new(float.PositiveInfinity);
            Vector3 max = new(float.NegativeInfinity);

            IReadOnlyList<Vector3> rigStops = RigStops(rigSettings);
            foreach (Vector3 stop in rigStops)
            {
                Vector3 stood = Vector3.Transform(stop + Vector3.UnitY * rigSettings.Height, world);
                Grow(ref min, ref max, stood - new Vector3(StandingReach));
                Grow(ref min, ref max, stood + new Vector3(StandingReach));
            }

            Vector3 rootOrigin = Vector3.Transform(
                rigStops[0] + Vector3.UnitY * rigSettings.Height,
                world);
            foreach (VfxEmitterDefinition emitter in system.Emitters)
            {
                if (emitter.Disabled) continue;
                GrowEmitter(ref min, ref max, emitter, worldBasis, rootOrigin);
            }

            if (!IsFinite(min) || !IsFinite(max))
            {
                min = new Vector3(-StandingReach, 0f, -StandingReach);
                max = new Vector3(StandingReach, VfxRigMotion.ChampionHeight, StandingReach);
            }

            return new VfxDefinitionBounds(min, max);
        }

        internal static VfxCameraFrame FramePerspective(
            VfxDefinitionBounds bounds,
            float verticalFovDegrees,
            float aspect,
            Vector3 direction)
        {
            float fov = float.IsFinite(verticalFovDegrees) && verticalFovDegrees > 0f
                ? verticalFovDegrees
                : 45f;
            float safeAspect = float.IsFinite(aspect) && aspect > 0f ? aspect : 1f;
            Vector3 safeDirection = direction.LengthSquared() > 1e-8f && IsFinite(direction)
                ? Vector3.Normalize(direction)
                : Vector3.UnitZ;

            float vertical = fov * (MathF.PI / 180f);
            float horizontal = 2f * MathF.Atan(MathF.Tan(vertical * 0.5f) * safeAspect);
            float halfAngle = MathF.Min(vertical, horizontal) * 0.5f;
            float sine = MathF.Max(MathF.Sin(halfAngle), 1e-4f);
            float distance = bounds.Radius / sine * CameraMargin;
            Vector3 target = bounds.Center;
            return new VfxCameraFrame(target + safeDirection * distance, target);
        }

        internal static Vector3 Ground(
            VfxSystemDefinition system,
            VfxRigSettings rigSettings)
        {
            ArgumentNullException.ThrowIfNull(system);
            Matrix4x4 world = system.Transform.GetValueOrDefault(Matrix4x4.Identity);
            Vector3 origin = RigStops(rigSettings)[0];
            return Vector3.Transform(origin, world);
        }

        internal static VfxOrthographicFrame FrameOrthographic(
            VfxDefinitionBounds bounds,
            float viewportWidth,
            float viewportHeight,
            Vector3 direction)
        {
            Vector3 safeDirection = direction.LengthSquared() > 1e-8f && IsFinite(direction)
                ? Vector3.Normalize(direction)
                : Vector3.UnitZ;
            float radius = bounds.Radius;
            float across = MathF.Min(MathF.Max(0f, viewportWidth), MathF.Max(0f, viewportHeight));
            float zoom = across > 0f ? across / (2f * radius * CameraMargin) : 1f;
            float width = zoom > 0f ? MathF.Max(1f, viewportWidth) / zoom : radius * 2f * CameraMargin;
            float distance = MathF.Min(radius * 4f, 10000f);
            Vector3 target = bounds.Center;
            return new VfxOrthographicFrame(target + safeDirection * distance, target, width);
        }

        private static IReadOnlyList<Vector3> RigStops(VfxRigSettings settings)
            => settings.MotionKind switch
            {
                VfxRigMotionKind.Path => new[]
                {
                    new Vector3(-settings.FlightRange * 0.5f, 0f, 0f),
                    new Vector3(settings.FlightRange * 0.5f, 0f, 0f)
                },
                VfxRigMotionKind.Orbit => new[]
                {
                    new Vector3(settings.OrbitRadius, 0f, 0f),
                    new Vector3(-settings.OrbitRadius, 0f, 0f),
                    new Vector3(0f, 0f, settings.OrbitRadius),
                    new Vector3(0f, 0f, -settings.OrbitRadius)
                },
                _ => new[] { Vector3.Zero }
            };

        private static void GrowEmitter(
            ref Vector3 min,
            ref Vector3 max,
            VfxEmitterDefinition emitter,
            Matrix4x4 worldBasis,
            Vector3 rootOrigin)
        {
            Vector3 rotation = emitter.RotationOverride.GetValueOrDefault() * (MathF.PI / 180f);
            Matrix4x4 frame =
                Matrix4x4.CreateScale(emitter.ScaleOverride ?? Vector3.One) *
                Matrix4x4.CreateRotationZ(rotation.Z) *
                Matrix4x4.CreateRotationX(rotation.X) *
                Matrix4x4.CreateRotationY(rotation.Y) *
                worldBasis *
                Matrix4x4.CreateTranslation(rootOrigin);

            Vector3 origin = emitter.EmitterPosition.Sample(0f);
            Vector3 offset = origin + emitter.TranslationOverride.GetValueOrDefault();

            GrowCross(ref min, ref max, origin, frame);
            GrowPoint(ref min, ref max, offset, frame);
            GrowCross(ref min, ref max, offset, frame);

            VfxSpawnShape shape = emitter.SpawnShape;
            if (shape is null)
            {
                GrowCross(ref min, ref max, offset, frame);
                return;
            }

            switch (shape.Kind)
            {
                case VfxSpawnShapeKind.Point:
                    GrowCross(ref min, ref max, offset + shape.EmitOffset.Sample(0f), frame);
                    break;

                case VfxSpawnShapeKind.Legacy:
                {
                    Vector3 place = offset + shape.EmitOffset.Sample(0f);
                    if (shape.BirthTranslation is { } translation)
                        place += translation.Sample(0f);
                    GrowCross(ref min, ref max, place, frame);
                    break;
                }

                case VfxSpawnShapeKind.Box:
                    GrowBox(ref min, ref max, offset, shape.Size, frame);
                    break;

                case VfxSpawnShapeKind.Cylinder:
                    GrowCylinder(ref min, ref max, offset, shape.Radius, shape.Height, frame);
                    break;

                case VfxSpawnShapeKind.Sphere:
                    GrowSphere(ref min, ref max, offset, shape.Radius, frame);
                    break;
            }
        }

        private static void GrowCross(
            ref Vector3 min,
            ref Vector3 max,
            Vector3 center,
            Matrix4x4 frame)
        {
            GrowPoint(ref min, ref max, center - Vector3.UnitX * ShapeTick, frame);
            GrowPoint(ref min, ref max, center + Vector3.UnitX * ShapeTick, frame);
            GrowPoint(ref min, ref max, center - Vector3.UnitY * ShapeTick, frame);
            GrowPoint(ref min, ref max, center + Vector3.UnitY * ShapeTick, frame);
            GrowPoint(ref min, ref max, center - Vector3.UnitZ * ShapeTick, frame);
            GrowPoint(ref min, ref max, center + Vector3.UnitZ * ShapeTick, frame);
        }

        private static void GrowBox(
            ref Vector3 min,
            ref Vector3 max,
            Vector3 center,
            Vector3 size,
            Matrix4x4 frame)
        {
            for (int x = -1; x <= 1; x += 2)
            for (int y = -1; y <= 1; y += 2)
            for (int z = -1; z <= 1; z += 2)
            {
                Vector3 corner = center + new Vector3(x * size.X, y * size.Y, z * size.Z);
                GrowPoint(ref min, ref max, corner, frame);
            }
        }

        private static void GrowCylinder(
            ref Vector3 min,
            ref Vector3 max,
            Vector3 center,
            float radius,
            float height,
            Matrix4x4 frame)
        {
            for (int step = 0; step < RingSegments; step++)
            {
                float radians = step / (float)RingSegments * MathF.PI * 2f;
                float x = MathF.Cos(radians) * radius;
                float z = MathF.Sin(radians) * radius;
                GrowPoint(ref min, ref max, center + new Vector3(x, 0f, z), frame);
                GrowPoint(ref min, ref max, center + new Vector3(x, height, z), frame);
            }
        }

        private static void GrowSphere(
            ref Vector3 min,
            ref Vector3 max,
            Vector3 center,
            float radius,
            Matrix4x4 frame)
        {
            for (int normal = 0; normal < 3; normal++)
            {
                for (int step = 0; step < RingSegments; step++)
                {
                    float radians = step / (float)RingSegments * MathF.PI * 2f;
                    Vector3 point = center;
                    int first = (normal + 1) % 3;
                    int second = (normal + 2) % 3;
                    SetAxis(ref point, first, GetAxis(point, first) + MathF.Cos(radians) * radius);
                    SetAxis(ref point, second, GetAxis(point, second) + MathF.Sin(radians) * radius);
                    GrowPoint(ref min, ref max, point, frame);
                }
            }
        }

        private static void GrowPoint(
            ref Vector3 min,
            ref Vector3 max,
            Vector3 point,
            Matrix4x4 frame)
            => Grow(ref min, ref max, Vector3.Transform(point, frame));

        private static void Grow(ref Vector3 min, ref Vector3 max, Vector3 point)
        {
            if (!IsFinite(point)) return;
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }

        private static float GetAxis(Vector3 value, int axis)
            => axis == 0 ? value.X : axis == 1 ? value.Y : value.Z;

        private static void SetAxis(ref Vector3 value, int axis, float component)
        {
            if (axis == 0) value.X = component;
            else if (axis == 1) value.Y = component;
            else value.Z = component;
        }

        private static bool IsFinite(Vector3 value)
            => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }
}
