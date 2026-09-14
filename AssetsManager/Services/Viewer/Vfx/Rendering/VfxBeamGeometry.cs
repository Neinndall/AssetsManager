using System;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Rendering
{
    /// <summary>
    /// Builds the engine-style beam ribbon: one quad per live particle spanning the
    /// system source and target rather than following particle velocity.
    /// </summary>
    internal sealed class VfxBeamGeometry
    {
        internal const int VertexStride = VfxPlaybackRuntime.InstanceStride + 2;
        internal float[] Vertices { get; private set; } = Array.Empty<float>();

        internal int Build(VfxPlaybackRuntime.EmitterState state, Vector3 cameraPosition)
        {
            VfxBeamDefinition beam = state.Def.Beam;
            if (beam is null || state.InstanceCount == 0) return 0;

            int vertexCount = state.InstanceCount * 6;
            int needed = vertexCount * VertexStride;
            if (Vertices.Length < needed) Vertices = new float[needed];

            Vector3 source = state.SystemOrigin + beam.SourceOffset;
            Vector3 target = state.SystemTarget + beam.TargetOffset;
            Vector3 delta = target - source;
            float length = delta.Length();
            Vector3 axis = length > 1e-8f ? delta / length : Vector3.UnitX;

            int written = 0;
            for (int particle = 0; particle < state.InstanceCount; particle++)
            {
                int instance = particle * VfxPlaybackRuntime.InstanceStride;
                float width = state.Instances[instance + 3];
                float fromSource = state.Instances[instance + 4];
                float fromTarget = state.Instances[instance + 18];

                Vector3 wide = beam.Mode == 1
                    ? ArbitraryWidth(state, instance, source, delta, axis)
                    : CameraWidth(cameraPosition, source, delta, axis);

                Vector3 start = source + delta * fromSource;
                Vector3 end = target - delta * fromTarget;
                Vector3 half = wide * (width * 0.5f);

                float tilingU = particle < state.Particles.Count ? state.Particles[particle].TrailTiling.X : 0f;
                float tilingV = particle < state.Particles.Count ? state.Particles[particle].TrailTiling.Y : 0f;
                float across = tilingU > 0f ? width / tilingU : 1f;
                float along = tilingV > 0f ? length / tilingV : 1f;

                Vector4 distanceColor = beam.ColorBoundToDistance
                    ? beam.ColorByDistance.Sample(length)
                    : Vector4.One;

                Write(state, instance, ref written, end - half, 0f, 0f, distanceColor);
                Write(state, instance, ref written, end + half, across, 0f, distanceColor);
                Write(state, instance, ref written, start + half, across, along, distanceColor);
                Write(state, instance, ref written, end - half, 0f, 0f, distanceColor);
                Write(state, instance, ref written, start + half, across, along, distanceColor);
                Write(state, instance, ref written, start - half, 0f, along, distanceColor);
            }

            return written;
        }

        private static Vector3 CameraWidth(Vector3 eye, Vector3 source, Vector3 delta, Vector3 axis)
        {
            Vector3 wide = Vector3.Cross(eye - source, delta);
            if (wide.LengthSquared() > 1e-10f) return Vector3.Normalize(wide);

            Vector3 least = MathF.Abs(axis.X) <= MathF.Abs(axis.Y) && MathF.Abs(axis.X) <= MathF.Abs(axis.Z)
                ? Vector3.UnitX
                : MathF.Abs(axis.Y) <= MathF.Abs(axis.Z) ? Vector3.UnitY : Vector3.UnitZ;
            wide = Vector3.Cross(eye - source, least);
            return wide.LengthSquared() > 1e-10f ? Vector3.Normalize(wide) : least;
        }

        private static Vector3 ArbitraryWidth(
            VfxPlaybackRuntime.EmitterState state,
            int instance,
            Vector3 source,
            Vector3 delta,
            Vector3 axis)
        {
            Vector3 wide = new(-axis.Z, 0f, axis.X);
            Vector3 rotation = new(
                state.Instances[instance + 15],
                state.Instances[instance + 16],
                state.Instances[instance + 17]);
            Matrix4x4 turn = Matrix4x4.CreateRotationZ(rotation.Z) *
                Matrix4x4.CreateRotationX(rotation.X) *
                Matrix4x4.CreateRotationY(rotation.Y);
            wide = Vector3.TransformNormal(wide, turn);
            Vector3 local = new Vector3(
                state.Instances[instance],
                state.Instances[instance + 1],
                state.Instances[instance + 2]) - source;
            wide += local;
            if (wide.LengthSquared() <= 1e-10f)
                wide = Vector3.Cross(delta, Vector3.UnitY);
            return wide;
        }

        private void Write(
            VfxPlaybackRuntime.EmitterState state,
            int instance,
            ref int vertex,
            Vector3 position,
            float u,
            float v,
            Vector4 distanceColor)
        {
            int target = vertex * VertexStride;
            Vertices[target] = u;
            Vertices[target + 1] = v;
            Array.Copy(state.Instances, instance, Vertices, target + 2, VfxPlaybackRuntime.InstanceStride);
            Vertices[target + 2] = position.X;
            Vertices[target + 3] = position.Y;
            Vertices[target + 4] = position.Z;
            Vertices[target + 7] *= distanceColor.X;
            Vertices[target + 8] *= distanceColor.Y;
            Vertices[target + 9] *= distanceColor.Z;
            Vertices[target + 10] *= distanceColor.W;
            if (state.Def.PrimitiveKind == VfxPrimitiveKind.CameraSegmentBeam)
                Vertices[target + 26] = 0f;
            vertex++;
        }
    }
}
