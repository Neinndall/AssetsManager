using System.Collections.Generic;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Viewer.Resolvers;
using LeagueToolkit.Core.Animation;
using LeagueToolkit.Core.Meta;

namespace AssetsManager.Views.Models.Viewer
{
    /// <summary>
    /// VFX catalog authored by one MAP character skin and its linked BIN closure. The root resolver
    /// remains the skin's own resource map while each linked system keeps its document-local map.
    /// </summary>
    internal sealed record MapCharacterVfxCatalog(
        IReadOnlyDictionary<uint, VfxSystemDefinition> Systems,
        IReadOnlyDictionary<uint, uint> ResourceMap,
        IReadOnlyList<VfxIdleEffectDefinition> IdleEffects,
        VfxOwnerSceneContext OwnerSceneContext)
    {
        internal static MapCharacterVfxCatalog Empty { get; } = new(
            new Dictionary<uint, VfxSystemDefinition>(),
            new Dictionary<uint, uint>(),
            System.Array.Empty<VfxIdleEffectDefinition>(),
            null);
    }

    /// <summary>
    /// One fully resolved character skin shared by every placement that wears it in a map.
    /// Geometry, rig, material state and decoded textures exist once per skin rather than once per placeable.
    /// </summary>
    internal sealed record MapCharacterAssetData(
        MapCharacterSkinData Skin,
        MapCharacterMeshData Mesh,
        RigResource Skeleton,
        SknMaterialTextureResolution Materials,
        IReadOnlyDictionary<string, BitmapSource> Textures,
        IReadOnlyList<BinTree> Documents,
        AnimationGraphDefinition AnimationGraph,
        MapCharacterVfxCatalog Vfx = null);
}
