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
    /// Reads the terrain inputs of the map shaders: the MapContainer bounds, the environment assets of
    /// every MapSkin that draws the container (grass tints, per-state <c>mAlternateAssets</c> tints and
    /// environment cube) and the transition time of each primary visibility flag.
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
        private static readonly uint TransitionTimeField = Fnv1a.HashLower("TransitionTime");
        private static readonly uint TerrainPaintClass = Fnv1a.HashLower("MapTerrainPaint");
        private static readonly uint TerrainPaintTextureField = Fnv1a.HashLower("TerrainPaintTexturePath");

        // Unnamed MapSkin field holding the environment cube (assets/maps/deprecated/<map>/env_cubemap.dds).
        internal const uint EnvironmentCubeField = 0xd0a4e40b;

        internal static MapTerrainData Parse(
            BinTree materials,
            BinTree mapDocument,
            MapPath map,
            Func<ulong, string> resolveWadPath = null)
        {
            if (map == null)
                return null;

            IReadOnlyList<FlagDefinition> flags = ReadPrimaryFlags(mapDocument);
            var flagsByName = new Dictionary<uint, int>();
            foreach (FlagDefinition flag in flags)
                flagsByName.TryAdd(flag.NameHash, flag.Flag);

            MapSkinEnvironmentData[] skins = FindSkins(mapDocument, map)
                .Select(skin => ReadSkin(skin, flagsByName, resolveWadPath))
                .Where(skin => skin.GrassTint != null || skin.GrassTintAlternates.Count > 0 || skin.EnvironmentCube != null)
                .ToArray();
            BinTreeObject container = FindContainer(materials, map);
            MapTextureReference terrainPaint = ReadTerrainPaint(container, resolveWadPath);
            if (skins.Length == 0 && terrainPaint == null)
                return null;

            Vector2 min = ReadVector2(container?.Properties, BoundsMinField) ?? Vector2.Zero;
            Vector2 max = ReadVector2(container?.Properties, BoundsMaxField) ?? Vector2.Zero;
            var transitions = flags
                .Where(flag => flag.TransitionSeconds > 0f)
                .GroupBy(flag => flag.Flag)
                .ToDictionary(group => group.Key, group => group.First().TransitionSeconds);
            return new MapTerrainData(min, max, skins, transitions, terrainPaint);
        }

        /// <summary>The MapTerrainPaint component of the container: the texture TERRAIN_BLEND samples.</summary>
        private static MapTextureReference ReadTerrainPaint(BinTreeObject container, Func<ulong, string> resolveWadPath)
        {
            if (container == null ||
                !container.Properties.TryGetValue(MapSunParser.ComponentsField, out BinTreeProperty property) ||
                property is not BinTreeContainer components)
            {
                return null;
            }

            return components.Elements
                .OfType<BinTreeStruct>()
                .Where(component => component.ClassHash == TerrainPaintClass)
                .Select(component => ReadTexture(component.Properties, TerrainPaintTextureField, resolveWadPath))
                .FirstOrDefault(texture => texture != null);
        }

        private readonly record struct FlagDefinition(uint NameHash, int Flag, float TransitionSeconds);

        /// <summary>MapSkins drawing the container, in the authored order of the Map object's skin list.</summary>
        private static IEnumerable<BinTreeObject> FindSkins(BinTree mapDocument, MapPath map)
        {
            if (mapDocument?.Objects == null)
                return Array.Empty<BinTreeObject>();

            BinTreeObject owner = mapDocument.Objects.Values.FirstOrDefault(entry => entry.ClassHash == MapVariantParser.MapClass);
            IEnumerable<BinTreeObject> authored = owner != null &&
                                                  owner.Properties.TryGetValue(MapVariantParser.MapSkinsField, out BinTreeProperty property) &&
                                                  property is BinTreeContainer list
                ? list.Elements
                    .OfType<BinTreeObjectLink>()
                    .Select(link => mapDocument.Objects.GetValueOrDefault(link.Value))
                : mapDocument.Objects.Values;
            return authored
                .Where(entry => entry?.ClassHash == MapVariantParser.MapSkinClass &&
                                string.Equals(
                                    ReadString(entry.Properties, MapVariantParser.ContainerLinkField),
                                    map.Value,
                                    StringComparison.OrdinalIgnoreCase))
                .Distinct()
                .ToArray();
        }

        private static MapSkinEnvironmentData ReadSkin(
            BinTreeObject skin,
            IReadOnlyDictionary<uint, int> flagsByName,
            Func<ulong, string> resolveWadPath) =>
            new(
                ReadString(skin.Properties, MapVariantParser.SkinNameField),
                ReadTexture(skin.Properties, GrassTintField, resolveWadPath),
                ReadAlternates(skin.Properties, flagsByName, resolveWadPath),
                ReadTexture(skin.Properties, EnvironmentCubeField, resolveWadPath));

        private static BinTreeObject FindContainer(BinTree materials, MapPath map)
        {
            if (materials?.Objects == null)
                return null;
            return materials.Objects.TryGetValue(Fnv1a.HashLower(map.Value), out BinTreeObject exact) &&
                   exact.ClassHash == MapVariantParser.MapContainerClass
                ? exact
                : materials.Objects.Values.FirstOrDefault(entry => entry.ClassHash == MapVariantParser.MapContainerClass);
        }

        /// <summary>Primary visibility flags of the Map object: hashed name, bit and transition time.</summary>
        private static IReadOnlyList<FlagDefinition> ReadPrimaryFlags(BinTree mapDocument)
        {
            BinTreeObject owner = mapDocument?.Objects?.Values.FirstOrDefault(entry => entry.ClassHash == MapVariantParser.MapClass);
            if (owner == null ||
                !owner.Properties.TryGetValue(VisibilityFlagDefinesField, out BinTreeProperty definesProperty) ||
                definesProperty is not BinTreeStruct defines ||
                !defines.Properties.TryGetValue(FlagDefinitionsField, out BinTreeProperty listProperty) ||
                listProperty is not BinTreeContainer list)
            {
                return Array.Empty<FlagDefinition>();
            }

            var flags = new List<FlagDefinition>();
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
                float seconds = definition.Properties.TryGetValue(TransitionTimeField, out BinTreeProperty timeProperty) &&
                                timeProperty is BinTreeF32 time
                    ? time.Value
                    : 0f;
                if (bit is >= 0 and <= 7)
                    flags.Add(new FlagDefinition(name.Value, 1 << bit, seconds));
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
