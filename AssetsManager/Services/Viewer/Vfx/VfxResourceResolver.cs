using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using AssetsManager.Utils;

namespace AssetsManager.Services.Viewer.Vfx
{
    /// <summary>Resolves and decodes resources referenced by an effect graph.</summary>
    internal sealed class VfxResourceResolver
    {
        private static readonly string[] TextureExtensions = { ".tex", ".dds", ".png", ".tga" };
        private static readonly string[] MeshExtensions = { ".scb", ".sco", ".skn" };
        private static readonly string[] BinExtensions = { ".bin" };

        private readonly Dictionary<string, VfxResourceIndex> _indexes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, BitmapSource> _textures = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _missingTextures = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (float[] Positions, float[] Uvs, uint[] Indices)?> _meshes =
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

        public (float[] Positions, float[] Uvs, uint[] Indices)? ResolveMesh(
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

        private static string FindChampionMesh(string searchDirectory)
        {
            if (string.IsNullOrWhiteSpace(searchDirectory)) return null;
            try
            {
                var dir = new DirectoryInfo(searchDirectory);
                while (dir != null && dir.Exists)
                {
                    var skn = dir.GetFiles("*.skn", SearchOption.AllDirectories).FirstOrDefault();
                    if (skn != null) return skn.FullName;
                    var scb = dir.GetFiles("*.scb", SearchOption.AllDirectories).FirstOrDefault();
                    if (scb != null) return scb.FullName;
                    dir = dir.Parent;
                }
            }
            catch { }
            return null;
        }

        private static (float[] Positions, float[] Uvs, uint[] Indices) GetFallbackQuadMesh()
        {
            float[] pos = { -50f, -50f, 0f, 50f, -50f, 0f, 50f, 50f, 0f, -50f, 50f, 0f };
            float[] uvs = { 0f, 1f, 1f, 1f, 1f, 0f, 0f, 0f };
            uint[] idx = { 0, 1, 2, 0, 2, 3 };
            return (pos, uvs, idx);
        }

        public string ResolveBin(string authoredPath, string searchDirectory)
            => ResolvePath(authoredPath, searchDirectory, BinExtensions);

        public void ClearCaches()
        {
            _indexes.Clear();
            _textures.Clear();
            _missingTextures.Clear();
            _meshes.Clear();
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

        private static (float[] Positions, float[] Uvs, uint[] Indices)? DecodeMesh(string path)
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
            var indices = new uint[vertexCount];
            int positionOffset = 0;
            int uvOffset = 0;

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
                    indices[(positionOffset / 3) - 1] = (uint)((positionOffset / 3) - 1);
                }
            }

            return (positions, uvs, indices);
        }

        private static (float[] Positions, float[] Uvs, uint[] Indices)? DecodeSkinnedMesh(string path)
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
            for (int index = 0; index < sourcePositions.Count; index++)
            {
                var position = sourcePositions[index];
                positions[index * 3] = position.X;
                positions[index * 3 + 1] = position.Y;
                positions[index * 3 + 2] = position.Z;
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
            return indices.Length == 0 ? null : (positions, uvs, indices);
        }

        private static string CreateKey(string path, string directory)
            => path.Replace('\\', '/').ToLowerInvariant() + "|" + Path.GetFullPath(directory);

        public static uint Fnv1a(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            uint hash = 2166136261;
            foreach (char character in text.ToLowerInvariant())
            {
                hash ^= character;
                hash *= 16777619;
            }
            return hash;
        }
    }
}
