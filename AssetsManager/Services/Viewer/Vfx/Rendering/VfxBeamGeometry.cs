using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Services.Viewer.Vfx.Semantics;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Rendering
{
    /// <summary>
    /// Builds beam ribbons and particle-driven segment strips, and places stretched beam meshes.
    /// </summary>
    internal sealed class VfxBeamGeometry
    {
        internal const int VertexStride = VfxPlaybackRuntime.InstanceStride + 2;
        internal float[] Vertices { get; private set; } = Array.Empty<float>();
        private float[] _ribs = Array.Empty<float>();
        private int[] _order = Array.Empty<int>();
        private readonly BirthOrder _birthOrder = new();
        private static readonly int[] SegmentCorners = { 0, -2, -1, 0, -1, 1 };

        private sealed class BirthOrder : IComparer<int>
        {
            internal VfxPlaybackRuntime.EmitterState State;
            public int Compare(int left, int right) => State.Particles[right].Serial.CompareTo(State.Particles[left].Serial);
        }

        internal int Build(VfxPlaybackRuntime.EmitterState state, Vector3 cameraPosition, int instanceCount = int.MaxValue)
        {
            VfxBeamDefinition beam = state.Def.Beam;
            int count = Math.Min(Math.Max(0, instanceCount), state.InstanceCount);
            if (beam is null || state.Def.SuppressesBeamRibbon || count == 0) return 0;
            ReadOnlySpan<float> instances = state.PrepareInstances(count);

            int vertexCount = count * 6;
            int needed = vertexCount * VertexStride;
            if (Vertices.Length < needed)
                Vertices = new float[VfxBufferGrowth.NextLength(Vertices.Length, needed, VertexStride * 6)];

            if (state.Def.PrimitiveKind == VfxPrimitiveKind.CameraSegmentBeam)
                return BuildSegments(state, cameraPosition, instances, Math.Min(count, state.Particles.Count));

            // The engine turns both local-space end offsets by the system's orientation before adding them.
            Vector3 source = state.SystemOrigin + Vector3.TransformNormal(beam.SourceOffset, state.SystemOrientation);
            Vector3 target = state.SystemTarget + Vector3.TransformNormal(beam.TargetOffset, state.SystemOrientation);
            Vector3 delta = target - source;
            float length = delta.Length();
            Vector3 axis = length > 0f ? delta / length : Vector3.Zero;

            int written = 0;
            for (int particle = 0; particle < count; particle++)
            {
                int instance = particle * VfxPlaybackRuntime.InstanceStride;
                float width = instances[instance + 3];
                float fromSource = instances[instance + 4];
                float fromTarget = instances[instance + 18];

                Vector3 wide = beam.Mode == 1
                    ? ArbitraryWidth(state, instances, particle, instance, axis)
                    : CameraWidth(cameraPosition, source, delta);

                Vector3 start = source + delta * fromSource;
                Vector3 end = target - delta * fromTarget;
                Vector3 half = wide * (width * 0.5f);

                float tilingU = particle < state.Particles.Count ? state.Particles[particle].TrailTiling.X : 0f;
                float tilingV = particle < state.Particles.Count ? state.Particles[particle].TrailTiling.Y : 0f;
                float across = tilingU > 0f ? width / tilingU : 1f;
                float along = tilingV > 0f ? length / tilingV : 1f;

                Vector4 distanceColor = DistanceColor(state);

                // The engine transforms (along, across) first and only then transposes the
                // resulting coordinate pair for beam sampling. Supplying (across, along)
                // here is not equivalent once a layer has non-uniform scale or rotation.
                Write(state, instances, instance, ref written, end - half, 0f, 0f, distanceColor, 0);
                Write(state, instances, instance, ref written, end + half, 0f, across, distanceColor, 1);
                Write(state, instances, instance, ref written, start + half, along, across, distanceColor, 2);
                Write(state, instances, instance, ref written, end - half, 0f, 0f, distanceColor, 0);
                Write(state, instances, instance, ref written, start + half, along, across, distanceColor, 2);
                Write(state, instances, instance, ref written, start - half, along, 0f, distanceColor, 3);
            }

            return written;
        }

        internal static void Ends(VfxPlaybackRuntime.EmitterState state, out Vector3 source, out Vector3 target)
        {
            source = state.SystemOrigin + Vector3.TransformNormal(state.Def.Beam.SourceOffset, state.SystemOrientation);
            target = state.SystemTarget + Vector3.TransformNormal(state.Def.Beam.TargetOffset, state.SystemOrientation);
        }

        internal static Vector4 DistanceColor(VfxPlaybackRuntime.EmitterState state)
        {
            Vector4 tint = state.Def.Beam.ColorBoundToDistance
                ? state.Def.Beam.ColorByDistance.Sample((state.SystemTarget - state.SystemOrigin).Length()) : Vector4.One;
            // Instances already carry premultiplied tint; applying the same rule to this factor
            // is equivalent to multiplying the distance colour before preprocessing the particle.
            return VfxColorSemantics.PremultiplyForAddOrSubtract(tint, state.Def.BlendMode,
                state.Def.DrawsAsDistortion, state.Def.HasResolvedCustomMaterial);
        }

        internal static bool TryMeshPlacement(VfxPlaybackRuntime.EmitterState state, out Vector3 position,
            out Matrix4x4 basis, out float stretch)
        {
            Ends(state, out Vector3 source, out Vector3 target);
            Vector3 direction = source - target;
            float squared = direction.LengthSquared();
            position = (source + target) * 0.5f;
            basis = Matrix4x4.Identity;
            stretch = 0f;
            if (squared <= 1e-9f || state.MeshDepth <= 0f) return false;
            Vector3 forward = Vector3.Normalize(direction);
            Vector3 right = Vector3.Cross(Vector3.UnitY, forward);
            if (right.LengthSquared() <= 1e-9f) right = Vector3.Cross(Vector3.UnitX, forward);
            right = Vector3.Normalize(right);
            Vector3 up = Vector3.Cross(forward, right);
            basis = new Matrix4x4(right.X, right.Y, right.Z, 0, up.X, up.Y, up.Z, 0,
                forward.X, forward.Y, forward.Z, 0, 0, 0, 0, 1);
            stretch = MathF.Sqrt(squared) / state.MeshDepth;
            return true;
        }

        private int BuildSegments(VfxPlaybackRuntime.EmitterState state, Vector3 eye, ReadOnlySpan<float> instances, int count)
        {
            if (count < 2 || state.Def.Beam.Segments <= 0) return 0;
            if (_order.Length < count) _order = new int[count];
            if (_ribs.Length < count * 2 * VertexStride) _ribs = new float[count * 2 * VertexStride];
            for (int index = 0; index < count; index++) _order[index] = index;
            _birthOrder.State = state;
            Array.Sort(_order, 0, count, _birthOrder);
            Ends(state, out Vector3 source, out Vector3 target);
            Vector3 delta = target - source;
            Vector3 wide = Vector3.Cross(delta, eye - source);
            wide = wide.LengthSquared() > 0 ? Vector3.Normalize(wide) : CameraWidth(eye, source, Vector3.Zero);
            float reach = state.Def.Beam.TrailMode == 0 ? 0f : (state.SystemTarget - state.SystemOrigin).Length();
            Vector4 color = DistanceColor(state);
            int ribs = 0, written = 0;
            ushort previous = 0;
            for (int listed = 0; listed < count; listed++)
            {
                int particle = _order[listed];
                var data = state.Particles[particle];
                if (data.Ordinal >= state.Def.Beam.Segments) continue;
                int instance = particle * VfxPlaybackRuntime.InstanceStride;
                float share = state.Def.Beam.Segments > 1 ? (float)data.Ordinal / (state.Def.Beam.Segments - 1) : 0f;
                Vector3 local = new Vector3(instances[instance], instances[instance + 1], instances[instance + 2]) - state.SystemOrigin;
                Vector3 center = source + delta * share + local;
                float width = instances[instance + 3];
                float across = data.TrailTiling.Y > 0 ? width / data.TrailTiling.Y : data.TrailTiling.Y == 0 ? 1 : -data.TrailTiling.Y;
                float v = data.TrailTiling.X > 0 ? reach * share / data.TrailTiling.X : 0;
                int rib = ribs * 2;
                WriteRib(state, instances, instance, rib, center - wide * (width * 0.5f), 0.5f - across * 0.5f, v, color);
                WriteRib(state, instances, instance, rib + 1, center + wide * (width * 0.5f), 0.5f + across * 0.5f, v, color);
                if (ribs > 0 && data.Ordinal < previous)
                {
                    foreach (int corner in SegmentCorners)
                        _ribs.AsSpan((rib + corner) * VertexStride, VertexStride).CopyTo(Vertices.AsSpan(written++ * VertexStride, VertexStride));
                }
                ribs++;
                previous = data.Ordinal;
            }
            return written;
        }

        private void WriteRib(VfxPlaybackRuntime.EmitterState state, ReadOnlySpan<float> instances, int instance,
            int rib, Vector3 position, float u, float v, Vector4 color)
        {
            int at = rib * VertexStride;
            instances.Slice(instance, VfxPlaybackRuntime.InstanceStride).CopyTo(_ribs.AsSpan(at + 2));
            _ribs[at + 2] = position.X; _ribs[at + 3] = position.Y; _ribs[at + 4] = position.Z;
            VfxRibbonVertexSemantics.Pack(state, instances, instance, _ribs, at, u, v, false, segment: true);
            _ribs[at + 7] *= color.X; _ribs[at + 8] *= color.Y; _ribs[at + 9] *= color.Z; _ribs[at + 10] *= color.W;
            _ribs[at + 26] = 0;
        }

        private static Vector3 CameraWidth(Vector3 eye, Vector3 source, Vector3 delta)
        {
            Vector3 toEye = eye - source;
            Vector3 wide = Vector3.Cross(toEye, delta);
            if (wide.LengthSquared() > 0f) return Vector3.Normalize(wide);

            // sideOf() chooses the world axis least aligned with the eye vector and only
            // falls back when the cross product has exactly zero length.
            Vector3 least = MathF.Abs(toEye.X) <= MathF.Abs(toEye.Y) && MathF.Abs(toEye.X) <= MathF.Abs(toEye.Z)
                ? Vector3.UnitX
                : MathF.Abs(toEye.Y) <= MathF.Abs(toEye.Z) ? Vector3.UnitY : Vector3.UnitZ;
            wide = Vector3.Cross(toEye, least);
            return wide.LengthSquared() > 0f ? Vector3.Normalize(wide) : least;
        }

        private static Vector3 ArbitraryWidth(
            VfxPlaybackRuntime.EmitterState state,
            ReadOnlySpan<float> instances,
            int particle,
            int instance,
            Vector3 axis)
        {
            Vector3 wide = new(-axis.Z, 0f, axis.X);
            Matrix4x4 standing = VfxPlaybackRuntime.ResolveStandingBasisForRender(state, state.Particles[particle]);
            wide = Vector3.TransformNormal(wide, standing);
            Vector3 local = new Vector3(
                instances[instance],
                instances[instance + 1],
                instances[instance + 2]) - state.SystemOrigin;
            // Riot adds the particle's position relative to the system origin, not the beam's
            // source after mLocalSpaceSourceOffset, and deliberately leaves the result unnormalised.
            wide += local;
            return wide;
        }

        private void Write(
            VfxPlaybackRuntime.EmitterState state,
            ReadOnlySpan<float> instances,
            int instance,
            ref int vertex,
            Vector3 position,
            float u,
            float v,
            Vector4 distanceColor,
            int alphaVertexIndex)
        {
            int target = vertex * VertexStride;
            instances.Slice(instance, VfxPlaybackRuntime.InstanceStride)
                .CopyTo(Vertices.AsSpan(target + 2, VfxPlaybackRuntime.InstanceStride));
            Vertices[target + 2] = position.X;
            Vertices[target + 3] = position.Y;
            Vertices[target + 4] = position.Z;
            VfxRibbonVertexSemantics.Pack(state, instances, instance, Vertices, target, u, v, transpose: true, alphaVertexIndex: alphaVertexIndex);
            Vertices[target + 7] *= distanceColor.X;
            Vertices[target + 8] *= distanceColor.Y;
            Vertices[target + 9] *= distanceColor.Z;
            Vertices[target + 10] *= distanceColor.W;
            vertex++;
        }
    }
}
