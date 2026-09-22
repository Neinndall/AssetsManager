using System.Collections.Generic;

namespace AssetsManager.Services.Viewer.Map.Semantics
{
    /// <summary>
    /// Stable placeable identities shared by scene visibility and the MAP outliner.
    /// A whole chunk or one chunk/key pair can be hidden with the same identifiers as LTK 1.20.0.
    /// </summary>
    internal static class MapOutlineSemantics
    {
        internal static string ChunkId(uint chunkHash) => $"0x{chunkHash:x8}";

        internal static string ItemId(uint chunkHash, uint keyHash) =>
            $"{ChunkId(chunkHash)}/0x{keyHash:x8}";

        internal static bool IsHidden(
            IReadOnlySet<string> hidden,
            uint chunkHash,
            uint keyHash)
        {
            if (hidden == null || hidden.Count == 0)
                return false;

            return hidden.Contains(ChunkId(chunkHash)) ||
                   hidden.Contains(ItemId(chunkHash, keyHash));
        }
    }
}
