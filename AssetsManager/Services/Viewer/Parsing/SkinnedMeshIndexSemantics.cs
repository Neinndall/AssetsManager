using System;
using System.IO;
using LeagueToolkit.Core.Mesh;

namespace AssetsManager.Services.Viewer.Parsing
{
    /// <summary>
    /// Converts the raw index encoding exposed by LeagueToolkit C# into the absolute shared-vertex
    /// indices consumed by the viewer. Classic SKN files store absolute indices; v4 files carrying
    /// NORMALIZED_INDICES store each range relative to its StartVertex.
    /// </summary>
    internal static class SkinnedMeshIndexSemantics
    {
        // ltk_mesh::SkinnedMeshFlags values used by the current MAIN format.
        internal const uint DirectBlendIndicesFlag = 0x1u;
        internal const uint NormalizedIndicesFlag = 0x2u;

        internal static uint[] AbsoluteIndices(SkinnedMesh mesh, SkinnedMeshRange range)
        {
            ArgumentNullException.ThrowIfNull(mesh);
            ValidateRangeBounds(mesh, range);

            var source = mesh.Indices.Slice(range.StartIndex, range.IndexCount);
            var result = new uint[range.IndexCount];
            bool normalized = (mesh.Flags & NormalizedIndicesFlag) != 0;
            int vertexCount = mesh.VerticesView.VertexCount;

            for (int index = 0; index < range.IndexCount; index++)
            {
                uint stored = source[index];
                long absolute = normalized
                    ? (long)range.StartVertex + stored
                    : stored;

                // LTK MAIN validates the resolved index against the shared vertex buffer,
                // not against the range's advisory StartVertex/VertexCount span.
                if (absolute < 0 || absolute >= vertexCount)
                    throw new InvalidDataException("SKN range index extends beyond the mesh vertices.");

                result[index] = checked((uint)absolute);
            }

            return result;
        }

        internal static void ValidateRangeBounds(SkinnedMesh mesh, SkinnedMeshRange range)
        {
            ArgumentNullException.ThrowIfNull(mesh);
            int vertexCount = mesh.VerticesView.VertexCount;
            if (range.StartVertex < 0 || range.VertexCount < 0 ||
                (long)range.StartVertex + range.VertexCount > vertexCount ||
                range.StartIndex < 0 || range.IndexCount < 0 ||
                (long)range.StartIndex + range.IndexCount > mesh.Indices.Count)
            {
                throw new InvalidDataException("SKN range extends beyond the mesh buffers.");
            }
        }
    }
}
