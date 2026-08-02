using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using AssetsManager.Utils;
using LeagueToolkit.Core.Animation;
using LeagueToolkit.Core.Memory;
using LeagueToolkit.Core.Mesh;
using LeagueToolkit.Hashing;
using System.Numerics;
using AssetsManager.Services.Core;

namespace AssetsManager.Services.Viewer.Vfx
{
    /// <summary>Resolves and decodes resources referenced by an effect graph.</summary>
    internal sealed class VfxResourceResolver
    {
        private static readonly string[] TextureExtensions = { ".tex", ".dds", ".png", ".tga" };
        private static readonly string[] MeshExtensions = { ".scb", ".sco", ".skn" };
        private static readonly string[] SkeletonExtensions = { ".skl" };
        private static readonly string[] AnimationExtensions = { ".anm" };
        private static readonly string[] BinExtensions = { ".bin" };

        private readonly Dictionary<string, VfxResourceIndex> _indexes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, BitmapSource> _textures = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _missingTextures = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (float[] Positions, float[] Uvs, float[] Colors, uint[] Indices)?> _meshes =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, VfxAnimatedMesh> _meshAnimations =
            new(StringComparer.OrdinalIgnoreCase);

        public BitmapSource ResolveTexture(string authoredPath, string searchDirectory)
        {
            if (string.IsNullOrWhiteSpace(authoredPath)) return null;
            string key = CreateKey(authoredPath, searchDirectory);
            if (_textures.TryGetValue(key, out BitmapSource cached)) return cached;
            if (_missingTextures.Contains(key)) return null;

            string resolvedPath = ResolvePath(authoredPath, searchDirectory, TextureExtensions);
            BitmapSource texture = resolvedPath == null ? null : TextureUtils.LoadTextureFromFile(resolvedPath);
            if (texture == null)
            {
                _missingTextures.Add(key);
                return null;
            }

            if (texture.CanFreeze) texture.Freeze();
            _textures[key] = texture;
            return texture;
        }

        public (float[] Positions, float[] Uvs, float[] Colors, uint[] Indices)? ResolveMesh(
            string authoredPath,
            string searchDirectory)
        {
            if (string.IsNullOrWhiteSpace(authoredPath)) return null;

            string key = CreateKey(authoredPath, searchDirectory);
            if (_meshes.TryGetValue(key, out var cached)) return cached;

            string resolvedPath = ResolvePath(authoredPath, searchDirectory, MeshExtensions);
            var mesh = resolvedPath == null ? null : DecodeMesh(resolvedPath);
            _meshes[key] = mesh;
            return mesh;
        }

        public VfxAnimatedMesh ResolveMeshAnimation(
            string meshPath,
            string skeletonPath,
            string animationPath,
            string searchDirectory,
            LogService log = null)
        {
            if (string.IsNullOrWhiteSpace(meshPath) ||
                string.IsNullOrWhiteSpace(skeletonPath) ||
                string.IsNullOrWhiteSpace(animationPath))
            {
                return null;
            }

            string key = CreateKey($"{meshPath}|{skeletonPath}|{animationPath}", searchDirectory);
            if (_meshAnimations.TryGetValue(key, out VfxAnimatedMesh cached)) return cached;

            string resolvedMesh = ResolvePath(meshPath, searchDirectory, new[] { ".skn" });
            string resolvedSkeleton = ResolvePath(skeletonPath, searchDirectory, SkeletonExtensions);
            string resolvedAnimation = ResolvePath(animationPath, searchDirectory, AnimationExtensions);
            if (resolvedMesh == null || resolvedSkeleton == null || resolvedAnimation == null)
            {
                _meshAnimations[key] = null;
                return null;
            }

            try
            {
                var animation = VfxAnimatedMesh.Load(resolvedMesh, resolvedSkeleton, resolvedAnimation);
                _meshAnimations[key] = animation;
                return animation;
            }
            catch (Exception ex)
            {
                log?.LogError(
                    ex,
                    $"Failed to load VFX mesh animation: {resolvedAnimation} (mesh: {resolvedMesh}, skeleton: {resolvedSkeleton}).");
                _meshAnimations[key] = null;
                return null;
            }
        }

        public IReadOnlyList<string> ResolveLinkedBins(
            string authoredPath,
            string wadRoot,
            string searchDirectory)
        {
            if (string.IsNullOrWhiteSpace(authoredPath) || string.IsNullOrWhiteSpace(searchDirectory))
                return Array.Empty<string>();

            string root = !string.IsNullOrWhiteSpace(wadRoot) && Directory.Exists(wadRoot)
                ? Path.GetFullPath(wadRoot)
                : FindAssetRoot(searchDirectory);
            return root == null
                ? Array.Empty<string>()
                : GetIndex(root).ResolveLinkedAll(authoredPath, OrderedExtensions(authoredPath, BinExtensions));
        }

        public void ClearCaches()
        {
            foreach (VfxAnimatedMesh animation in _meshAnimations.Values)
                animation?.Dispose();
            _indexes.Clear();
            _textures.Clear();
            _missingTextures.Clear();
            _meshes.Clear();
            _meshAnimations.Clear();
        }

        private string ResolvePath(string authoredPath, string searchDirectory, IReadOnlyList<string> extensions)
        {
            if (string.IsNullOrWhiteSpace(authoredPath) || string.IsNullOrWhiteSpace(searchDirectory)) return null;

            string directPath = Path.Combine(
                searchDirectory,
                authoredPath.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar));
            foreach (string extension in OrderedExtensions(authoredPath, extensions))
            {
                string candidate = Path.ChangeExtension(directPath, extension);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }

