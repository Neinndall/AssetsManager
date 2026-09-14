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
using System.Globalization;
using AssetsManager.Services.Core;

namespace AssetsManager.Services.Viewer.Vfx.Resources
{
    /// <summary>Resolves and decodes resources referenced by an effect graph.</summary>
    internal sealed class VfxResourceResolver : IDisposable
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
            if (string.IsNullOrWhiteSpace(authoredPath) || string.IsNullOrWhiteSpace(searchDirectory)) return null;
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
            if (string.IsNullOrWhiteSpace(authoredPath) || string.IsNullOrWhiteSpace(searchDirectory)) return null;

            string key = CreateKey(authoredPath, searchDirectory);
            if (_meshes.TryGetValue(key, out var cached)) return cached;

            string resolvedPath = ResolvePath(authoredPath, searchDirectory, MeshExtensions);
            var mesh = resolvedPath == null ? null : DecodeMesh(resolvedPath);
            _meshes[key] = mesh;
            return mesh;
        }

        public (float[] Positions, float[] Uvs, float[] Colors, uint[] Indices)? ResolveAttachedMesh(
            string authoredPath,
            IReadOnlyList<uint> submeshHashes,
            string searchDirectory,
            float skinScale = 1f)
        {
            if (string.IsNullOrWhiteSpace(authoredPath) ||
                string.IsNullOrWhiteSpace(searchDirectory) ||
                submeshHashes is not { Count: > 0 }) return null;
            string maskKey = string.Join(",", submeshHashes.OrderBy(static value => value));
            float resolvedScale = float.IsFinite(skinScale) && skinScale > 0f ? skinScale : 1f;
            string key = CreateKey(
                $"{authoredPath}|attached|{maskKey}|scale:{resolvedScale:R}",
                searchDirectory);
            if (_meshes.TryGetValue(key, out var cached)) return cached;
            string resolvedPath = ResolvePath(authoredPath, searchDirectory, new[] { ".skn" });
            var mesh = resolvedPath == null
                ? null
                : DecodeAttachedSkinnedMesh(resolvedPath, submeshHashes, resolvedScale);
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
                string.IsNullOrWhiteSpace(animationPath) ||
                string.IsNullOrWhiteSpace(searchDirectory))
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

        public void Dispose() => ClearCaches();

        internal string ResolvePath(string authoredPath, string searchDirectory, IReadOnlyList<string> extensions)
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
            return best ?? (Directory.Exists(directory) ? Path.GetFullPath(directory) : null);
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

        private static (float[] Positions, float[] Uvs, float[] Colors, uint[] Indices)? DecodeAttachedSkinnedMesh(
            string path,
            IReadOnlyList<uint> submeshHashes,
            float skinScale)
        {
            using var mesh = LeagueToolkit.Core.Mesh.SkinnedMesh.ReadFromSimpleSkin(path);
            var requested = submeshHashes.ToHashSet();
            var filteredIndices = new List<uint>();
            foreach (var range in mesh.Ranges)
            {
                string material = range.Material.TrimEnd('\0');
                if (!requested.Contains(Fnv1a.HashLower(material))) continue;
                var subIndices = mesh.Indices.Slice(range.StartIndex, range.IndexCount);
                bool usesGlobalIndices = true;
                bool usesLocalIndices = range.StartVertex > 0;
                for (int index = 0; index < range.IndexCount; index++)
                {
                    int value = (int)subIndices[index];
                    usesGlobalIndices &= value >= range.StartVertex && value < range.StartVertex + range.VertexCount;
                    usesLocalIndices &= value >= 0 && value < range.VertexCount;
                }
                if (!usesGlobalIndices && !usesLocalIndices) continue;
                int vertexOffset = usesLocalIndices && !usesGlobalIndices ? range.StartVertex : 0;
                for (int index = 0; index < range.IndexCount; index++)
                    filteredIndices.Add((uint)(subIndices[index] + vertexOffset));
            }
            if (filteredIndices.Count == 0) return null;

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
                Vector3 position = sourcePositions[index];
                positions[index * 3] = position.X * skinScale;
                positions[index * 3 + 1] = position.Y * skinScale;
                positions[index * 3 + 2] = position.Z * skinScale;
                colors[index * 4] = colors[index * 4 + 1] = colors[index * 4 + 2] = colors[index * 4 + 3] = 1f;
            }
            for (int index = 0; index < sourceUvs.Count; index++)
            {
                Vector2 uv = sourceUvs[index];
                uvs[index * 2] = uv.X;
                uvs[index * 2 + 1] = uv.Y;
            }
            return (positions, uvs, colors, filteredIndices.ToArray());
        }

        private static string CreateKey(string path, string directory)
            => PathUtils.NormalizeSeparators(path).ToLowerInvariant() + "|" + Path.GetFullPath(directory);

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
    /// Immutable file index shared by every effect in an extracted WAD tree.
    /// Exact authored paths always win; basename fallback is deterministic.
    /// </summary>
    internal sealed class VfxResourceIndex
    {
        private const int ExtractedFileNameLimit = 240;

        private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".tex", ".dds", ".png", ".tga", ".scb", ".sco", ".skn", ".skl", ".anm", ".bin"
        };

        private readonly string _root;
        private readonly Dictionary<string, string> _byRelativePath;
        private readonly Dictionary<string, string> _byHash;
        private readonly Dictionary<string, string[]> _byFileName;

        private VfxResourceIndex(string root)
        {
            _root = Path.GetFullPath(root);
            _byRelativePath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _byHash = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var byName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (string path in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                if (!SupportedExtensions.Contains(Path.GetExtension(path))) continue;

                string fullPath = Path.GetFullPath(path);
                string relativePath = Normalize(Path.GetRelativePath(_root, fullPath));
                _byRelativePath.TryAdd(relativePath, fullPath);

                string ext = Path.GetExtension(fullPath);
                string fileName = Path.GetFileName(fullPath);
                if (!byName.TryGetValue(fileName, out var paths))
                {
                    paths = new List<string>();
                    byName[fileName] = paths;
                }
                paths.Add(fullPath);

                // Index by XxHash64 of the virtual relative path (e.g. assets/characters/lulu/...)
                ulong hash = XxHash64Ext.Hash(relativePath.ToLowerInvariant());
                string hashKey = hash.ToString("x16");
                _byHash.TryAdd(hashKey, fullPath);
                _byHash.TryAdd(hashKey + ext, fullPath);

                if (!relativePath.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) &&
                    !relativePath.StartsWith("data/", StringComparison.OrdinalIgnoreCase))
                {
                    ulong assetsHash = XxHash64Ext.Hash(("assets/" + relativePath).ToLowerInvariant());
                    string assetsHashKey = assetsHash.ToString("x16");
                    _byHash.TryAdd(assetsHashKey, fullPath);
                    _byHash.TryAdd(assetsHashKey + ext, fullPath);
                }

                // If the file on disk was extracted with a 16-hex hash name, also index it
                string stem = Path.GetFileNameWithoutExtension(fullPath);
                if (stem.Length == 16 && ulong.TryParse(stem, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
                {
                    string stemLower = stem.ToLowerInvariant();
                    _byHash.TryAdd(stemLower, fullPath);
                    _byHash.TryAdd(stemLower + ext, fullPath);
                }
            }

            _byFileName = byName.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        }

        public static VfxResourceIndex Build(string root) => new(root);

        public string Resolve(string authoredPath, IReadOnlyList<string> extensions)
            => ResolveAll(authoredPath, extensions).FirstOrDefault();

        /// <summary>
        /// Resolves a declared BIN dependency by authored path.
        /// Truncated extraction names remain supported; unrelated basename matches do not.
        /// </summary>
        public IReadOnlyList<string> ResolveLinkedAll(string authoredPath, IReadOnlyList<string> extensions)
        {
            if (string.IsNullOrWhiteSpace(authoredPath)) return Array.Empty<string>();

            string normalized = Normalize(authoredPath);
            IReadOnlyList<string> exact = ResolveExact(normalized, extensions);
            if (exact.Count > 0) return exact;

            return ResolveTruncated(normalized, extensions);
        }

        public IReadOnlyList<string> ResolveAll(string authoredPath, IReadOnlyList<string> extensions)
        {
            if (string.IsNullOrWhiteSpace(authoredPath)) return Array.Empty<string>();

            string normalized = Normalize(authoredPath);
            IReadOnlyList<string> exact = ResolveExact(normalized, extensions);
            if (exact.Count > 0) return exact;

            IReadOnlyList<string> truncated = ResolveTruncated(normalized, extensions);
            if (truncated.Count > 0) return truncated;

            string authoredDirectory = Normalize(Path.GetDirectoryName(normalized) ?? string.Empty);
            foreach (string extension in extensions)
            {
                string fileName = Path.GetFileNameWithoutExtension(normalized) + extension;
                if (!_byFileName.TryGetValue(fileName, out string[] candidates)) continue;

                int bestScore = candidates.Max(path => SharedSuffixLength(
                    authoredDirectory,
                    Normalize(Path.GetDirectoryName(Path.GetRelativePath(_root, path)) ?? string.Empty)));
                return candidates
                    .Where(path => SharedSuffixLength(
                        authoredDirectory,
                        Normalize(Path.GetDirectoryName(Path.GetRelativePath(_root, path)) ?? string.Empty)) == bestScore)
                    .OrderByDescending(path => SharedSuffixLength(
                        authoredDirectory,
                        Normalize(Path.GetDirectoryName(Path.GetRelativePath(_root, path)) ?? string.Empty)))
                    .ThenBy(path => path.Length)
                    .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }

            return Array.Empty<string>();
        }

        private IReadOnlyList<string> ResolveExact(string normalized, IReadOnlyList<string> extensions)
        {
            foreach (string extension in extensions)
            {
                string candidate = Normalize(Path.ChangeExtension(normalized, extension));
                if (_byRelativePath.TryGetValue(candidate, out string exact)) return new[] { exact };
            }

            // Authored path is a 16-hex hash string (e.g. "aa5a8ee2e6b5d8c4.anm" or "0xaa5a8ee2e6b5d8c4")
            string authoredStem = Path.GetFileNameWithoutExtension(normalized);
            if (authoredStem.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                authoredStem = authoredStem[2..];
            if (authoredStem.Length == 16 && ulong.TryParse(authoredStem, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            {
                string hexLower = authoredStem.ToLowerInvariant();
                if (_byHash.TryGetValue(hexLower, out string matched)) return new[] { matched };
                foreach (string extension in extensions)
                {
                    if (_byHash.TryGetValue(hexLower + extension, out string matchedWithExt))
                        return new[] { matchedWithExt };
                }
            }

            // Unresolved WAD entries keep the hash of their full virtual path, including DATA/ASSETS.
            foreach (string sourceExtension in extensions)
            {
                string virtualPath = Path.ChangeExtension(normalized, sourceExtension).ToLowerInvariant();
                string hash = XxHash64Ext.Hash(virtualPath).ToString("x16");
                if (_byHash.TryGetValue(hash, out string hashedMatch))
                    return new[] { hashedMatch };
                foreach (string storedExtension in extensions)
                {
                    if (_byFileName.TryGetValue(hash + storedExtension, out string[] hashed))
                        return hashed;
                }
            }

            return Array.Empty<string>();
        }

        private IReadOnlyList<string> ResolveTruncated(string normalized, IReadOnlyList<string> extensions)
        {
            string authoredDirectory = Normalize(Path.GetDirectoryName(normalized) ?? string.Empty);
            foreach (string extension in extensions)
            {
                if (!extension.Equals(".bin", StringComparison.OrdinalIgnoreCase)) continue;
                string authoredFileName = Path.GetFileName(Path.ChangeExtension(normalized, extension));
                string authoredStem = Path.GetFileNameWithoutExtension(authoredFileName);
                string[] truncated = _byRelativePath
                    .Where(pair =>
                    {
                        string indexedDirectory = Normalize(Path.GetDirectoryName(pair.Key) ?? string.Empty);
                        string indexedFileName = Path.GetFileName(pair.Key);
                        string indexedStem = StripCollisionSuffix(Path.GetFileNameWithoutExtension(indexedFileName));
                        return (indexedFileName.Length >= ExtractedFileNameLimit ||
                                HasCollisionSuffix(Path.GetFileNameWithoutExtension(indexedFileName))) &&
                               indexedDirectory.Equals(authoredDirectory, StringComparison.OrdinalIgnoreCase) &&
                               authoredStem.StartsWith(indexedStem, StringComparison.OrdinalIgnoreCase);
                    })
                    .OrderBy(pair => HasCollisionSuffix(Path.GetFileNameWithoutExtension(pair.Key)) ? 1 : 0)
                    .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => pair.Value)
                    .ToArray();
                if (truncated.Length > 0) return truncated;
            }

            return Array.Empty<string>();
        }

        private static bool HasCollisionSuffix(string stem)
        {
            int open = stem.LastIndexOf(" (", StringComparison.Ordinal);
            return open >= 0 && stem.EndsWith(')') &&
                   int.TryParse(stem.AsSpan(open + 2, stem.Length - open - 3), out _);
        }

        private static string StripCollisionSuffix(string stem)
        {
            int open = stem.LastIndexOf(" (", StringComparison.Ordinal);
            return open >= 0 && stem.EndsWith(')') &&
                   int.TryParse(stem.AsSpan(open + 2, stem.Length - open - 3), out _)
                ? stem[..open]
                : stem;
        }

        private static string Normalize(string path)
        {
            return PathUtils.NormalizeSeparators(path).TrimStart('/');
        }

        private static int SharedSuffixLength(string left, string right)
        {
            string[] leftParts = left.Split('/', StringSplitOptions.RemoveEmptyEntries);
            string[] rightParts = right.Split('/', StringSplitOptions.RemoveEmptyEntries);
            int score = 0;
            while (score < leftParts.Length && score < rightParts.Length &&
                   string.Equals(leftParts[^(1 + score)], rightParts[^(1 + score)], StringComparison.OrdinalIgnoreCase))
            {
                score++;
            }
            return score;
        }
    }
}
