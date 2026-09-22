using System;
using System.Collections.Generic;
using System.Linq;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Viewer.Map.Parsing
{
    /// <summary>
    /// Reads the mesh, skeleton, scale, hidden submeshes and animation graph of one
    /// SkinCharacterDataProperties object as LTK Manager 1.20.0 does.
    /// </summary>
    internal sealed class MapCharacterSkinParser
    {
        internal static readonly uint SkinClass = Fnv1a.HashLower("SkinCharacterDataProperties");
        internal static readonly uint MeshPropertiesField = Fnv1a.HashLower("skinMeshProperties");
        internal static readonly uint SimpleSkinField = Fnv1a.HashLower("simpleSkin");
        internal static readonly uint SkeletonField = Fnv1a.HashLower("skeleton");
        internal static readonly uint SkinScaleField = Fnv1a.HashLower("skinScale");
        internal static readonly uint HiddenSubmeshesField = Fnv1a.HashLower("initialSubmeshToHide");
        internal static readonly uint AnimationPropertiesField = Fnv1a.HashLower("skinAnimationProperties");
        internal static readonly uint AnimationGraphField = Fnv1a.HashLower("animationGraphData");

        public MapCharacterSkinData Parse(BinTree document, string skinPath)
        {
            if (document?.Objects == null || string.IsNullOrWhiteSpace(skinPath))
                return null;

            uint entryHash = Fnv1a.HashLower(skinPath);
            if (!document.Objects.TryGetValue(entryHash, out BinTreeObject skin) || skin.ClassHash != SkinClass)
                return null;

            BinTreeStruct mesh = skin.Properties.TryGetValue(MeshPropertiesField, out BinTreeProperty meshProperty)
                ? meshProperty as BinTreeStruct
                : null;
            MapAssetReference skn = ReadAsset(mesh?.Properties, SimpleSkinField);
            MapAssetReference skl = ReadAsset(mesh?.Properties, SkeletonField);
            float scale = mesh?.Properties.TryGetValue(SkinScaleField, out BinTreeProperty scaleProperty) == true &&
                          scaleProperty is BinTreeF32 authoredScale
                ? authoredScale.Value
                : 1f;
            IReadOnlyList<string> hidden = mesh?.Properties.TryGetValue(HiddenSubmeshesField, out BinTreeProperty hiddenProperty) == true &&
                                           hiddenProperty is BinTreeString hiddenText
                ? SplitSubmeshNames(hiddenText.Value)
                : MapCharacterSkinData.NoHiddenSubmeshes;

            uint graphHash = 0;
            if (skin.Properties.TryGetValue(AnimationPropertiesField, out BinTreeProperty animationProperty) &&
                animationProperty is BinTreeStruct animation &&
                animation.Properties.TryGetValue(AnimationGraphField, out BinTreeProperty graphProperty) &&
                graphProperty is BinTreeObjectLink graph)
            {
                graphHash = graph.Value;
            }

            return new MapCharacterSkinData(skinPath, skn, skl, scale, hidden, graphHash);
        }

        internal static MapAssetReference ReadAsset(
            IReadOnlyDictionary<uint, BinTreeProperty> properties,
            uint field)
        {
            if (properties == null || !properties.TryGetValue(field, out BinTreeProperty property))
                return null;

            if (property is BinTreeOptional optional)
                property = optional.Value;

            return property switch
            {
                BinTreeString text when !string.IsNullOrEmpty(text.Value) =>
                    new MapAssetReference(text.Value, 0),
                BinTreeWadChunkLink link when link.Value != 0 =>
                    new MapAssetReference(null, link.Value),
                _ => null
            };
        }

        internal static IReadOnlyList<string> SplitSubmeshNames(string text) =>
            string.IsNullOrWhiteSpace(text)
                ? MapCharacterSkinData.NoHiddenSubmeshes
                : text.Split(
                        new[] { ' ', '\t', '\r', '\n', ',' },
                        StringSplitOptions.RemoveEmptyEntries)
                    .ToArray();
    }
}
