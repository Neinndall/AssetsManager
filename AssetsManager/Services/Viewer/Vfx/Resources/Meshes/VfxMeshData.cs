namespace AssetsManager.Services.Viewer.Vfx.Resources
{
    /// <summary>One authored SKN submesh range inside the flattened VFX index buffer.</summary>
    public readonly record struct VfxMeshRangeData(uint Hash, int StartIndex, int IndexCount);

    /// <summary>
    /// Decoded geometry for a VFX mesh. Normals are retained because League's mesh particle
    /// shader uses the real surface normal for Fresnel/reflection rather than a UV proxy.
    /// </summary>
    public readonly record struct VfxMeshData(
        float[] Positions,
        float[] Normals,
        float[] Uvs,
        float[] Colors,
        uint[] Indices,
        float[] BoneIndices = null,
        float[] BoneWeights = null,
        float OwnerScale = 1f,
        VfxMeshRangeData[] Ranges = null)
    {
        /// <summary>
        /// Four skin indices/weights per vertex. AttachedMesh uses direct owner-joint indices;
        /// an animated VFX mesh uses its SKN influence-table indices and uploads the matching
        /// influence palette per particle.
        /// </summary>
        public bool HasSkinning =>
            BoneIndices is { Length: > 0 } &&
            BoneWeights is { Length: > 0 } &&
            BoneIndices.Length == BoneWeights.Length &&
            BoneIndices.Length == (Positions?.Length ?? 0) / 3 * 4;
    }
}
