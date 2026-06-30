using System;
using System.Collections.Generic;
using System.Numerics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Media3D;
using LeagueToolkit.Core.Animation;
using LeagueToolkit.Core.Mesh;
using LeagueToolkit.Hashing;
using LeagueToolkit.Core.Memory;
using AssetsManager.Views.Models.Viewer;
using AssetsManager.Services;
using AssetsManager.Services.Core;
using Quaternion = System.Numerics.Quaternion;

namespace AssetsManager.Services.Viewer
{
    public class AnimationPlayer : IDisposable
    {
        private readonly Dictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)> _currentPose = new();
        private readonly LogService _logService;

        // Persistent buffers to avoid per-frame allocations
        private Matrix4x4[] _boneTransforms;
        private Matrix4x4[] _finalBoneTransforms;
        private Vector3[] _skinnedVertices;
        private uint[] _jointHashes;

        // Cached model-specific data
        private string _lastModelName;
        private IVertexBufferView _lastVerticesView;
        private Vector3[] _cachedPositions;
        private (byte x, byte y, byte z, byte w)[] _cachedBlendIndices;
        private Vector4[] _cachedBlendWeights;

        private bool _isDisposed;

        public AnimationPlayer(LogService logService)
        {
            _logService = logService;
        }

        /// <summary>
        /// Releases cached buffers from the previous model so a new load does not
        /// accumulate GPU/CPU memory across multiple model switches.
        /// Buffers are recreated lazily on the next Update call.
        /// </summary>
        public void ClearCache()
        {
            _lastModelName = null;
            _lastVerticesView = null;
            _cachedPositions = null;
            _cachedBlendIndices = null;
            _cachedBlendWeights = null;
            _skinnedVertices = null;
            _currentPose.Clear();
        }

        private void EnsureBuffers(RigResource skeleton, SkinnedMesh skin, string modelName)
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(AnimationPlayer));

            int jointCount = skeleton.Joints.Count;
            if (_boneTransforms == null || _boneTransforms.Length != jointCount || _lastModelName != modelName)
            {
                _boneTransforms = new Matrix4x4[jointCount];
                _finalBoneTransforms = new Matrix4x4[jointCount];
                _jointHashes = new uint[jointCount];
                for (int i = 0; i < jointCount; i++)
                {
                    _jointHashes[i] = Elf.HashLower(skeleton.Joints[i].Name);
                }
            }

            if (_lastModelName != modelName || _lastVerticesView != skin.VerticesView)
            {
                _lastModelName = modelName;
                _lastVerticesView = skin.VerticesView;

                var posAccessor = skin.VerticesView.GetAccessor(VertexElement.POSITION.Name);
                _cachedPositions = posAccessor.AsVector3Array().ToArray();

                var blendIdxAccessor = skin.VerticesView.GetAccessor(VertexElement.BLEND_INDEX.Name);
                _cachedBlendIndices = blendIdxAccessor.AsXyzwU8Array().ToArray();

                var blendWeightAccessor = skin.VerticesView.GetAccessor(VertexElement.BLEND_WEIGHT.Name);
                _cachedBlendWeights = blendWeightAccessor.AsVector4Array().ToArray();

                _skinnedVertices = new Vector3[_cachedPositions.Length];
            }
        }

        public void Update(float totalSeconds, IAnimationAsset animation, RigResource skeleton, SkinnedMesh skin,
            System.Collections.Generic.IList<ModelPart> modelParts, string modelName)
        {
            if (_isDisposed) return;
            if (animation == null || skeleton == null || skin == null)
            {
                return;
            }

            // 1. Ensure buffers are ready (only allocates when model changes)
            EnsureBuffers(skeleton, skin, modelName);

            var currentTime = totalSeconds % animation.Duration;
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

            // 3. Final Skinning Matrices
            for (int i = 0; i < skeleton.Joints.Count; i++)
            {
                _finalBoneTransforms[i] = skeleton.Joints[i].InverseBindTransform * _boneTransforms[i];
            }

            if (_cachedPositions.Length == 0) return;

            try
            {
                var influencesCount = skeleton.Influences.Count;
                var boneCount = _finalBoneTransforms.Length;

                // 4. Parallel Linear Blend Skinning (LBS)
                Parallel.For(0, _cachedPositions.Length, i =>
                {
                    var pos = _cachedPositions[i];
                    var indices = _cachedBlendIndices[i];
                    var weights = _cachedBlendWeights[i];

                    var idx0 = indices.x < influencesCount ? skeleton.Influences[indices.x] : (short)0;
                    var idx1 = indices.y < influencesCount ? skeleton.Influences[indices.y] : (short)0;
                    var idx2 = indices.z < influencesCount ? skeleton.Influences[indices.z] : (short)0;
                    var idx3 = indices.w < influencesCount ? skeleton.Influences[indices.w] : (short)0;

                    var i0 = idx0 < boneCount ? idx0 : (short)0;
                    var i1 = idx1 < boneCount ? idx1 : (short)0;
                    var i2 = idx2 < boneCount ? idx2 : (short)0;
                    var i3 = idx3 < boneCount ? idx3 : (short)0;

                    Matrix4x4 skinningMatrix = _finalBoneTransforms[i0] * weights.X +
                                               _finalBoneTransforms[i1] * weights.Y +
                                               _finalBoneTransforms[i2] * weights.Z +
                                               _finalBoneTransforms[i3] * weights.W;

                    _skinnedVertices[i] = Vector3.Transform(pos, skinningMatrix);
                });

                // 5. Update Viewport (WPF) - Offload Point3DCollection creation and freezing to background threads
                var collections = new Point3DCollection[modelParts.Count];

                Parallel.For(0, modelParts.Count, i =>
                {
                    var part = modelParts[i];
                    var range = skin.Ranges[i];
                    var sourceVertexIndices = part.SourceVertexIndices;
                    int vertexCount = sourceVertexIndices?.Length ?? range.VertexCount;

                    // Instantiating on a background thread is allowed in WPF as long as we freeze the object
                    // before passing it to the UI thread.
                    var posCollection = new Point3DCollection(vertexCount);

                    for (int j = 0; j < vertexCount; j++)
                    {
                        var vertexIndex = sourceVertexIndices != null ? sourceVertexIndices[j] : range.StartVertex + j;
                        var skinnedPos = _skinnedVertices[vertexIndex];
                        posCollection.Add(new Point3D(skinnedPos.X, skinnedPos.Y, skinnedPos.Z));
                    }

                    if (posCollection.CanFreeze)
                    {
                        posCollection.Freeze();
                    }

                    collections[i] = posCollection;
                });

                // Apply the frozen collections to the geometry on the UI thread (extremely fast pointer assignment)
                for (int i = 0; i < modelParts.Count; i++)
                {
                    var part = modelParts[i];
                    var geometry = (MeshGeometry3D)part.Geometry.Geometry;
                    geometry.Positions = collections[i];
                }
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Error during skinning.");
                return;
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
