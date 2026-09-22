using System.Collections.Generic;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Viewer.Resolvers;
using LeagueToolkit.Core.Animation;
using LeagueToolkit.Core.Meta;

namespace AssetsManager.Views.Models.Viewer
{
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
        AnimationGraphDefinition AnimationGraph);
}
