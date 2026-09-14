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
        private IAnimationAsset _lastAnimation;
        private IAnimationAsset _evaluatedAnimation;
        private RigResource _evaluatedSkeleton;
        private float _evaluatedTime = float.NaN;

        // Cached model-specific data
        private string _lastModelName;
        private RigResource _lastSkeleton;
        private SkinnedMesh _lastSkin;
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
            _lastSkin = null;
            _lastModelParts = null;
            _lastAnimation = null;
            _evaluatedAnimation = null;
            _evaluatedSkeleton = null;
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
                !ReferenceEquals(_lastSkin, skin) ||
                !ReferenceEquals(_lastModelParts, modelParts))
            {
                _lastModelName = modelName;
                _lastSkeleton = skeleton;
                _lastSkin = skin;
                _lastModelParts = modelParts;

                _gpuSkinningData = GpuSkinningData.TryCreate(
                    skeleton,
                    skin,
                    modelParts,
                    out string failureReason);
                if (_gpuSkinningData == null)
                {
                    _logService?.LogWarning(
                        $"GPU skinning unavailable for model '{modelName}': {failureReason ?? "Unsupported skin data."} The model will remain in bind pose.");
                }
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

            // 1. Ensure buffers are ready (only allocates when model data changes)
            EnsureBuffers(skeleton, skin, modelParts, modelName);
            _lastAnimation = animation;

            EvaluatePose(totalSeconds, animation, skeleton);

            // Final Skinning Matrices for GPU vertex shader
            for (int i = 0; i < skeleton.Joints.Count; i++)
            {
                _finalBoneTransforms[i] = skeleton.Joints[i].InverseBindTransform * _boneTransforms[i];
            }
        }

        /// <summary>
        /// Evaluates the cached animation at an arbitrary clip time and returns a bone's
        /// world-space transform. Seeking needs this path because the runtime replays VFX
        /// events at many intermediate times rather than only at the final pose.
        /// </summary>
        public bool TrySampleBoneTransform(
            float totalSeconds,
            string boneName,
            uint boneHash,
            out Matrix4x4 transform)
        {
            transform = Matrix4x4.Identity;
            if (_isDisposed || _lastAnimation == null || _lastSkeleton == null || _boneTransforms == null)
                return false;

            EvaluatePose(totalSeconds, _lastAnimation, _lastSkeleton);
            return TryGetBoneTransform(boneName, boneHash, out transform);
        }

        private void EvaluatePose(float totalSeconds, IAnimationAsset animation, RigResource skeleton)
        {
            if (ReferenceEquals(animation, _evaluatedAnimation) && ReferenceEquals(skeleton, _evaluatedSkeleton) && totalSeconds == _evaluatedTime)
                return;
            _evaluatedAnimation = animation;
            _evaluatedSkeleton = skeleton;
            _evaluatedTime = totalSeconds;
            _currentPose.Clear();
            float currentTime = animation.Duration > 0f
                ? Math.Clamp(totalSeconds, 0f, animation.Duration)
                : 0f;
            animation.Evaluate(currentTime, _currentPose);

            // Calculate bone matrices hierarchically so attachment consumers receive the
            // same transforms as GPU skinning, including joints omitted by the animation.
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

                _boneTransforms[i] = joint.ParentId > -1
                    ? localTransform * _boneTransforms[joint.ParentId]
                    : localTransform;
            }
        }

        private bool TryGetBoneTransform(string boneName, uint boneHash, out Matrix4x4 transform)
        {
            if (!string.IsNullOrWhiteSpace(boneName) && TryGetBoneTransform(boneName, out transform))
                return true;
            return TryGetBoneTransform(boneHash, out transform);
        }

        public bool TryGetBoneTransform(string boneName, out Matrix4x4 transform)
        {
            transform = Matrix4x4.Identity;
            if (_lastSkeleton == null || _boneTransforms == null || string.IsNullOrWhiteSpace(boneName))
                return false;

            for (int i = 0; i < _lastSkeleton.Joints.Count; i++)
            {
                if (string.Equals(_lastSkeleton.Joints[i].Name, boneName, StringComparison.OrdinalIgnoreCase))
                {
                    if (i < _boneTransforms.Length)
                    {
                        transform = _boneTransforms[i];
                        return true;
                    }
                }
            }

            uint elf = Elf.HashLower(boneName);
            uint fnv = Fnv1a.HashLower(boneName);
            return TryGetBoneTransform(elf, out transform) || TryGetBoneTransform(fnv, out transform);
        }

        public bool TryGetBoneTransform(uint boneHash, out Matrix4x4 transform)
        {
            transform = Matrix4x4.Identity;
            if (_lastSkeleton == null || _boneTransforms == null || boneHash == 0)
                return false;

            for (int i = 0; i < _lastSkeleton.Joints.Count; i++)
            {
                if (_jointHashes != null && i < _jointHashes.Length && _jointHashes[i] == boneHash)
                {
                    if (i < _boneTransforms.Length)
                    {
                        transform = _boneTransforms[i];
                        return true;
                    }
                }

                if (Fnv1a.HashLower(_lastSkeleton.Joints[i].Name) == boneHash)
                {
                    if (i < _boneTransforms.Length)
                    {
                        transform = _boneTransforms[i];
                        return true;
                    }
                }
            }

            return false;
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
