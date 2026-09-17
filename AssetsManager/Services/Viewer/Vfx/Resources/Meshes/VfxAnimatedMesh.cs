using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using LeagueToolkit.Core.Animation;
using LeagueToolkit.Core.Memory;
using LeagueToolkit.Core.Mesh;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Viewer.Vfx.Resources
{
    /// <summary>CPU skinning state for an authored VFX .skn + .skl + .anm resource.</summary>
    internal sealed class VfxAnimatedMesh : IDisposable
    {
        private readonly Vector3[] _positions;
        private readonly (byte x, byte y, byte z, byte w)[] _blendIndices;
        private readonly Vector4[] _blendWeights;
        private readonly RigResource _skeleton;
        private readonly IAnimationAsset _animation;
        private readonly uint[] _jointHashes;
        private readonly Matrix4x4[] _boneTransforms;
        private readonly Matrix4x4[] _finalBoneTransforms;
        private readonly Dictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)> _pose = new();
        private readonly float[] _output;

        private VfxAnimatedMesh(
            Vector3[] positions,
            (byte x, byte y, byte z, byte w)[] blendIndices,
            Vector4[] blendWeights,
            RigResource skeleton,
            IAnimationAsset animation)
        {
            _positions = positions;
            _blendIndices = blendIndices;
            _blendWeights = blendWeights;
            _skeleton = skeleton;
            _animation = animation;
            _jointHashes = skeleton.Joints.Select(joint => Elf.HashLower(joint.Name)).ToArray();
            _boneTransforms = new Matrix4x4[skeleton.Joints.Count];
            _finalBoneTransforms = new Matrix4x4[skeleton.Joints.Count];
            _output = new float[positions.Length * 3];
        }

        public static VfxAnimatedMesh Load(string meshPath, string skeletonPath, string animationPath)
        {
            using var mesh = SkinnedMesh.ReadFromSimpleSkin(meshPath);
            Vector3[] positions = mesh.VerticesView
                .GetAccessor(VertexElement.POSITION.Name)
                .AsVector3Array()
                .ToArray();
            var blendIndices = mesh.VerticesView
                .GetAccessor(VertexElement.BLEND_INDEX.Name)
                .AsXyzwU8Array()
                .ToArray();
            Vector4[] blendWeights = mesh.VerticesView
                .GetAccessor(VertexElement.BLEND_WEIGHT.Name)
                .AsVector4Array()
                .ToArray();

            RigResource skeleton;
            using (var stream = File.OpenRead(skeletonPath))
                skeleton = new RigResource(stream);
            IAnimationAsset animation;
            using (var stream = File.OpenRead(animationPath))
                animation = AnimationAsset.Load(stream);

            return new VfxAnimatedMesh(positions, blendIndices, blendWeights, skeleton, animation);
        }

        public float[] Evaluate(float seconds)
        {
            float duration = Math.Max(0.0001f, _animation.Duration);
            _pose.Clear();
            _animation.Evaluate(seconds % duration, _pose);

            for (int index = 0; index < _skeleton.Joints.Count; index++)
            {
                var joint = _skeleton.Joints[index];
                Matrix4x4 localTransform = joint.LocalTransform;
                if (_pose.TryGetValue(_jointHashes[index], out var pose))
                {
                    localTransform = Matrix4x4.CreateScale(pose.Scale) *
                                     Matrix4x4.CreateFromQuaternion(pose.Rotation) *
                                     Matrix4x4.CreateTranslation(pose.Translation);
                }
                _boneTransforms[index] = joint.ParentId > -1
                    ? localTransform * _boneTransforms[joint.ParentId]
                    : localTransform;
            }

            for (int index = 0; index < _skeleton.Joints.Count; index++)
                _finalBoneTransforms[index] = _skeleton.Joints[index].InverseBindTransform * _boneTransforms[index];

            int influenceCount = _skeleton.Influences.Count;
            int boneCount = _finalBoneTransforms.Length;
            for (int index = 0; index < _positions.Length; index++)
            {
                var blendIndex = _blendIndices[index];
                Vector4 weight = _blendWeights[index];
                int i0 = ResolveBone(blendIndex.x, influenceCount, boneCount);
                int i1 = ResolveBone(blendIndex.y, influenceCount, boneCount);
                int i2 = ResolveBone(blendIndex.z, influenceCount, boneCount);
                int i3 = ResolveBone(blendIndex.w, influenceCount, boneCount);
                Matrix4x4 skinning =
                    _finalBoneTransforms[i0] * weight.X +
                    _finalBoneTransforms[i1] * weight.Y +
                    _finalBoneTransforms[i2] * weight.Z +
                    _finalBoneTransforms[i3] * weight.W;
                Vector3 position = Vector3.Transform(_positions[index], skinning);
                _output[index * 3] = position.X;
                _output[index * 3 + 1] = position.Y;
                _output[index * 3 + 2] = position.Z;
            }
            return _output;
        }

        private int ResolveBone(byte blendIndex, int influenceCount, int boneCount)
        {
            int joint = blendIndex < influenceCount ? _skeleton.Influences[blendIndex] : 0;
            return joint >= 0 && joint < boneCount ? joint : 0;
        }

        public void Dispose() => _animation.Dispose();
    }

    /// <summary>
}