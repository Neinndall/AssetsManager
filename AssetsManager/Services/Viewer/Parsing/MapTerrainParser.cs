using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Viewer.Parsing
{
    /// <summary>
    /// Reads the terrain inputs of the map shaders: the MapContainer bounds and the grass tint maps of
    /// the MapSkin that draws the container, including its per-state <c>mAlternateAssets</c> tints.
    /// </summary>
    internal static class MapTerrainParser
    {
        private static readonly uint BoundsMinField = Fnv1a.HashLower("boundsMin");
        private static readonly uint BoundsMaxField = Fnv1a.HashLower("boundsMax");
        private static readonly uint GrassTintField = Fnv1a.HashLower("mGrassTintTexture");
        private static readonly uint AlternateAssetsField = Fnv1a.HashLower("mAlternateAssets");
        private static readonly uint AlternateGrassTintField = Fnv1a.HashLower("mGrassTintTextureName");
        private static readonly uint AlternateFlagNameField = Fnv1a.HashLower("mVisibilityFlagName");
        private static readonly uint VisibilityFlagDefinesField = Fnv1a.HashLower("VisibilityFlagDefines");
        private static readonly uint FlagDefinitionsField = Fnv1a.HashLower("FlagDefinitions");
        private static readonly uint FlagNameField = Fnv1a.HashLower("name");
        private static readonly uint BitIndexField = Fnv1a.HashLower("BitIndex");

        internal static MapTerrainData Parse(
            BinTree materials,
            BinTree mapDocument,
            MapPath map,
            Func<ulong, string> resolveWadPath = null)
        {
            if (map == null)
                return null;

            BinTreeObject skin = FindSkin(mapDocument, map);
            MapTextureReference grassTint = skin == null ? null : ReadTexture(skin.Properties, GrassTintField, resolveWadPath);
            IReadOnlyList<MapGrassTintAlternate> alternates = skin == null
                ? Array.Empty<MapGrassTintAlternate>()
                : ReadAlternates(skin.Properties, PrimaryFlagsByName(mapDocument), resolveWadPath);
            if (grassTint == null && alternates.Count == 0)
                return null;

            BinTreeObject container = FindContainer(materials, map);
            Vector2 min = ReadVector2(container?.Properties, BoundsMinField) ?? Vector2.Zero;
            Vector2 max = ReadVector2(container?.Properties, BoundsMaxField) ?? Vector2.Zero;
            return new MapTerrainData(min, max, grassTint, alternates);
        }

        /// <summary>
        /// The MapSkin drawing the container, chosen with the variant opening rule (Default first,
        /// then authored order of the Map object's skin list).
        /// </summary>
        private static BinTreeObject FindSkin(BinTree mapDocument, MapPath map)
        {
            if (mapDocument?.Objects == null)
                return null;

            BinTreeObject owner = mapDocument.Objects.Values.FirstOrDefault(entry => entry.ClassHash == MapVariantParser.MapClass);
            IEnumerable<BinTreeObject> authored = owner != null &&
                                                  owner.Properties.TryGetValue(MapVariantParser.MapSkinsField, out BinTreeProperty property) &&
                                                  property is BinTreeContainer list
                ? list.Elements
                    .OfType<BinTreeObjectLink>()
                    .Select(link => mapDocument.Objects.GetValueOrDefault(link.Value))
                : mapDocument.Objects.Values;
            BinTreeObject[] drawing = authored
                .Where(entry => entry?.ClassHash == MapVariantParser.MapSkinClass && Draws(entry, map))
                .ToArray();
            return drawing.FirstOrDefault(entry =>
                       string.Equals(ReadString(entry.Properties, MapVariantParser.SkinNameField), "default", StringComparison.OrdinalIgnoreCase)) ??
                   drawing.FirstOrDefault();
        }

        private static bool Draws(BinTreeObject skin, MapPath map) =>
            string.Equals(
                ReadString(skin.Properties, MapVariantParser.ContainerLinkField),
                map.Value,
                StringComparison.OrdinalIgnoreCase);

        private static BinTreeObject FindContainer(BinTree materials, MapPath map)
        {
            if (materials?.Objects == null)
                return null;
            return materials.Objects.TryGetValue(Fnv1a.HashLower(map.Value), out BinTreeObject exact) &&
                   exact.ClassHash == MapVariantParser.MapContainerClass
                ? exact
                : materials.Objects.Values.FirstOrDefault(entry => entry.ClassHash == MapVariantParser.MapContainerClass);
        }

        /// <summary>Primary visibility flag bits of the Map object, keyed by their hashed name.</summary>
        private static IReadOnlyDictionary<uint, int> PrimaryFlagsByName(BinTree mapDocument)
        {
            var flags = new Dictionary<uint, int>();
            BinTreeObject owner = mapDocument?.Objects?.Values.FirstOrDefault(entry => entry.ClassHash == MapVariantParser.MapClass);
            if (owner == null ||
                !owner.Properties.TryGetValue(VisibilityFlagDefinesField, out BinTreeProperty definesProperty) ||
                definesProperty is not BinTreeStruct defines ||
                !defines.Properties.TryGetValue(FlagDefinitionsField, out BinTreeProperty listProperty) ||
                listProperty is not BinTreeContainer list)
            {
                return flags;
            }

            foreach (BinTreeStruct definition in list.Elements.OfType<BinTreeStruct>())
            {
                if (!definition.Properties.TryGetValue(FlagNameField, out BinTreeProperty nameProperty) ||
                    nameProperty is not BinTreeHash name)
                {
                    continue;
                }
                int bit = definition.Properties.TryGetValue(BitIndexField, out BinTreeProperty bitProperty) &&
                          bitProperty is BinTreeU8 value
                    ? value.Value
                    : 0;
                if (bit is >= 0 and <= 7)
                    flags.TryAdd(name.Value, 1 << bit);
            }
            return flags;
        }

        private static IReadOnlyList<MapGrassTintAlternate> ReadAlternates(
            IReadOnlyDictionary<uint, BinTreeProperty> skin,
            IReadOnlyDictionary<uint, int> flagsByName,
            Func<ulong, string> resolveWadPath)
        {
            // mAlternateAssets is an embedded MapAlternateAssets whose own mAlternateAssets holds the list.
            if (!skin.TryGetValue(AlternateAssetsField, out BinTreeProperty outer) ||
                outer is not BinTreeStruct wrapper ||
                !wrapper.Properties.TryGetValue(AlternateAssetsField, out BinTreeProperty inner) ||
                inner is not BinTreeContainer list)
            {
                return Array.Empty<MapGrassTintAlternate>();
            }

            var alternates = new List<MapGrassTintAlternate>();
            foreach (BinTreeStruct asset in list.Elements.OfType<BinTreeStruct>())
            {
                MapTextureReference texture = ReadTexture(asset.Properties, AlternateGrassTintField, resolveWadPath);
                if (texture == null ||
                    !asset.Properties.TryGetValue(AlternateFlagNameField, out BinTreeProperty nameProperty) ||
                    nameProperty is not BinTreeHash name ||
                    !flagsByName.TryGetValue(name.Value, out int flag) ||
                    alternates.Any(existing => existing.Flag == flag))
                {
                    continue;
                }
                alternates.Add(new MapGrassTintAlternate(flag, texture));
            }
            return alternates;
        }

        private static MapTextureReference ReadTexture(
            IReadOnlyDictionary<uint, BinTreeProperty> properties,
            uint field,
            Func<ulong, string> resolveWadPath)
        {
            if (properties == null || !properties.TryGetValue(field, out BinTreeProperty property))
                return null;
            return property switch
            {
                BinTreeString text when !string.IsNullOrWhiteSpace(text.Value) =>
                    new MapTextureReference(PathUtils.ToVirtualPath(text.Value), 0),
                BinTreeWadChunkLink link when link.Value != 0 =>
                    new MapTextureReference(ResolvePath(resolveWadPath, link.Value), link.Value),
                _ => null
            };
        }

        private static string ResolvePath(Func<ulong, string> resolveWadPath, ulong hash)
        {
            string resolved = resolveWadPath?.Invoke(hash);
            return string.IsNullOrWhiteSpace(resolved) ||
                   resolved.Equals(hash.ToString("x16"), StringComparison.OrdinalIgnoreCase)
                ? null
                : PathUtils.ToVirtualPath(resolved);
        }

        private static Vector2? ReadVector2(IReadOnlyDictionary<uint, BinTreeProperty> properties, uint field) =>
            properties != null && properties.TryGetValue(field, out BinTreeProperty property) && property is BinTreeVector2 value
                ? value.Value
                : null;

        private static string ReadString(IReadOnlyDictionary<uint, BinTreeProperty> properties, uint field) =>
            properties != null && properties.TryGetValue(field, out BinTreeProperty property) && property is BinTreeString text
                ? text.Value
                : null;
    }
}
