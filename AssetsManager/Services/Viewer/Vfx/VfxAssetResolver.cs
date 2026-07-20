using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Utils;

namespace AssetsManager.Services.Viewer.Vfx
{
    /// <summary>
    /// Resolves VFX asset file paths (particle textures, colour gradients, mesh primitives)
    /// against an extracted project folder. Caches resolved paths to avoid repeating the
    /// expensive recursive directory scans on every load.
    /// </summary>
    public sealed class VfxAssetResolver
    {
        private readonly Dictionary<string, BitmapSource> _resolvedTextureCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _resolvedMeshCache = new(StringComparer.OrdinalIgnoreCase);

        public BitmapSource ResolveTexture(string texturePath, string searchDirectory)
        {
            if (string.IsNullOrEmpty(texturePath)) return null;

            string cacheKey = texturePath + "|" + searchDirectory;
            if (_resolvedTextureCache.TryGetValue(cacheKey, out var cached))
                return cached;
            if (_resolvedTextureCache.TryGetValue(cacheKey + ":null", out _))
                return null;

            string baseName = Path.GetFileNameWithoutExtension(texturePath);
            string originalExt = Path.GetExtension(texturePath);
            string championName = Path.GetFileName(searchDirectory);

            var extensionsToTry = new List<string> { originalExt, ".dds", ".tex", ".png", ".tga" };
            var uniqueExts = new List<string>();
            foreach (var ext in extensionsToTry)
            {
                if (!string.IsNullOrEmpty(ext) && !uniqueExts.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)))
                    uniqueExts.Add(ext);
            }

            foreach (var ext in uniqueExts)
            {
                string candidateName = baseName + ext;

                string targetPath = Path.Combine(searchDirectory, candidateName);
                if (File.Exists(targetPath)) return Cache(texturePath, searchDirectory, TextureUtils.LoadTextureFromFile(targetPath));

                string wadRoot = ResolveWadRoot(searchDirectory);
                if (!string.IsNullOrEmpty(wadRoot) && Directory.Exists(wadRoot))
                {
                    string relativePath = Path.ChangeExtension(texturePath, ext).Replace('/', Path.DirectorySeparatorChar);
                    string directWadPath = Path.Combine(wadRoot, relativePath);
                    if (File.Exists(directWadPath)) return Cache(texturePath, searchDirectory, TextureUtils.LoadTextureFromFile(directWadPath));

                    var directFolders = new[]
                    {
                        Path.Combine(wadRoot, "assets", "characters", championName, "skins", "base", "particles"),
                        Path.Combine(wadRoot, "data", "characters", championName, "skins", "base", "particles"),
                        Path.Combine(wadRoot, "assets", "particles"),
                        Path.Combine(wadRoot, "assets", "shared", "particles"),
                        Path.Combine(wadRoot, "data", "particles"),
                        Path.Combine(wadRoot, "data", "shared", "particles"),
                        Path.Combine(wadRoot, "assets", "characters", championName, "skins", "base"),
                        Path.Combine(wadRoot, "data", "characters", championName, "skins", "base"),
                        Path.Combine(searchDirectory, "textures")
                    };

                    foreach (var folder in directFolders)
                    {
                        if (Directory.Exists(folder))
                        {
                            string directPath = Path.Combine(folder, candidateName);
                            if (File.Exists(directPath)) return Cache(texturePath, searchDirectory, TextureUtils.LoadTextureFromFile(directPath));
                        }
                    }

                    try
                    {
                        var files = Directory.GetFiles(wadRoot, candidateName, SearchOption.AllDirectories);
                        if (files.Length > 0)
                        {
                            var loaded = TextureUtils.LoadTextureFromFile(files[0]);
                            if (loaded != null) return Cache(texturePath, searchDirectory, loaded);
                        }
                    }
                    catch { }
                }
            }

