using System.Collections.Generic;
using System.Numerics;

namespace AssetsManager.Views.Models.Viewer
{
    /// <summary>
    /// One authored SKN submesh as a contiguous run of the flattened absolute index buffer.
    /// </summary>
    internal sealed record MapCharacterMeshRange(
        string Name,
        int StartIndex,
        int IndexCount);

    /// <summary>
    /// Shared SKN geometry in the same contract LTK Manager exposes to its viewport.
    /// Tangents are authored when present and baked in-memory from position/normal/UV otherwise;
    /// W stores tangent-space handedness so a bitangent can be reconstructed as cross(N, T) * W.
    /// Skin indices remain authored shader-joint slots; the skeleton influence table maps them
    /// when the renderer builds its bone palette.
    /// </summary>
    internal sealed record MapCharacterMeshData(
        Vector3[] Positions,
        Vector3[] Normals,
        Vector2[] Uv,
        Vector4[] Tangents,
        byte[] SkinIndices,
        float[] SkinWeights,
        uint[] Indices,
        IReadOnlyList<MapCharacterMeshRange> Ranges)
    {
        public int VertexCount => Positions?.Length ?? 0;
        public bool HasTangents => Tangents != null && Tangents.Length == VertexCount;
        public bool HasSkin =>
            SkinIndices != null &&
            SkinWeights != null &&
            SkinIndices.Length == VertexCount * 4 &&
            SkinWeights.Length == VertexCount * 4;
    }
}