            string root = FindAssetRoot(searchDirectory);
            if (root == null) return null;
            return GetIndex(root).Resolve(authoredPath, OrderedExtensions(authoredPath, extensions));
        }

        private VfxResourceIndex GetIndex(string root)
        {
            string fullRoot = Path.GetFullPath(root);
            if (_indexes.TryGetValue(fullRoot, out VfxResourceIndex index)) return index;
            index = VfxResourceIndex.Build(fullRoot);
            _indexes[fullRoot] = index;
            return index;
        }

        private static string FindAssetRoot(string directory)
        {
            var current = new DirectoryInfo(Path.GetFullPath(directory));
            string best = null;
            while (current != null)
            {
                if (current.Name.EndsWith(".wad.client", StringComparison.OrdinalIgnoreCase) ||
                    (Directory.Exists(Path.Combine(current.FullName, "assets")) && Directory.Exists(Path.Combine(current.FullName, "data"))))
                {
                    return current.FullName;
                }
                if (Directory.Exists(Path.Combine(current.FullName, "assets")) ||
                    Directory.Exists(Path.Combine(current.FullName, "data")))
                {
                    best ??= current.FullName;
                }
                current = current.Parent;
            }
            return best;
        }

        private static string[] OrderedExtensions(string authoredPath, IReadOnlyList<string> supported)
        {
            string authoredExtension = Path.GetExtension(authoredPath);
            return supported
                .Prepend(authoredExtension)
                .Where(extension => !string.IsNullOrWhiteSpace(extension))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static (float[] Positions, float[] Uvs, float[] Colors, uint[] Indices)? DecodeMesh(string path)
        {
            if (path.EndsWith(".skn", StringComparison.OrdinalIgnoreCase))
                return DecodeSkinnedMesh(path);

            using var stream = File.OpenRead(path);
            var source = path.EndsWith(".sco", StringComparison.OrdinalIgnoreCase)
                ? LeagueToolkit.Core.Mesh.StaticMesh.ReadAscii(stream)
                : LeagueToolkit.Core.Mesh.StaticMesh.ReadBinary(stream);
            if (source.Faces.Count == 0) return null;

            int vertexCount = source.Faces.Count * 3;
            var positions = new float[vertexCount * 3];
            var uvs = new float[vertexCount * 2];
            var colors = new float[vertexCount * 4];
            var indices = new uint[vertexCount];
            int positionOffset = 0;
            int uvOffset = 0;
            int colorOffset = 0;

            foreach (var face in source.Faces)
            {
                int[] vertexIds = { face.VertexId0, face.VertexId1, face.VertexId2 };
                System.Numerics.Vector2[] faceUvs = { face.UV0, face.UV1, face.UV2 };
                for (int corner = 0; corner < 3; corner++)
                {
                    var position = source.Vertices[vertexIds[corner]];
                    positions[positionOffset++] = position.X;
                    positions[positionOffset++] = position.Y;
                    positions[positionOffset++] = position.Z;
                    uvs[uvOffset++] = faceUvs[corner].X;
                    uvs[uvOffset++] = faceUvs[corner].Y;
                    var color = source.HasVertexColors && vertexIds[corner] < source.VertexColors.Count
                        ? source.VertexColors[vertexIds[corner]]
                        : LeagueToolkit.Core.Primitives.Color.One;
                    colors[colorOffset++] = color.R;
                    colors[colorOffset++] = color.G;
                    colors[colorOffset++] = color.B;
                    colors[colorOffset++] = color.A;
                    indices[(positionOffset / 3) - 1] = (uint)((positionOffset / 3) - 1);
                }
            }

            return (positions, uvs, colors, indices);
        }

        private static (float[] Positions, float[] Uvs, float[] Colors, uint[] Indices)? DecodeSkinnedMesh(string path)
        {
            using var mesh = LeagueToolkit.Core.Mesh.SkinnedMesh.ReadFromSimpleSkin(path);
            var sourcePositions = mesh.VerticesView
                .GetAccessor(LeagueToolkit.Core.Memory.VertexElement.POSITION.Name)
                .AsVector3Array();
            var sourceUvs = mesh.VerticesView
                .GetAccessor(LeagueToolkit.Core.Memory.VertexElement.TEXCOORD_0.Name)
                .AsVector2Array();

            var positions = new float[sourcePositions.Count * 3];
            var uvs = new float[sourceUvs.Count * 2];
            var colors = new float[sourcePositions.Count * 4];
            for (int index = 0; index < sourcePositions.Count; index++)
            {
                var position = sourcePositions[index];
                positions[index * 3] = position.X;
                positions[index * 3 + 1] = position.Y;
                positions[index * 3 + 2] = position.Z;
                colors[index * 4] = 1f;
                colors[index * 4 + 1] = 1f;
                colors[index * 4 + 2] = 1f;
                colors[index * 4 + 3] = 1f;
            }
            for (int index = 0; index < sourceUvs.Count; index++)
            {
                var uv = sourceUvs[index];
                uvs[index * 2] = uv.X;
                uvs[index * 2 + 1] = uv.Y;
            }

            var indices = new uint[mesh.Indices.Count];
            for (int index = 0; index < indices.Length; index++)
                indices[index] = mesh.Indices[index];
            return indices.Length == 0 ? null : (positions, uvs, colors, indices);
        }

        private static string CreateKey(string path, string directory)
            => path.Replace('\\', '/').ToLowerInvariant() + "|" + Path.GetFullPath(directory);

    }

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
}
