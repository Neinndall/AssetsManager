using System;
using System.Collections.Generic;

namespace AssetsManager.Views.Models.Viewer
{
    /// <summary>
    /// Authored skin data required to stand one map character without creating a SceneModel.
    /// Assets keep both their authored path and WAD hash so project overrides and unnamed chunks work.
    /// </summary>
    internal sealed record MapCharacterSkinData(
        string Skin,
        MapAssetReference Mesh,
        MapAssetReference Skeleton,
        float Scale,
        IReadOnlyList<string> HiddenSubmeshes,
        uint AnimationGraphHash)
    {
        public static IReadOnlyList<string> NoHiddenSubmeshes { get; } = Array.Empty<string>();
    }
}
