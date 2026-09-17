using System;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Rendering
{
    internal sealed class VfxTrailGeometry
    {
        internal const int VertexStride = VfxPlaybackRuntime.InstanceStride + 2;
        internal float[] Vertices { get; private set; } = Array.Empty<float>();
        private float[] _points = Array.Empty<float>();

        internal int Build(VfxPlaybackRuntime.EmitterState state, Vector3 viewDirection)
        {
            int count = state.InstanceCount;
            if (count < 2) return 0;
            int needed = count * 2 * VertexStride;
            if (_points.Length < needed) _points = new float[needed];
            needed = (count - 1) * 6 * VertexStride;
            if (Vertices.Length < needed) Vertices = new float[needed];
            var trail = state.Def.Trail;
            bool smoothed = trail?.SmoothingMode > 0;
            int step = trail?.SmoothingMode == 2 ? 1 : -1;
            int start = step == 1 ? 0 : count - 1;
            float walked = 0f, uvBase = 0f;
            int held = 0, vertices = 0;
            Vector3 last = default, lastAcross = default;
            for (int seen = 0; seen < count; seen++)
            {
                int at = start + seen * step;
                Vector3 point = Position(state, at, smoothed && seen != 0 && at != 0);
                Vector3 tangent = seen == 0 ? Position(state, at + step, false) - point : point - last;
                if (seen > 0)
                {
                    walked += tangent.Length();
                    if (trail?.Cutoff > 0f && walked >= trail.Cutoff) break;
                }
                Vector3 across;
                if (state.Def.PrimitiveKind == VfxPrimitiveKind.ArbitraryTrail)
                {
                    int rotation = at * VfxPlaybackRuntime.InstanceStride + 15;
                    var r = new Vector3(state.Instances[rotation], state.Instances[rotation + 1], state.Instances[rotation + 2]);
                    var local = Vector3.TransformNormal(Vector3.UnitX,
                        Matrix4x4.CreateRotationZ(r.Z) * Matrix4x4.CreateRotationX(r.X) * Matrix4x4.CreateRotationY(r.Y));
                    across = state.PlacementRight * local.X + state.PlacementUp * local.Y + state.PlacementForward * local.Z;
                }
                else
                {
                    across = Vector3.Cross(viewDirection, tangent);
                    if (across.LengthSquared() <= 1e-10f)
                    {
                        across = seen > 0 ? lastAcross : Vector3.Cross(viewDirection, Vector3.UnitY);
                        if (across.LengthSquared() <= 1e-10f) across = Vector3.UnitX;
                    }
                }
                across = Vector3.Normalize(across);
                Vector3 rawAcross = across;
                if (smoothed && seen > 0 && (across + lastAcross).LengthSquared() > 1e-10f)
                    across = Vector3.Normalize(across + lastAcross);
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
                if (held > 0 && Vector3.DistanceSquared(last, point) > 1e-10f)
                {
                    int v = held * 2;
                    Copy(v, ref vertices); Copy(v - 1, ref vertices); Copy(v - 2, ref vertices);
                    Copy(v, ref vertices); Copy(v + 1, ref vertices); Copy(v - 1, ref vertices);
                }
                held++;
                last = point;
                lastAcross = rawAcross;
            }
            return vertices;
        }

        private static Vector3 Position(VfxPlaybackRuntime.EmitterState state, int at, bool filtered)
        {
            int first = filtered ? Math.Max(0, at - 3) : at;
            int last = filtered ? Math.Min(state.InstanceCount - 1, at + 3) : at;
            Vector3 sum = default;
            for (int i = first; i <= last; i++)
            {
                int offset = i * VfxPlaybackRuntime.InstanceStride;
                sum += new Vector3(state.Instances[offset], state.Instances[offset + 1], state.Instances[offset + 2]);
            }
            return sum / (last - first + 1);
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
