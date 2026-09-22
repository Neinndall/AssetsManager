using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Animation;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Animation;
using LeagueToolkit.Core.Memory;
using LeagueToolkit.Core.Mesh;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Viewer.Vfx.Resources
{
    /// <summary>
    /// Reusable pose state for one authored VFX SKN/SKL and its selected animation.
    /// The GPU consumes influence-index skinning attributes while children can query the
    /// same pose by joint name, both at particle-local time.
    /// </summary>
    internal sealed class VfxAnimatedMesh : IVfxMeshJointProvider, IDisposable
    {
        private readonly RigResource _skeleton;
        private readonly IAnimationAsset _animation;
        private readonly uint[] _jointHashes;
        private readonly int[] _hierarchyOrder;
        private readonly int[] _hierarchyParents;
        private readonly Dictionary<string, int> _jointsByName;
        private readonly Matrix4x4[] _worldTransforms;
        private readonly Matrix4x4[] _palette;
        private readonly Dictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)> _pose = new();
        private float _evaluatedTime = float.NaN;

        private VfxAnimatedMesh(
            float[] boneIndices,
            float[] boneWeights,
            RigResource skeleton,
            IAnimationAsset animation)
        {
            BoneIndices = boneIndices;
            BoneWeights = boneWeights;
            _skeleton = skeleton;
            _animation = animation;
            _jointHashes = skeleton.Joints.Select(joint => Elf.HashLower(joint.Name)).ToArray();
            (_hierarchyOrder, _hierarchyParents) = AnimationService.BuildHierarchy(
                skeleton.Joints.Select(static joint => (int)joint.ParentId).ToArray());
            _jointsByName = skeleton.Joints
                .Select((joint, index) => (joint.Name, index))
                .Where(static pair => !string.IsNullOrWhiteSpace(pair.Name))
                .GroupBy(static pair => pair.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(static group => group.Key, static group => group.First().index, StringComparer.OrdinalIgnoreCase);
            _worldTransforms = new Matrix4x4[skeleton.Joints.Count];
            _palette = new Matrix4x4[skeleton.Influences.Count];
        }

        internal float[] BoneIndices { get; }
        internal float[] BoneWeights { get; }
        internal int PaletteCount => _palette.Length;

        internal static VfxAnimatedMesh Load(string meshPath, string skeletonPath, string animationPath = null)
        {
            using var mesh = SkinnedMesh.ReadFromSimpleSkin(meshPath);
            RigResource skeleton;
            using (var stream = File.OpenRead(skeletonPath))
                skeleton = new RigResource(stream);
            if (skeleton.Joints.Count == 0 || skeleton.Joints.Count > GpuSkinningData.MaxBones ||
                skeleton.Influences.Count == 0 || skeleton.Influences.Count > GpuSkinningData.MaxBones)
            {
                throw new InvalidDataException("VFX mesh skeleton is outside the supported GPU skinning limits.");
            }

            int vertexCount = mesh.VerticesView.VertexCount;
            var sourceIndices = mesh.VerticesView
                .GetAccessor(VertexElement.BLEND_INDEX.Name)
                .AsXyzwU8Array()
                .ToArray();
            Vector4[] sourceWeights = mesh.VerticesView
                .GetAccessor(VertexElement.BLEND_WEIGHT.Name)
                .AsVector4Array()
                .ToArray();
            if (sourceIndices.Length != vertexCount || sourceWeights.Length != vertexCount)
                throw new InvalidDataException("VFX mesh skinning attributes do not match its vertex count.");

            var boneIndices = new float[vertexCount * 4];
            var boneWeights = new float[vertexCount * 4];
            int influenceCount = skeleton.Influences.Count;
            for (int vertex = 0; vertex < vertexCount; vertex++)
            {
                var indices = sourceIndices[vertex];
                Vector4 weights = sourceWeights[vertex];
                byte[] authoredIndices = { indices.x, indices.y, indices.z, indices.w };
                float[] authoredWeights = { weights.X, weights.Y, weights.Z, weights.W };
                float total = 0f;
                int target = vertex * 4;
                for (int part = 0; part < 4; part++)
                {
                    float weight = authoredWeights[part];
                    byte influence = authoredIndices[part];
                    if (influence >= influenceCount || !float.IsFinite(weight) || weight <= 0f) continue;
                    boneIndices[target + part] = influence;
                    boneWeights[target + part] = weight;
                    total += weight;
                }
                if (total > 0f)
                {
                    for (int part = 0; part < 4; part++)
                        boneWeights[target + part] /= total;
                }
            }

            IAnimationAsset animation = null;
            if (!string.IsNullOrWhiteSpace(animationPath))
            {
                using var stream = File.OpenRead(animationPath);
                animation = AnimationAsset.Load(stream);
            }
            return new VfxAnimatedMesh(boneIndices, boneWeights, skeleton, animation);
        }

        internal ReadOnlySpan<Matrix4x4> EvaluatePalette(float seconds)
        {
            EvaluatePose(seconds);
            for (int influence = 0; influence < _palette.Length; influence++)
            {
                int jointIndex = _skeleton.Influences[influence];
                _palette[influence] = jointIndex >= 0 && jointIndex < _worldTransforms.Length
                    ? _skeleton.Joints[jointIndex].InverseBindTransform * _worldTransforms[jointIndex]
                    : Matrix4x4.Identity;
            }
            return _palette;
        }

        public bool TryGetJointTransform(string jointName, float particleTime, out Matrix4x4 transform)
        {
            if (string.IsNullOrWhiteSpace(jointName) || !_jointsByName.TryGetValue(jointName, out int jointIndex))
            {
                transform = default;
                return false;
            }
            EvaluatePose(particleTime);
            transform = _worldTransforms[jointIndex];
            return true;
        }

        private void EvaluatePose(float seconds)
        {
            float time = float.IsFinite(seconds) ? Math.Max(0f, seconds) : 0f;
            if (_animation is not null)
            {
                float duration = _animation.Duration;
                if (float.IsFinite(duration) && duration > 0f)
                    time %= duration;
            }
            else
            {
                time = 0f;
            }
            if (_evaluatedTime == time) return;

            _pose.Clear();
            _animation?.Evaluate(time, _pose);
            foreach (int index in _hierarchyOrder)
            {
                var joint = _skeleton.Joints[index];
                Matrix4x4 local = joint.LocalTransform;
                if (_pose.TryGetValue(_jointHashes[index], out var pose))
                {
                    local = Matrix4x4.CreateScale(pose.Scale) *
                            Matrix4x4.CreateFromQuaternion(pose.Rotation) *
                            Matrix4x4.CreateTranslation(pose.Translation);
                }
                int parent = _hierarchyParents[index];
                _worldTransforms[index] = parent >= 0 ? local * _worldTransforms[parent] : local;
            }
            _evaluatedTime = time;
        }

        public void Dispose() => _animation?.Dispose();
    }
}