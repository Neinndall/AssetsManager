using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Resources;

namespace AssetsManager.Services.Viewer.Vfx.Runtime
{
    internal sealed class VfxEmitterEmissionSampler : IVfxEmissionSurfaceSampler
    {
        internal IVfxEmissionSurfaceSampler Mesh { get; }
        internal IVfxEmissionSurfaceSampler Surface { get; }
        internal VfxEmitterEmissionSampler(IVfxEmissionSurfaceSampler mesh, IVfxEmissionSurfaceSampler surface)
            => (Mesh, Surface) = (mesh, surface);
        public bool TrySample(float time, VfxLtkRandom rng, out VfxSurfaceBirth birth)
        {
            birth = default;
            return Surface?.TrySample(time, rng, out birth) == true;
        }
    }

    /// <summary>Static emission mesh: area-weighted triangles with League's two-draw corner blend.</summary>
    internal sealed class VfxStaticEmissionMeshSampler : IVfxEmissionSurfaceSampler
    {
        private readonly VfxMeshData _mesh;
        private readonly double[] _reach;
        private readonly double _total;
        internal VfxStaticEmissionMeshSampler(VfxMeshData mesh)
        {
            _mesh = mesh;
            _reach = new double[(mesh.Indices?.Length ?? 0) / 3];
            double total = 0d;
            for (int triangle = 0; triangle < _reach.Length; triangle++)
            {
                if (Corners(triangle, out Vector3 a, out Vector3 b, out Vector3 c))
                    total += Vector3.Cross(b - a, c - a).Length() / 2d;
                _reach[triangle] = total;
            }
            _total = total;
        }
        private bool Corners(int triangle, out Vector3 a, out Vector3 b, out Vector3 c)
        {
            a = b = c = default;
            int count = (_mesh.Positions?.Length ?? 0) / 3;
            uint i = _mesh.Indices[triangle * 3], j = _mesh.Indices[triangle * 3 + 1], k = _mesh.Indices[triangle * 3 + 2];
            if (i >= count || j >= count || k >= count) return false;
            a = Vertex((int)i); b = Vertex((int)j); c = Vertex((int)k);
            return true;
        }
        private Vector3 Vertex(int index)
            => new(_mesh.Positions[index * 3], _mesh.Positions[index * 3 + 1], _mesh.Positions[index * 3 + 2]);
        public bool TrySample(float time, VfxLtkRandom rng, out VfxSurfaceBirth birth)
        {
            birth = default;
            if (!(_total >= 1e-5d)) return false;
            double pick = rng.NextUnitFloat() * _total;
            int triangle = 0;
            while (triangle + 1 < _reach.Length && pick >= _reach[triangle]) triangle++;
            if (!Corners(triangle, out Vector3 a, out Vector3 b, out Vector3 c)) return false;
            float u = rng.NextUnitFloat(), v = rng.NextUnitFloat();
            birth = new VfxSurfaceBirth(a * ((1f - u) * (1f - v)) + b * ((1f - u) * v) + c * u,
                Vector3.Normalize(Vector3.Cross(b - a, c - a)));
            return true;
        }
    }

    /// <summary>Pose data consumed by authored mesh and skeleton emission surfaces.</summary>
    internal interface IVfxEmissionSurfacePose
    {
        int JointCount { get; }
        float[] BoneIndices { get; }
        float[] BoneWeights { get; }
        uint JointHashAt(int index);
        int ParentIndexAt(int index);
        ReadOnlySpan<Matrix4x4> EvaluatePalette(float seconds);
        void EvaluateJointPositions(float seconds, Span<Vector3> positions);
        void EvaluateRestJointPositions(Span<Vector3> positions);
    }

    /// <summary>LTK-compatible uniform-by-triangle mesh surface sampling.</summary>
    internal sealed class VfxMeshEmissionSurfaceSampler : IVfxEmissionSurfaceSampler
    {
        private readonly VfxMeshData _mesh;
        private readonly IVfxEmissionSurfacePose _pose;
        private readonly int _maxJointWeights;

        internal VfxMeshEmissionSurfaceSampler(
            VfxMeshData mesh,
            IVfxEmissionSurfacePose pose,
            int maxJointWeights)
        {
            _mesh = mesh;
            _pose = pose;
            _maxJointWeights = Math.Clamp(maxJointWeights, 1, 4);
        }

