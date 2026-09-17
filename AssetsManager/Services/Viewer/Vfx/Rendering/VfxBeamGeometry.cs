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
            // LTK's draw-kind contract suppresses the beam ribbon when mMesh is authored.
            // The same primitive does not fall through to mesh rendering, so this is intentionally blank.
            if (beam is null || state.Def.SuppressesBeamRibbon || state.InstanceCount == 0) return 0;

            int vertexCount = state.InstanceCount * 6;
            int needed = vertexCount * VertexStride;
            if (Vertices.Length < needed) Vertices = new float[needed];

            Vector3 source = state.SystemOrigin + beam.SourceOffset;
            Vector3 target = state.SystemTarget + beam.TargetOffset;
            Vector3 delta = target - source;
            float length = delta.Length();
            float colorDistance = (state.SystemTarget - state.SystemOrigin).Length();
            Vector3 axis = length > 1e-8f ? delta / length : Vector3.Zero;

            int written = 0;
            for (int particle = 0; particle < state.InstanceCount; particle++)
            {
                int instance = particle * VfxPlaybackRuntime.InstanceStride;
                float width = state.Instances[instance + 3];
                float fromSource = state.Instances[instance + 4];
                float fromTarget = state.Instances[instance + 18];

                Vector3 wide = beam.Mode == 1
                    ? ArbitraryWidth(state, instance, source, axis)
                    : CameraWidth(cameraPosition, source, delta);

                Vector3 start = source + delta * fromSource;
                Vector3 end = target - delta * fromTarget;
                Vector3 half = wide * (width * 0.5f);

                float tilingU = particle < state.Particles.Count ? state.Particles[particle].TrailTiling.X : 0f;
                float tilingV = particle < state.Particles.Count ? state.Particles[particle].TrailTiling.Y : 0f;
                float across = tilingU > 0f ? width / tilingU : 1f;
                float along = tilingV > 0f ? length / tilingV : 1f;

                Vector4 distanceColor = beam.ColorBoundToDistance
                    ? beam.ColorByDistance.Sample(colorDistance)
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

        private static Vector3 CameraWidth(Vector3 eye, Vector3 source, Vector3 delta)
        {
            Vector3 toEye = eye - source;
            Vector3 wide = Vector3.Cross(toEye, delta);
            if (wide.LengthSquared() > 1e-10f) return Vector3.Normalize(wide);

            // LTK sideOf() chooses the world axis least aligned with the eye vector.
            Vector3 least = MathF.Abs(toEye.X) <= MathF.Abs(toEye.Y) && MathF.Abs(toEye.X) <= MathF.Abs(toEye.Z)
                ? Vector3.UnitX
                : MathF.Abs(toEye.Y) <= MathF.Abs(toEye.Z) ? Vector3.UnitY : Vector3.UnitZ;
            wide = Vector3.Cross(toEye, least);
            return wide.LengthSquared() > 1e-10f ? Vector3.Normalize(wide) : least;
        }

        private static Vector3 ArbitraryWidth(
            VfxPlaybackRuntime.EmitterState state,
            int instance,
            Vector3 source,
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
            // LTK intentionally leaves this unnormalised and allows degenerate arbitrary beams.
            wide += local;
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
            Array.Copy(state.Instances, instance, Vertices, target + 2, VfxPlaybackRuntime.InstanceStride);
            Vertices[target + 2] = position.X;
            Vertices[target + 3] = position.Y;
            Vertices[target + 4] = position.Z;
            VfxRibbonVertexSemantics.Pack(state, instance, Vertices, target, u, v, transpose: true);
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
