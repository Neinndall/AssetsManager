using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Core;
using AssetsManager.Utils;

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
        private readonly Dictionary<string, VfxCubeMapData> _cubeMaps = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _missingCubeMaps = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, VfxMeshData?> _meshes =
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
            BitmapSource texture = null;
            if (resolvedPath != null)
            {
                try
                {
                    texture = TextureUtils.LoadTextureFromFile(resolvedPath);
                }
                catch
                {
                    // LTK treats a texture decode failure as one missing sampler. Keep the
                    // rest of the effect alive and remember the failed lookup for this resolver.
                    texture = null;
                }
            }
            if (texture == null)
            {
                _missingTextures.Add(key);
                return null;
            }

            if (texture.CanFreeze) texture.Freeze();
            _textures[key] = texture;
            return texture;
        }

        public VfxCubeMapData ResolveCubeMap(string authoredPath, string searchDirectory)
        {
            if (string.IsNullOrWhiteSpace(authoredPath) || string.IsNullOrWhiteSpace(searchDirectory)) return null;
            string key = CreateKey(authoredPath, searchDirectory);
            if (_cubeMaps.TryGetValue(key, out VfxCubeMapData cached)) return cached;
            if (_missingCubeMaps.Contains(key)) return null;

            string resolvedPath = ResolvePath(authoredPath, searchDirectory, TextureExtensions);
            VfxCubeMapData cube = resolvedPath == null ? null : VfxCubeMapDecoder.Decode(resolvedPath);
            if (cube?.IsValid != true)
            {
                _missingCubeMaps.Add(key);
                return null;
            }

            _cubeMaps[key] = cube;
            return cube;
        }

        public VfxMeshData? ResolveMesh(
            string authoredPath,
            string searchDirectory)
            => ResolveMesh(authoredPath, Array.Empty<uint>(), Array.Empty<uint>(), searchDirectory);

        public VfxMeshData? ResolveMesh(
            string authoredPath,
            IReadOnlyList<uint> submeshesToDraw,
            IReadOnlyList<uint> submeshesToDrawAlways,
            string searchDirectory)
        {
            if (string.IsNullOrWhiteSpace(authoredPath) || string.IsNullOrWhiteSpace(searchDirectory)) return null;

            submeshesToDraw ??= Array.Empty<uint>();
            submeshesToDrawAlways ??= Array.Empty<uint>();
            string drawKey = string.Join(",", submeshesToDraw.OrderBy(static value => value));
            string alwaysKey = string.Join(",", submeshesToDrawAlways.OrderBy(static value => value));
            string key = CreateKey($"{authoredPath}|draw:{drawKey}|always:{alwaysKey}", searchDirectory);
            if (_meshes.TryGetValue(key, out var cached)) return cached;

            string resolvedPath = ResolvePath(authoredPath, searchDirectory, MeshExtensions);
            VfxMeshData? mesh = null;
            if (resolvedPath != null)
            {
                try
                {
                    mesh = VfxMeshDecoder.DecodeMesh(resolvedPath, submeshesToDraw, submeshesToDrawAlways);
                }
                catch
                {
                    // LTK reports unsupported/corrupt geometry as a failed asset load instead
                    // of aborting the VFX system. This also covers authored .tmesh/.gmesh files,
                    // which LTK 1.19.4 recognizes but does not decode.
                    mesh = null;
                }
            }
            _meshes[key] = mesh;
            return mesh;
        }

        public VfxMeshData? ResolveAttachedMesh(
            string authoredPath,
            IReadOnlyList<uint> submeshHashes,
            string searchDirectory,
            float skinScale = 1f)
            => ResolveAttachedMesh(
                authoredPath,
                submeshHashes,
                Array.Empty<uint>(),
                Array.Empty<uint>(),
                searchDirectory,
                skinScale);

        public VfxMeshData? ResolveAttachedMesh(
            string authoredPath,
            IReadOnlyList<uint> submeshesToDraw,
            IReadOnlyList<uint> submeshesToDrawAlways,
            IReadOnlyList<uint> hiddenSubmeshes,
            string searchDirectory,
            float skinScale = 1f)
            => ResolveAttachedMesh(
                authoredPath,
                null,
                submeshesToDraw,
                submeshesToDrawAlways,
                hiddenSubmeshes,
                searchDirectory,
                skinScale);

        public VfxMeshData? ResolveAttachedMesh(
            string authoredPath,
            string skeletonPath,
            IReadOnlyList<uint> submeshesToDraw,
            IReadOnlyList<uint> submeshesToDrawAlways,
            IReadOnlyList<uint> hiddenSubmeshes,
            string searchDirectory,
            float skinScale = 1f)
        {
            if (string.IsNullOrWhiteSpace(authoredPath) || string.IsNullOrWhiteSpace(searchDirectory))
                return null;

            submeshesToDraw ??= Array.Empty<uint>();
            submeshesToDrawAlways ??= Array.Empty<uint>();
            hiddenSubmeshes ??= Array.Empty<uint>();
            string drawKey = string.Join(",", submeshesToDraw.OrderBy(static value => value));
            string alwaysKey = string.Join(",", submeshesToDrawAlways.OrderBy(static value => value));
            string hiddenKey = string.Join(",", hiddenSubmeshes.OrderBy(static value => value));
            float resolvedScale = float.IsFinite(skinScale) && skinScale > 0f ? skinScale : 1f;
            string skeletonKey = string.IsNullOrWhiteSpace(skeletonPath) ? "none" : skeletonPath;
            string key = CreateKey(
                $"{authoredPath}|attached|skeleton:{skeletonKey}|draw:{drawKey}|always:{alwaysKey}|hidden:{hiddenKey}|scale:{resolvedScale:R}",
                searchDirectory);
            if (_meshes.TryGetValue(key, out var cached)) return cached;

            string resolvedPath = ResolvePath(authoredPath, searchDirectory, new[] { ".skn" });
            string resolvedSkeleton = string.IsNullOrWhiteSpace(skeletonPath)
                ? null
                : ResolvePath(skeletonPath, searchDirectory, SkeletonExtensions);
            VfxMeshData? mesh = null;
            if (resolvedPath != null)
            {
                try
                {
                    mesh = VfxMeshDecoder.DecodeAttachedSkinnedMesh(
                        resolvedPath,
                        submeshesToDraw,
                        submeshesToDrawAlways,
                        hiddenSubmeshes,
                        resolvedScale,
                        resolvedSkeleton);
                }
                catch
                {
                    // A broken owner mesh is local to the attached-mesh resource. LTK keeps
                    // the remaining particle system running when that geometry cannot decode.
                    mesh = null;
                }
            }
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
            _cubeMaps.Clear();
            _missingCubeMaps.Clear();
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

        private static string CreateKey(string path, string directory)
            => PathUtils.NormalizeSeparators(path).ToLowerInvariant() + "|" + Path.GetFullPath(directory);

    }
}
