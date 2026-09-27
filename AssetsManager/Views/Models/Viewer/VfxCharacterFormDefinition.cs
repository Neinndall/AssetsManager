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
        bool HasMaterialOverrides = false,
        bool ReloadsModel = false,
        IReadOnlyList<uint> InitialHiddenSubmeshHashes = null)
    {
        public bool IsBase => GearIndex < 0;

        public static VfxCharacterFormDefinition CreateBase(string name = "Base") =>
            new(
                PathHash: 0,
                GearIndex: -1,
                Name: name,
                ShowSubmeshHashes: System.Array.Empty<uint>(),
                HideSubmeshHashes: System.Array.Empty<uint>());
    }
}