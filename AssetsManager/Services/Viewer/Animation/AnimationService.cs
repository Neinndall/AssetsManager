using System;
using System.Collections.Generic;
using System.Numerics;
using LeagueToolkit.Core.Animation;
using LeagueToolkit.Core.Mesh;
using LeagueToolkit.Hashing;
using AssetsManager.Views.Models.Viewer;
using AssetsManager.Services.Core;
using Quaternion = System.Numerics.Quaternion;

namespace AssetsManager.Services.Viewer.Animation
{
    public class AnimationService : IDisposable
    {
        private readonly Dictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)> _currentPose = new();
        private readonly LogService _logService;

        // Persistent buffers to avoid per-frame allocations
        private Matrix4x4[] _boneTransforms;
        private Matrix4x4[] _finalBoneTransforms;
        private uint[] _jointHashes;
        private GpuSkinningData _gpuSkinningData;

        // Cached model-specific data
        private string _lastModelName;
        private RigResource _lastSkeleton;
        private IList<ModelPart> _lastModelParts;

        private bool _isDisposed;

        public AnimationService(LogService logService = null)
        {
            _logService = logService;
        }

        internal Matrix4x4[] FinalBoneTransforms => _finalBoneTransforms;
        internal GpuSkinningData SkinningData => _gpuSkinningData;

        /// <summary>
        /// Releases cached buffers from the previous model so a new load does not
        /// accumulate GPU memory across multiple model switches.
        /// Buffers are recreated lazily on the next Update call.
        /// </summary>
        public void ClearCache()
        {
            _lastModelName = null;
            _lastSkeleton = null;
            _lastModelParts = null;
            _gpuSkinningData = null;
            _currentPose.Clear();
        }

        private void EnsureBuffers(
            RigResource skeleton,
            SkinnedMesh skin,
            IList<ModelPart> modelParts,
            string modelName)
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(AnimationService));

            int jointCount = skeleton.Joints.Count;
            if (_boneTransforms == null ||
                _boneTransforms.Length != jointCount ||
                _lastModelName != modelName ||
                !ReferenceEquals(_lastSkeleton, skeleton))
            {
                _boneTransforms = new Matrix4x4[jointCount];
                _finalBoneTransforms = new Matrix4x4[jointCount];
                _jointHashes = new uint[jointCount];
                for (int i = 0; i < jointCount; i++)
                {
                    _jointHashes[i] = Elf.HashLower(skeleton.Joints[i].Name);
                }
            }

            if (_lastModelName != modelName ||
                !ReferenceEquals(_lastSkeleton, skeleton) ||
                !ReferenceEquals(_lastModelParts, modelParts))
            {
                _lastModelName = modelName;
                _lastSkeleton = skeleton;
                _lastModelParts = modelParts;

                _gpuSkinningData = GpuSkinningData.TryCreate(skeleton, skin, modelParts);
            }
        }

        public void Update(
            float totalSeconds,
            IAnimationAsset animation,
            RigResource skeleton,
            SkinnedMesh skin,
            IList<ModelPart> modelParts,
            string modelName)
        {
            if (_isDisposed) return;
            if (animation == null || skeleton == null || skin == null)
            {
                return;
            }

            // 1. Ensure buffers are ready (only allocates when model changes)
            EnsureBuffers(skeleton, skin, modelParts, modelName);

            _currentPose.Clear();
            var currentTime = animation.Duration > 0f ? totalSeconds % animation.Duration : 0f;
            animation.Evaluate(currentTime, _currentPose);

            // 2. Calculate Bone Matrices (Hierarchical)
            for (int i = 0; i < skeleton.Joints.Count; i++)
            {
                var joint = skeleton.Joints[i];
                var jointHash = _jointHashes[i];

                var localTransform = joint.LocalTransform;
                if (_currentPose.TryGetValue(jointHash, out var pose))
                {
                    localTransform = Matrix4x4.CreateScale(pose.Scale) *
                                     Matrix4x4.CreateFromQuaternion(pose.Rotation) *
                                     Matrix4x4.CreateTranslation(pose.Translation);
                }

                if (joint.ParentId > -1)
                {
                    _boneTransforms[i] = localTransform * _boneTransforms[joint.ParentId];
                }
                else
                {
                    _boneTransforms[i] = localTransform;
                }
            }

            // 3. Final Skinning Matrices for GPU vertex shader
            for (int i = 0; i < skeleton.Joints.Count; i++)
            {
                _finalBoneTransforms[i] = skeleton.Joints[i].InverseBindTransform * _boneTransforms[i];
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            // Clear persistent buffers so the GC can reclaim the memory
            ClearCache();
            _boneTransforms = null;
            _finalBoneTransforms = null;
            _jointHashes = null;
        }
    }
}
