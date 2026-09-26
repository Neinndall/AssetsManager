using System.Collections.Generic;

namespace AssetsManager.Views.Models.Viewer
{
    /// <summary>Authored gear form data projected from SkinUpgradeData and GearData BIN objects.</summary>
    public sealed record VfxCharacterFormDefinition(
        uint PathHash,
        int GearIndex,
        string Name,
        IReadOnlyList<uint> ShowSubmeshHashes,
        IReadOnlyList<uint> HideSubmeshHashes,
        string MeshPath = null,
        string SkeletonPath = null,
        string EquipAnimation = null,
        IReadOnlyDictionary<uint, uint> ResourceMap = null,
        IReadOnlyList<VfxIdleEffectDefinition> OverrideIdleEffects = null,
        bool EnableOverrideIdleEffects = false,
        bool HasMaterialOverrides = false);
}