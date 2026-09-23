using System;
using System.Collections.Generic;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;

namespace AssetsManager.Services.Viewer.Parsing
{
    /// <summary>
    /// Resolves the map variants declared by Map, MapSkin, and MapContainer objects like current LTK Manager MAIN.
    /// </summary>
    internal sealed class MapVariantParser
    {
        internal const uint MapClass = 0xdfa2efb1;
        internal const uint MapSkinsField = 0x2ed3b95d;
        internal const uint MapSkinClass = 0xcd19ef3c;
        internal const uint SkinNameField = 0x8d39bde6;
        internal const uint ContainerLinkField = 0x960efd81;
        internal const uint MapContainerClass = 0xdde8c114;
        internal const uint MapPathField = 0xcc5e808a;

        public IReadOnlyList<MapVariantData> Parse(BinTree document, uint entryHash)
        {
            if (document?.Objects == null ||
                !document.Objects.TryGetValue(entryHash, out BinTreeObject entry))
            {
                return Array.Empty<MapVariantData>();
            }

            return entry.ClassHash switch
            {
                MapContainerClass => Single(ReadStated(entry.Properties, MapPathField, skin: null)),
                MapSkinClass => Single(ReadSkinVariant(entry.Properties)),
                MapClass => ReadMapVariants(document, entry),
                _ => Array.Empty<MapVariantData>()
            };
        }

        private static IReadOnlyList<MapVariantData> ReadMapVariants(BinTree document, BinTreeObject map)
        {
            if (!map.Properties.TryGetValue(MapSkinsField, out BinTreeProperty property) ||
                property is not BinTreeContainer skins)
            {
                return Array.Empty<MapVariantData>();
            }

            var result = new List<MapVariantData>(skins.Elements.Count);
            foreach (BinTreeProperty item in skins.Elements)
            {
                if (item is not BinTreeObjectLink link ||
                    !document.Objects.TryGetValue(link.Value, out BinTreeObject skin) ||
                    skin.ClassHash != MapSkinClass)
                {
                    continue;
                }

                MapVariantData variant = ReadSkinVariant(skin.Properties);
                if (variant != null)
                    result.Add(variant);
            }

            return result;
        }

        private static MapVariantData ReadSkinVariant(IReadOnlyDictionary<uint, BinTreeProperty> properties)
        {
            string skin = properties != null &&
                          properties.TryGetValue(SkinNameField, out BinTreeProperty nameProperty) &&
                          nameProperty is BinTreeString name
                ? name.Value
                : null;
            return ReadStated(properties, ContainerLinkField, skin);
        }

        private static MapVariantData ReadStated(
            IReadOnlyDictionary<uint, BinTreeProperty> properties,
            uint field,
            string skin)
        {
            if (properties == null ||
                !properties.TryGetValue(field, out BinTreeProperty property) ||
                property is not BinTreeString text ||
                string.IsNullOrEmpty(text.Value) ||
                !MapPath.TryFromEntryPath(text.Value, out MapPath map))
            {
                return null;
            }

            return new MapVariantData(skin, map);
        }

        private static IReadOnlyList<MapVariantData> Single(MapVariantData variant) =>
            variant == null ? Array.Empty<MapVariantData>() : new[] { variant };
    }
}
