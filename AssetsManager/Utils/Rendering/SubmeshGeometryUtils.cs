using System;
using System.Collections.Generic;
using System.IO;

namespace AssetsManager.Utils.Rendering
{
    /// <summary>
    /// Utility methods for validating, slicing, and correcting mesh range indices across Riot simple skins (.skn).
    /// </summary>
    public static class SubmeshGeometryUtils
    {
        /// <summary>
        /// Determines whether a submesh range uses global vertex indices or local relative indices,
        /// and computes the vertex index offset required to address the shared vertex buffer.
        /// </summary>
        public static int ResolveVertexOffset(
            int startVertex,
            int vertexCount,
            IReadOnlyList<uint> submeshIndices,
            string submeshName)
        {
            if (submeshIndices == null || submeshIndices.Count == 0)
                return 0;

            bool usesGlobalIndices = true;
            bool usesLocalIndices = startVertex > 0;

            for (int i = 0; i < submeshIndices.Count; i++)
            {
                int index = (int)submeshIndices[i];
                usesGlobalIndices &= index >= startVertex && index < startVertex + vertexCount;
                usesLocalIndices &= index >= 0 && index < vertexCount;
            }

            if (!usesGlobalIndices && !usesLocalIndices)
            {
                throw new InvalidDataException(
                    $"Submesh '{submeshName}' contains indices outside its declared vertex range (start={startVertex}, count={vertexCount}).");
            }

            return usesLocalIndices && !usesGlobalIndices ? startVertex : 0;
        }

        /// <summary>
        /// Overload accepting a list or array of signed 32-bit integer indices.
        /// </summary>
        public static int ResolveVertexOffset(
            int startVertex,
            int vertexCount,
            IReadOnlyList<int> submeshIndices,
            string submeshName)
        {
            if (submeshIndices == null || submeshIndices.Count == 0)
                return 0;

            bool usesGlobalIndices = true;
            bool usesLocalIndices = startVertex > 0;

            for (int i = 0; i < submeshIndices.Count; i++)
            {
                int index = submeshIndices[i];
                usesGlobalIndices &= index >= startVertex && index < startVertex + vertexCount;
                usesLocalIndices &= index >= 0 && index < vertexCount;
            }

            if (!usesGlobalIndices && !usesLocalIndices)
            {
                throw new InvalidDataException(
                    $"Submesh '{submeshName}' contains indices outside its declared vertex range (start={startVertex}, count={vertexCount}).");
            }

            return usesLocalIndices && !usesGlobalIndices ? startVertex : 0;
        }

        /// <summary>
        /// Evaluates whether a submesh is visible given a set of hidden submesh names (case-insensitive).
        /// </summary>
        public static bool IsSubmeshVisible(string submeshName, IReadOnlySet<string> hiddenSubmeshes)
        {
            if (string.IsNullOrWhiteSpace(submeshName) || hiddenSubmeshes == null || hiddenSubmeshes.Count == 0)
                return true;

            return !hiddenSubmeshes.Contains(submeshName);
        }
    }
}