            _resolvedTextureCache[cacheKey + ":null"] = null;
            return null;
        }

        public (float[] Positions, float[] Uvs, uint[] Indices)? ResolveMesh(string meshPath, string searchDirectory)
        {
            if (string.IsNullOrEmpty(meshPath)) return null;

            string meshCacheKey = meshPath + "|" + searchDirectory;
            if (_resolvedMeshCache.TryGetValue(meshCacheKey, out var cachedPath))
                return cachedPath != null ? DecodeMesh(cachedPath) : null;

            string fileName = Path.GetFileName(meshPath);
            string wadRoot = ResolveWadRoot(searchDirectory);
            string resolvedPath = null;

            string directPath = Path.Combine(searchDirectory, fileName);
            if (File.Exists(directPath)) resolvedPath = directPath;

            if (resolvedPath == null && !string.IsNullOrEmpty(wadRoot) && Directory.Exists(wadRoot))
            {
                string wadRelative = Path.Combine(wadRoot, meshPath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(wadRelative)) resolvedPath = wadRelative;

                if (resolvedPath == null)
                {
                    string championName = Path.GetFileName(searchDirectory);
                    var searchFolders = new[]
                    {
                        Path.Combine(wadRoot, "assets", "characters", championName, "skins", "base", "particles"),
                        Path.Combine(wadRoot, "assets", "particles"),
                        Path.Combine(wadRoot, "assets", "shared", "particles"),
                        searchDirectory
                    };

                    foreach (var folder in searchFolders)
                    {
                        if (!Directory.Exists(folder)) continue;
                        string candidate = Path.Combine(folder, fileName);
                        if (File.Exists(candidate)) { resolvedPath = candidate; break; }
                    }
                }

                if (resolvedPath == null)
                {
                    try
                    {
                        var files = Directory.GetFiles(wadRoot, fileName, SearchOption.AllDirectories);
                        if (files.Length > 0) resolvedPath = files[0];
                    }
                    catch { }
                }
            }

            _resolvedMeshCache[meshCacheKey] = resolvedPath;
            return resolvedPath != null ? DecodeMesh(resolvedPath) : null;
        }

        public void ClearCaches()
        {
            _resolvedTextureCache.Clear();
            _resolvedMeshCache.Clear();
        }

        private BitmapSource Cache(string texturePath, string searchDirectory, BitmapSource loaded)
        {
            if (loaded == null) return null;
            _resolvedTextureCache[texturePath + "|" + searchDirectory] = loaded;
            return loaded;
        }

        private static string ResolveWadRoot(string searchDirectory)
        {
            string wadRoot = searchDirectory;
            for (int i = 0; i < 4; i++)
            {
                if (string.IsNullOrEmpty(wadRoot)) break;
                if (Directory.Exists(Path.Combine(wadRoot, "assets")) || Directory.Exists(Path.Combine(wadRoot, "data")))
                    break;
                wadRoot = Path.GetDirectoryName(wadRoot);
            }
            return wadRoot;
        }

        private static (float[] Positions, float[] Uvs, uint[] Indices)? DecodeMesh(string resolvedPath)
        {
            try
            {
                using var stream = File.OpenRead(resolvedPath);
                var staticMesh = resolvedPath.EndsWith(".sco", StringComparison.OrdinalIgnoreCase)
                    ? LeagueToolkit.Core.Mesh.StaticMesh.ReadAscii(stream)
                    : LeagueToolkit.Core.Mesh.StaticMesh.ReadBinary(stream);

                int faces = staticMesh.Faces.Count;
                if (faces == 0) return null;

                var positions = new float[faces * 3 * 3];
                var uvs = new float[faces * 3 * 2];
                var indices = new uint[faces * 3];
                int vp = 0, vu = 0;

                for (int f = 0; f < faces; f++)
                {
                    var face = staticMesh.Faces[f];
                    int[] vid = { face.VertexId0, face.VertexId1, face.VertexId2 };
                    System.Numerics.Vector2[] fuv = { face.UV0, face.UV1, face.UV2 };

                    for (int k = 0; k < 3; k++)
                    {
                        var v = staticMesh.Vertices[vid[k]];
                        positions[vp++] = v.X; positions[vp++] = v.Y; positions[vp++] = v.Z;
                        uvs[vu++] = fuv[k].X; uvs[vu++] = fuv[k].Y;
                        indices[f * 3 + k] = (uint)(f * 3 + k);
                    }
                }

                return (positions, uvs, indices);
            }
            catch
            {
                return null;
            }
        }

        public static uint Fnv1a(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            uint hash = 2166136261;
            foreach (char c in text.ToLowerInvariant())
            {
                hash ^= c;
                hash *= 16777619;
            }
            return hash;
        }
    }
}
