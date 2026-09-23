using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Resources;

namespace AssetsManager.Services.Viewer.Vfx.Runtime
{
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
    }

    /// <summary>LTK-compatible uniform-by-triangle mesh surface sampling.</summary>
    internal sealed class VfxMeshEmissionSurfaceSampler : IVfxEmissionSurfaceSampler
    {
        private readonly VfxMeshData _mesh;
        private readonly IVfxEmissionSurfacePose _pose;
        private readonly float _scale;
        private readonly int _maxJointWeights;

        internal VfxMeshEmissionSurfaceSampler(
            VfxMeshData mesh,
            IVfxEmissionSurfacePose pose,
            float scale,
            int maxJointWeights)
        {
            _mesh = mesh;
            _pose = pose;
            _scale = float.IsFinite(scale) ? scale : 1f;
            _maxJointWeights = Math.Clamp(maxJointWeights, 0, 4);
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
            position *= _scale;

            Vector3 normal = Vector3.Cross(v1 - v0, v2 - v0);
            if (normal.LengthSquared() > 1e-12f)
                normal = Vector3.Normalize(normal);
            else
                normal = Vector3.Zero;

            birth = new VfxSurfaceBirth(position, normal);
            return true;
        }

        private Vector3 VertexAt(int vertexIndex, ReadOnlySpan<Matrix4x4> palette)
        {
            Vector3 bind = ReadPosition(_mesh.Positions, vertexIndex);
            if (_pose is null || _maxJointWeights <= 0 || palette.IsEmpty)
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
                skinned += Vector3.Transform(bind, palette[influence]) * weight;
                sum += weight;
            }

            return sum > 0f ? skinned / sum : bind;
        }

        private static Vector3 ReadPosition(float[] positions, int index)
            => new(positions[index * 3], positions[index * 3 + 1], positions[index * 3 + 2]);
    }

    /// <summary>LTK-compatible skeleton sampling weighted by posed bone-segment length.</summary>
    internal sealed class VfxSkeletonEmissionSurfaceSampler : IVfxEmissionSurfaceSampler
    {
        private readonly IVfxEmissionSurfacePose _pose;
        private readonly int[] _slots;
        private readonly Vector3[] _positions;
        private readonly double[] _lengths;
        private readonly float _scale;

        internal VfxSkeletonEmissionSurfaceSampler(
            IVfxEmissionSurfacePose pose,
            IReadOnlyList<uint> jointMask,
            float scale)
        {
            _pose = pose ?? throw new ArgumentNullException(nameof(pose));
            _scale = float.IsFinite(scale) ? scale : 1f;
            var mask = (jointMask ?? Array.Empty<uint>()).ToHashSet();
            _slots = Enumerable.Range(0, pose.JointCount)
                .Where(index => pose.ParentIndexAt(index) >= 0 &&
                    (mask.Count == 0 || mask.Contains(pose.JointHashAt(index))))
                .ToArray();
            _positions = new Vector3[pose.JointCount];
            _lengths = new double[_slots.Length];
        }

        public bool TrySample(float time, VfxLtkRandom rng, out VfxSurfaceBirth birth)
        {
            ArgumentNullException.ThrowIfNull(rng);
            birth = default;
            if (_slots.Length == 0)
                return false;

            _pose.EvaluateJointPositions(time, _positions);
            double total = 0d;
            for (int index = 0; index < _slots.Length; index++)
            {
                int slot = _slots[index];
                int parentIndex = _pose.ParentIndexAt(slot);
                total += Vector3.Distance(_positions[slot], _positions[parentIndex]);
                _lengths[index] = total;
            }
            if (!(total > 0d))
                return false;

            double pick = rng.NextUnitFloat() * total;
            int selected = 0;
            while (selected < _slots.Length - 1 && pick >= _lengths[selected])
                selected++;

            int joint = _slots[selected];
            int parentJoint = _pose.ParentIndexAt(joint);
            Vector3 parent = _positions[parentJoint];
            Vector3 child = _positions[joint];
            Vector3 position = Vector3.Lerp(parent, child, rng.NextUnitFloat()) * _scale;
            Vector3 normal = child - parent;
            if (normal.LengthSquared() > 1e-12f)
                normal = Vector3.Normalize(normal);
            else
                normal = Vector3.Zero;

            birth = new VfxSurfaceBirth(position, normal);
            return true;
        }
    }
}
