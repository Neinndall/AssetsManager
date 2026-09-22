using System;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Rendering
{
    internal sealed class VfxTrailGeometry
    {
        internal const int VertexStride = VfxPlaybackRuntime.InstanceStride + 2;
        internal const int TrailPointsPerSource = 1024;
        internal const int TrailPointsPerEmitter = TrailPointsPerSource * 4;
        internal float[] Vertices { get; private set; } = Array.Empty<float>();
        private float[] _points = Array.Empty<float>();
        private ulong[] _birthOrder = Array.Empty<ulong>();

        internal static int ResolvePointCount(int instanceCount)
            => Math.Min(Math.Max(0, instanceCount), TrailPointsPerSource);

        internal int Build(VfxPlaybackRuntime.EmitterState state, Vector3 viewDirection, int maxPoints = TrailPointsPerSource)
        {
            VfxTrailDefinition trail = state.Def.Trail;
            if (trail is null) return 0;
            int count = Math.Min(
                Math.Min(ResolvePointCount(state.InstanceCount), state.Particles.Count),
                Math.Max(0, maxPoints));
            if (count < 2) return 0;

            // LTK's packed pool retires by swapping the final live particle into a freed slot.
            // Trails therefore recover birth order from each particle serial before building the
            // ribbon. Include the physical slot as a tie-breaker for deterministic test/fallback data.
            if (_birthOrder.Length < count)
                _birthOrder = new ulong[count];
            for (int index = 0; index < count; index++)
                _birthOrder[index] = ((ulong)state.Particles[index].Serial << 32) | (uint)index;
            Array.Sort(_birthOrder, 0, count);

            int needed = count * 2 * VertexStride;
            if (_points.Length < needed) _points = new float[needed];
            needed = (count - 1) * 6 * VertexStride;
            if (Vertices.Length < needed) Vertices = new float[needed];
            bool smoothed = trail.SmoothingMode > 0;
            int step = trail.SmoothingMode == 2 ? 1 : -1;
            int start = step == 1 ? 0 : count - 1;
            float walked = 0f, uvBase = 0f;
            int held = 0, vertices = 0;
            Vector3 last = default, lastAcross = default;
            for (int seen = 0; seen < count; seen++)
            {
                int orderedAt = start + seen * step;
                int at = ParticleIndex(orderedAt);
                Vector3 point = Position(state, orderedAt, count, smoothed && seen != 0 && orderedAt != 0);
                Vector3 tangent = seen == 0
                    ? Position(state, orderedAt + step, count, false) - point
                    : point - last;
                if (seen > 0)
                {
                    walked += tangent.Length();
                    if (trail?.Cutoff > 0f && walked >= trail.Cutoff) break;
                }
                Vector3 across;
                if (state.Def.PrimitiveKind == VfxPrimitiveKind.ArbitraryTrail)
                {
                    Matrix4x4 standing = VfxPlaybackRuntime.ResolveStandingBasisForRender(state, state.Particles[at]);
                    across = Vector3.TransformNormal(Vector3.UnitX, standing);
                }
                else
                {
                    across = Vector3.Cross(viewDirection, tangent);
                    if (across.LengthSquared() == 0f)
                        across = seen > 0 ? lastAcross : SideOf(viewDirection);
                    else
                        across = Vector3.Normalize(across);
                }
                Vector3 rawAcross = across;
                if (smoothed && seen > 0)
                {
                    Vector3 miter = across + lastAcross;
                    across = miter.LengthSquared() > 0f ? Vector3.Normalize(miter) : rawAcross;
                }
                int source = at * VfxPlaybackRuntime.InstanceStride;
                float halfWidth = state.Instances[source + 3];
                var particle = state.Particles[at];
                float tilingU = particle.TrailTiling.X, tilingV = particle.TrailTiling.Y;
                float u = tilingU > 0f ? (trail?.Mode == 1 ? particle.TrailBirthDistance : walked) / tilingU : 0f;
                if (seen == 0) uvBase = u - u % 2f;
                u -= uvBase;
                float span = tilingV > 0f ? halfWidth / tilingV : tilingV == 0f ? 1f : -tilingV;
                WritePoint(state, source, held * 2, point + across * halfWidth, u, 0.5f - span * 0.5f);
                WritePoint(state, source, held * 2 + 1, point - across * halfWidth, u, 0.5f + span * 0.5f);
                if (held > 0)
                {
                    // Riot still submits the two triangles when consecutive trail points land on
                    // the same place. They are degenerate geometry, but keeping them preserves the
                    // strand's vertex/attribute sequence exactly.
                    int v = held * 2;
                    // LTK ribbon indices are [v, v-2, v-1] and [v, v-1, v+1].
                    // Keep that authored winding: CustomMaterial may enable face culling even
                    // though ordinary trail materials are double-sided.
                    Copy(v, ref vertices); Copy(v - 2, ref vertices); Copy(v - 1, ref vertices);
                    Copy(v, ref vertices); Copy(v - 1, ref vertices); Copy(v + 1, ref vertices);
                }
                held++;
                last = point;
                lastAcross = rawAcross;
            }
            return vertices;
        }

        private int ParticleIndex(int orderedIndex)
            => (int)(_birthOrder[orderedIndex] & uint.MaxValue);

        private Vector3 Position(VfxPlaybackRuntime.EmitterState state, int orderedAt, int count, bool filtered)
        {
            int first = filtered ? Math.Max(0, orderedAt - 3) : orderedAt;
            int last = filtered ? Math.Min(count - 1, orderedAt + 3) : orderedAt;
            Vector3 sum = default;
            for (int orderedIndex = first; orderedIndex <= last; orderedIndex++)
            {
                int sourceIndex = ParticleIndex(orderedIndex);
                int offset = sourceIndex * VfxPlaybackRuntime.InstanceStride;
                sum += new Vector3(state.Instances[offset], state.Instances[offset + 1], state.Instances[offset + 2]);
            }
            return sum / (last - first + 1);
        }

        internal static Vector3 SideOf(Vector3 view)
        {
            float x = MathF.Abs(view.X);
            float y = MathF.Abs(view.Y);
            float z = MathF.Abs(view.Z);
            Vector3 axis = x <= y && x <= z
                ? Vector3.UnitX
                : y <= z ? Vector3.UnitY : Vector3.UnitZ;
            Vector3 side = Vector3.Cross(view, axis);
            return side.LengthSquared() > 0f ? Vector3.Normalize(side) : axis;
        }

        private void WritePoint(VfxPlaybackRuntime.EmitterState state, int source, int vertex, Vector3 position, float u, float v)
        {
            int offset = vertex * VertexStride;
            Array.Copy(state.Instances, source, _points, offset + 2, VfxPlaybackRuntime.InstanceStride);
            _points[offset + 2] = position.X;
            _points[offset + 3] = position.Y;
            _points[offset + 4] = position.Z;
            VfxRibbonVertexSemantics.Pack(state, source, _points, offset, u, v, transpose: false);
        }

        private void Copy(int point, ref int vertices)
        {
            Array.Copy(_points, point * VertexStride, Vertices, vertices * VertexStride, VertexStride);
            vertices++;
        }
    }
}