        public bool TrySample(float time, VfxLtkRandom rng, out VfxSurfaceBirth birth)
        {
            ArgumentNullException.ThrowIfNull(rng);
            birth = default;
            uint[] indices = _mesh.Indices;
            int vertexCount = _mesh.Positions?.Length / 3 ?? 0;
            int triangleCount = indices?.Length / 3 ?? 0;
            if (triangleCount <= 0 || vertexCount <= 0)
                return false;

            int triangle = Math.Min(
                triangleCount - 1,
                (int)(rng.NextUnitFloat() * triangleCount));
            int baseIndex = triangle * 3;
            int i0 = checked((int)indices[baseIndex]);
            int i1 = checked((int)indices[baseIndex + 1]);
            int i2 = checked((int)indices[baseIndex + 2]);
            if ((uint)i0 >= vertexCount || (uint)i1 >= vertexCount || (uint)i2 >= vertexCount)
                return false;

            ReadOnlySpan<Matrix4x4> palette = _pose is null
                ? ReadOnlySpan<Matrix4x4>.Empty
                : _pose.EvaluatePalette(time);
            Vector3 v0 = VertexAt(i0, palette);
            Vector3 v1 = VertexAt(i1, palette);
            Vector3 v2 = VertexAt(i2, palette);

            float root = MathF.Sqrt(rng.NextUnitFloat());
            float along = rng.NextUnitFloat();
            Vector3 position =
                v0 * (1f - root) +
                v1 * (root * (1f - along)) +
                v2 * (root * along);

            Vector3 normal = Vector3.Zero;
            if (_mesh.Normals is { } normals && normals.Length == _mesh.Positions.Length)
            {
                normal = VertexAt(i0, palette, normals, direction: true) * (1f - root) +
                    VertexAt(i1, palette, normals, direction: true) * (root * (1f - along)) +
                    VertexAt(i2, palette, normals, direction: true) * (root * along);
            }
            if (normal.LengthSquared() == 0f) normal = Vector3.Cross(v1 - v0, v2 - v0);
            if (normal.LengthSquared() > 0f)
                normal = Vector3.Normalize(normal);
            else
                normal = Vector3.Zero;

            birth = new VfxSurfaceBirth(position, normal);
            return true;
        }

        private Vector3 VertexAt(int vertexIndex, ReadOnlySpan<Matrix4x4> palette,
            float[] source = null, bool direction = false)
        {
            Vector3 bind = ReadPosition(source ?? _mesh.Positions, vertexIndex);
            if (_pose is null || palette.IsEmpty)
                return bind;

            float[] indices = _pose.BoneIndices;
            float[] weights = _pose.BoneWeights;
            int offset = vertexIndex * 4;
            if (indices is null || weights is null ||
                offset + 3 >= indices.Length || offset + 3 >= weights.Length)
            {
                return bind;
            }

            Vector3 skinned = Vector3.Zero;
            float sum = 0f;
            for (int part = 0; part < _maxJointWeights; part++)
            {
                float weight = weights[offset + part];
                int influence = (int)indices[offset + part];
                if (!float.IsFinite(weight) || weight <= 0f || (uint)influence >= palette.Length)
                    continue;
                Vector3 transformed = direction
                    ? Vector3.TransformNormal(bind, palette[influence])
                    : Vector3.Transform(bind, palette[influence]);
                if (direction && transformed.LengthSquared() > 0f) transformed = Vector3.Normalize(transformed);
                skinned += transformed * weight;
                sum += weight;
            }

            return sum > 0f ? skinned / sum : bind;
        }

        private static Vector3 ReadPosition(float[] positions, int index)
            => new(positions[index * 3], positions[index * 3 + 1], positions[index * 3 + 2]);
    }

    /// <summary>Rest-length-weighted bone births with a random perpendicular direction.</summary>
    internal sealed class VfxSkeletonEmissionSurfaceSampler : IVfxEmissionSurfaceSampler
    {
        private readonly IVfxEmissionSurfacePose _pose;
        private readonly int[] _slots;
        private readonly Vector3[] _positions;
        private readonly double[] _lengths;
        private readonly double _total;

        internal VfxSkeletonEmissionSurfaceSampler(
            IVfxEmissionSurfacePose pose,
            IReadOnlyList<uint> jointMask)
        {
            _pose = pose ?? throw new ArgumentNullException(nameof(pose));
            var mask = (jointMask ?? Array.Empty<uint>()).ToHashSet();
            _slots = Enumerable.Range(0, pose.JointCount)
                .Where(index => pose.ParentIndexAt(index) >= 0 &&
                    (mask.Count == 0 || mask.Contains(pose.JointHashAt(index))))
                .ToArray();
            _positions = new Vector3[pose.JointCount];
            _lengths = new double[_slots.Length];
            pose.EvaluateRestJointPositions(_positions);
            double total = 0d;
            for (int index = 0; index < _slots.Length; index++)
            {
                int slot = _slots[index];
                total += Vector3.Distance(_positions[slot], _positions[pose.ParentIndexAt(slot)]);
                _lengths[index] = total;
            }
            _total = total;
        }

        public bool TrySample(float time, VfxLtkRandom rng, out VfxSurfaceBirth birth)
        {
            ArgumentNullException.ThrowIfNull(rng);
            birth = default;
            if (!(_total > 0d))
                return false;

            _pose.EvaluateJointPositions(time, _positions);
            double pick = rng.NextUnitFloat() * _total;
            int selected = 0;
            while (selected < _slots.Length - 1 && pick >= _lengths[selected])
                selected++;

            int joint = _slots[selected];
            int parentJoint = _pose.ParentIndexAt(joint);
            Vector3 parent = _positions[parentJoint];
            Vector3 child = _positions[joint];
            Vector3 position = Vector3.Lerp(parent, child, rng.NextUnitFloat());
            Vector3 bone = child - parent;
            bone = bone.LengthSquared() > 0f ? Vector3.Normalize(bone) : Vector3.UnitY;
            Vector3 across = Vector3.Normalize(Vector3.Cross(MathF.Abs(bone.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY, bone));
            Vector3 over = Vector3.Cross(bone, across);
            float angle = rng.NextUnitFloat() * (MathF.PI * 2f);
            Vector3 normal = across * MathF.Cos(angle) + over * MathF.Sin(angle);

            birth = new VfxSurfaceBirth(position, normal);
            return true;
        }
    }
}
