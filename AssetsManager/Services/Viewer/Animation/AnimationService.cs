using System;
using System.Collections.Generic;
using System.Linq;
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
        private Matrix4x4[] _baseBoneTransforms;
        private Matrix4x4[] _localTransforms;
        private Matrix4x4[] _finalBoneTransforms;
        private uint[] _jointHashes;
        private uint[] _jointFnvHashes;
        private int[] _hierarchyOrder;
        private int[] _hierarchyParents;
        private IReadOnlyList<AnimationJointSnapCue> _jointSnapCues = Array.Empty<AnimationJointSnapCue>();
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
            _hierarchyOrder = null;
            _hierarchyParents = null;
            _jointSnapCues = Array.Empty<AnimationJointSnapCue>();
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
                _baseBoneTransforms = new Matrix4x4[jointCount];
                _localTransforms = new Matrix4x4[jointCount];
                _finalBoneTransforms = new Matrix4x4[jointCount];
                _jointHashes = new uint[jointCount];
                _jointFnvHashes = new uint[jointCount];
                for (int i = 0; i < jointCount; i++)
                {
                    string jointName = skeleton.Joints[i].Name;
                    _jointHashes[i] = Elf.HashLower(jointName);
                    _jointFnvHashes[i] = Fnv1a.HashLower(jointName);
                }

                (_hierarchyOrder, _hierarchyParents) = BuildHierarchy(
                    skeleton.Joints.Select(static joint => (int)joint.ParentId).ToArray());
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

        public void SetJointSnapCues(IReadOnlyList<AnimationJointSnapCue> cues)
        {
            _jointSnapCues = cues ?? Array.Empty<AnimationJointSnapCue>();
            _evaluatedTime = float.NaN;
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

        public bool TrySampleRootTransform(float totalSeconds, out Matrix4x4 transform)
        {
            transform = Matrix4x4.Identity;
            if (_isDisposed || _lastAnimation == null || _lastSkeleton == null || _boneTransforms == null)
                return false;

            EvaluatePose(totalSeconds, _lastAnimation, _lastSkeleton);
            int rootIndex = FindRootIndex(_lastSkeleton);
            if (rootIndex < 0 || rootIndex >= _boneTransforms.Length) return false;
            transform = _boneTransforms[rootIndex];
            return true;
        }

        private void EvaluatePose(float totalSeconds, IAnimationAsset animation, RigResource skeleton)
        {
            if (ReferenceEquals(animation, _evaluatedAnimation) &&
                ReferenceEquals(skeleton, _evaluatedSkeleton) &&
                totalSeconds == _evaluatedTime)
            {
                return;
            }

            _evaluatedAnimation = animation;
            _evaluatedSkeleton = skeleton;
            _evaluatedTime = totalSeconds;
            _currentPose.Clear();
            // LTK samples a clip as a looping pose. Fold the scene clock into one pass
            // before handing it to LeagueToolkit, whose animation evaluator itself clamps.
            float currentTime = FoldAnimationTime(totalSeconds, animation.Duration);
            animation.Evaluate(currentTime, _currentPose);

            // Build the base local/world pose first. LTK resolves joint-snap targets and
            // parents against this unmodified pose, then rewrites the snapped local joint.
            for (int orderIndex = 0; orderIndex < _hierarchyOrder.Length; orderIndex++)
            {
                int i = _hierarchyOrder[orderIndex];
                var joint = skeleton.Joints[i];
                Matrix4x4 localTransform = joint.LocalTransform;
                if (_currentPose.TryGetValue(_jointHashes[i], out var pose))
                {
                    localTransform = Matrix4x4.CreateScale(pose.Scale) *
                                     Matrix4x4.CreateFromQuaternion(pose.Rotation) *
                                     Matrix4x4.CreateTranslation(pose.Translation);
                }

                _localTransforms[i] = localTransform;
                int parentIndex = _hierarchyParents[i];
                _baseBoneTransforms[i] = parentIndex > -1
                    ? localTransform * _baseBoneTransforms[parentIndex]
                    : localTransform;
            }

            if (_jointSnapCues.Count > 0)
            {
                double folded = animation.Duration > 0f
                    ? PositiveModulo(totalSeconds, animation.Duration)
                    : 0d;

                foreach (AnimationJointSnapCue snap in _jointSnapCues)
                {
                    if (folded < snap.AtSeconds ||
                        (snap.UntilSeconds.HasValue && folded >= snap.UntilSeconds.Value))
                    {
                        continue;
                    }

                    int jointIndex = FindJointIndex(skeleton, snap.JointHash);
                    int targetIndex = FindJointIndex(skeleton, snap.SnapToHash);
                    if (jointIndex < 0 || targetIndex < 0 || jointIndex == targetIndex)
                        continue;

                    Matrix4x4 snappedLocal =
                        Matrix4x4.CreateTranslation(snap.Offset) * _baseBoneTransforms[targetIndex];
                    int parentIndex = _hierarchyParents[jointIndex];
                    if (parentIndex >= 0 &&
                        Matrix4x4.Invert(_baseBoneTransforms[parentIndex], out Matrix4x4 inverseParent))
                    {
                        snappedLocal *= inverseParent;
                    }

                    // Three.js decomposes the snapped local and writes all three components.
                    // Recompose after decomposition to normalize the quaternion/matrix path.
                    if (Matrix4x4.Decompose(
                            snappedLocal,
                            out Vector3 snappedScale,
                            out Quaternion snappedRotation,
                            out Vector3 snappedTranslation))
                    {
                        _localTransforms[jointIndex] =
                            Matrix4x4.CreateScale(snappedScale) *
                            Matrix4x4.CreateFromQuaternion(snappedRotation) *
                            Matrix4x4.CreateTranslation(snappedTranslation);
                    }
                }
            }

            // Recompose from the (possibly snapped) locals so descendants and VFX
            // attachments follow the same pose that GPU skinning draws.
            for (int orderIndex = 0; orderIndex < _hierarchyOrder.Length; orderIndex++)
            {
                int i = _hierarchyOrder[orderIndex];
                int parentIndex = _hierarchyParents[i];
                _boneTransforms[i] = parentIndex > -1
                    ? _localTransforms[i] * _boneTransforms[parentIndex]
                    : _localTransforms[i];
            }
        }

        private int FindJointIndex(RigResource skeleton, uint hash)
        {
            if (hash == 0u) return -1;
            for (int i = 0; i < skeleton.Joints.Count; i++)
            {
                if ((_jointHashes != null && _jointHashes[i] == hash) ||
                    (_jointFnvHashes != null && _jointFnvHashes[i] == hash))
                {
                    return i;
                }
            }
            return -1;
        }

        internal static float FoldAnimationTime(float time, float duration)
        {
            if (!(duration > 0f) || !float.IsFinite(duration) || !float.IsFinite(time))
                return 0f;
            return (float)PositiveModulo(time, duration);
        }

        internal static bool TryGetBindBoneTransform(
            RigResource skeleton,
            string boneName,
            out Matrix4x4 transform)
        {
            transform = Matrix4x4.Identity;
            if (skeleton == null || string.IsNullOrWhiteSpace(boneName)) return false;

            int jointIndex = -1;
            for (int index = 0; index < skeleton.Joints.Count; index++)
            {
                if (string.Equals(skeleton.Joints[index].Name, boneName, StringComparison.OrdinalIgnoreCase))
                {
                    jointIndex = index;
                    break;
                }
            }
            if (jointIndex < 0) return false;

            (int[] order, int[] parents) = BuildHierarchy(
                skeleton.Joints.Select(static joint => (int)joint.ParentId).ToArray());
            var world = new Matrix4x4[skeleton.Joints.Count];
            foreach (int index in order)
            {
                Matrix4x4 local = skeleton.Joints[index].LocalTransform;
                int parent = parents[index];
                world[index] = parent >= 0 ? local * world[parent] : local;
            }
            transform = world[jointIndex];
            return true;
        }

        internal static bool TryGetBindRootTransform(RigResource skeleton, out Matrix4x4 transform)
        {
            transform = Matrix4x4.Identity;
            int rootIndex = FindRootIndex(skeleton);
            if (rootIndex < 0) return false;
            transform = skeleton.Joints[rootIndex].LocalTransform;
            return true;
        }

        private static int FindRootIndex(RigResource skeleton)
        {
            if (skeleton?.Joints == null || skeleton.Joints.Count == 0) return -1;
            (_, int[] parents) = BuildHierarchy(
                skeleton.Joints.Select(static joint => (int)joint.ParentId).ToArray());
            for (int index = 0; index < parents.Length; index++)
            {
                if (parents[index] < 0) return index;
            }
            return -1;
        }

        internal static (int[] Order, int[] Parents) BuildHierarchy(IReadOnlyList<int> sourceParents)
        {
            int count = sourceParents?.Count ?? 0;
            var parents = new int[count];
            var children = new List<int>[count];
            for (int i = 0; i < count; i++)
            {
                children[i] = new List<int>();
                int parent = sourceParents[i];
                parents[i] = parent >= 0 && parent < count && parent != i ? parent : -1;
            }

            for (int child = 0; child < count; child++)
            {
                int parent = parents[child];
                if (parent >= 0) children[parent].Add(child);
            }

            var order = new List<int>(count);
            var placed = new bool[count];

            void Walk(int root)
            {
                var stack = new Stack<int>();
                stack.Push(root);
                while (stack.Count > 0)
                {
                    int slot = stack.Pop();
                    if (placed[slot]) continue;
                    placed[slot] = true;
                    order.Add(slot);
                    foreach (int child in children[slot])
                    {
                        if (!placed[child]) stack.Push(child);
                    }
                }
            }

            for (int slot = 0; slot < count; slot++)
            {
                if (parents[slot] < 0) Walk(slot);
            }

            // Match LTK: any remaining cycle is broken at its first unplaced slot.
            for (int slot = 0; slot < count; slot++)
            {
                if (placed[slot]) continue;
                parents[slot] = -1;
                Walk(slot);
            }

            return (order.ToArray(), parents);
        }

        private static double PositiveModulo(double value, double span)
        {
            if (!(span > 0d)) return 0d;
            double wrapped = value % span;
            return wrapped < 0d ? wrapped + span : wrapped;
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
            _baseBoneTransforms = null;
            _localTransforms = null;
            _finalBoneTransforms = null;
            _jointHashes = null;
            _jointFnvHashes = null;
        }
    }
}
