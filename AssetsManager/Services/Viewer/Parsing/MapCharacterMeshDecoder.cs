using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Memory;
using LeagueToolkit.Core.Mesh;

namespace AssetsManager.Services.Viewer.Parsing
{
    /// <summary>
    /// Decodes one Simple Skin into the shared geometry contract used by map structures.
    /// The decoder keeps authored shader-joint indices and all submesh ranges so visibility
    /// can change later without rebuilding geometry.
    /// </summary>
    internal sealed class MapCharacterMeshDecoder
    {
        private const uint SimpleSkinMagic = 0x00112233;

        public MapCharacterMeshData Decode(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);
            if (!stream.CanSeek)
                throw new InvalidDataException("SKN stream must be seekable.");

            long origin = stream.Position;
            ushort major = ReadMajorVersion(stream);
            stream.Position = origin;

            using SkinnedMesh mesh = SkinnedMesh.ReadFromSimpleSkin(stream, leaveOpen: true);
            return Decode(mesh, major);
        }

        internal static MapCharacterMeshData Decode(SkinnedMesh mesh, ushort majorVersion)
        {
            ArgumentNullException.ThrowIfNull(mesh);

            VertexElementAccessor positionsAccessor = GetRequired(mesh, VertexElement.POSITION.Name);
            int vertexCount = mesh.VerticesView.VertexCount;
            Vector3[] positions = positionsAccessor.AsVector3Array().ToArray();
            if (positions.Length != vertexCount)
                throw new InvalidDataException("SKN position buffer does not match its vertex count.");

            Vector3[] normals = mesh.VerticesView.TryGetAccessor(VertexElement.NORMAL.Name, out VertexElementAccessor normalsAccessor)
                ? normalsAccessor.AsVector3Array().ToArray()
                : null;
            if (normals != null && normals.Length != vertexCount)
                throw new InvalidDataException("SKN normal buffer does not match its vertex count.");

            Vector2[] uv = mesh.VerticesView.TryGetAccessor(VertexElement.TEXCOORD_0.Name, out VertexElementAccessor uvAccessor)
                ? uvAccessor.AsVector2Array().ToArray()
                : null;
            if (uv != null && uv.Length != vertexCount)
                throw new InvalidDataException("SKN UV buffer does not match its vertex count.");

            Vector4[] tangents = mesh.VerticesView.TryGetAccessor(VertexElement.TANGENT.Name, out VertexElementAccessor tangentAccessor)
                ? tangentAccessor.AsVector4Array().ToArray()
                : null;
            if (tangents != null && tangents.Length != vertexCount)
                throw new InvalidDataException("SKN tangent buffer does not match its vertex count.");

            byte[] skinIndices = null;
            float[] skinWeights = null;
            bool hasIndices = mesh.VerticesView.TryGetAccessor(VertexElement.BLEND_INDEX.Name, out VertexElementAccessor blendIndexAccessor);
            bool hasWeights = mesh.VerticesView.TryGetAccessor(VertexElement.BLEND_WEIGHT.Name, out VertexElementAccessor blendWeightAccessor);
            if (hasIndices && hasWeights)
            {
                var indices = blendIndexAccessor.AsXyzwU8Array().ToArray();
                var weights = blendWeightAccessor.AsVector4Array().ToArray();
                if (indices.Length != vertexCount || weights.Length != vertexCount)
                    throw new InvalidDataException("SKN skinning buffers do not match its vertex count.");

                skinIndices = new byte[checked(vertexCount * 4)];
                skinWeights = new float[checked(vertexCount * 4)];
                for (int vertex = 0; vertex < vertexCount; vertex++)
                {
                    int at = vertex * 4;
                    var joints = indices[vertex];
                    Vector4 weight = weights[vertex];
                    skinIndices[at] = joints.x;
                    skinIndices[at + 1] = joints.y;
                    skinIndices[at + 2] = joints.z;
                    skinIndices[at + 3] = joints.w;
                    skinWeights[at] = weight.X;
                    skinWeights[at + 1] = weight.Y;
                    skinWeights[at + 2] = weight.Z;
                    skinWeights[at + 3] = weight.W;
                }
            }

            (uint[] flattened, IReadOnlyList<MapCharacterMeshRange> ranges) =
                majorVersion == 0
                    ? DecodeVersionZeroIndices(mesh)
                    : DecodeRanges(mesh);

            // LTK's Bake Tangents authoring action persists this stream into SKN. The MAP preview
            // must not mutate extracted assets, so missing tangents are baked into the runtime mesh.
            tangents ??= MeshTangentBuilder.Build(positions, normals, uv, flattened);

            return new MapCharacterMeshData(
                positions,
                normals,
                uv,
                tangents,
                skinIndices,
                skinWeights,
                flattened,
                ranges);
        }

        internal static Vector4[] BakeTangents(
            Vector3[] positions,
            Vector3[] normals,
            Vector2[] uv,
            uint[] indices)
            => MeshTangentBuilder.Build(positions, normals, uv, indices);

        private static ushort ReadMajorVersion(Stream stream)
        {
            long origin = stream.Position;
            try
            {
                using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
                uint magic = reader.ReadUInt32();
                if (magic != SimpleSkinMagic)
                    throw new InvalidDataException("Invalid Simple Skin signature.");
                return reader.ReadUInt16();
            }
            finally
            {
                stream.Position = origin;
            }
        }

        private static (uint[] Indices, IReadOnlyList<MapCharacterMeshRange> Ranges) DecodeVersionZeroIndices(
            SkinnedMesh mesh)
        {
            if (mesh.Ranges.Count == 0)
                return (Array.Empty<uint>(), Array.Empty<MapCharacterMeshRange>());

            // LeagueToolkit C# synthesizes one Base range for v0 while LTK's preview contract
            // exposes no ranges and draws the entire index buffer as one implicit run.
            SkinnedMeshRange range = mesh.Ranges[0];
            uint[] indices = SkinnedMeshIndexSemantics.AbsoluteIndices(mesh, range);
            return (indices, Array.Empty<MapCharacterMeshRange>());
        }

        private static (uint[] Indices, IReadOnlyList<MapCharacterMeshRange> Ranges) DecodeRanges(
            SkinnedMesh mesh)
        {
            var indices = new List<uint>(mesh.Indices.Count);
            var ranges = new List<MapCharacterMeshRange>(mesh.Ranges.Count);
            foreach (SkinnedMeshRange range in mesh.Ranges)
            {
                int start = indices.Count;
                indices.AddRange(SkinnedMeshIndexSemantics.AbsoluteIndices(mesh, range));
                ranges.Add(new MapCharacterMeshRange(
                    range.Material?.TrimEnd('\0') ?? string.Empty,
                    start,
                    range.IndexCount));
            }

            return (indices.ToArray(), ranges);
        }

        private static VertexElementAccessor GetRequired(SkinnedMesh mesh, ElementName element)
        {
            if (mesh.VerticesView.TryGetAccessor(element, out VertexElementAccessor accessor))
                return accessor;
            throw new InvalidDataException($"SKN does not contain required vertex element '{element}'.");
        }
    }
}
