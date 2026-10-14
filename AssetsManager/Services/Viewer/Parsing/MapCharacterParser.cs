using System;
using System.Collections.Generic;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;

namespace AssetsManager.Services.Viewer.Parsing
{
    /// <summary>
    /// Resolves the structures and level props that map placeables draw as character skins,
    /// matching current LTK Manager MAIN's map/characters contract.
    /// </summary>
    internal sealed class MapCharacterParser
    {
        internal const uint CharacterField = 0x8b3aa710;
        internal const uint SkinField = 0x336b65b8;
        internal const uint TeamField = 0xa2fd7d0c;
        internal const uint GdsMapObjectClass = 0xda9e5c0c;
        internal const uint ObjectTypeField = 0x5127f14d;
        internal const uint ObjectSkinIdField = 0xd65a78b6;
        internal const uint ExtraInfoField = 0xf549ff11;
        internal const uint AnimationInfoClass = 0x892e1ff2;
        internal const uint DefaultAnimationField = 0xedf1840c;

        private const byte LevelPropType = 10;
        private const string LevelPropPrefix = "LevelProp_";

        public IReadOnlyList<MapCharacterData> Parse(IReadOnlyList<MapPlaceableChunkData> chunks)
        {
            if (chunks == null || chunks.Count == 0)
                return Array.Empty<MapCharacterData>();

            var result = new List<MapCharacterData>();
            foreach (MapPlaceableChunkData chunk in chunks)
            {
                if (chunk?.Items == null)
                    continue;

                foreach (MapPlaceableData placed in chunk.Items)
                {
                    string skin = ReadSkin(placed);
                    if (string.IsNullOrEmpty(skin))
                        continue;

                    result.Add(new MapCharacterData(
                        placed,
                        skin,
                        ReadTeam(placed.Properties),
                        ReadDefaultAnimation(placed.Properties)));
                }
            }

            return result;
        }

        internal static string ReadSkin(MapPlaceableData placed)
        {
            if (placed == null)
                return null;

            return placed.ClassHash == GdsMapObjectClass
                ? ReadLevelPropSkin(placed.Properties)
                : ReadGameplaySkin(placed.Properties);
        }

        private static string ReadGameplaySkin(IReadOnlyDictionary<uint, BinTreeProperty> properties)
        {
            if (properties == null ||
                !properties.TryGetValue(CharacterField, out BinTreeProperty componentProperty) ||
                componentProperty is not BinTreeStruct component ||
                !component.Properties.TryGetValue(SkinField, out BinTreeProperty skinProperty) ||
                skinProperty is not BinTreeString skin)
            {
                return null;
            }

            return skin.Value;
        }

        private static string ReadLevelPropSkin(IReadOnlyDictionary<uint, BinTreeProperty> properties)
        {
            if (properties == null ||
                !properties.TryGetValue(ObjectTypeField, out BinTreeProperty typeProperty) ||
                typeProperty is not BinTreeU8 type ||
                type.Value != LevelPropType ||
                !properties.TryGetValue(MapPlaceableParser.NameField, out BinTreeProperty nameProperty) ||
                nameProperty is not BinTreeString name ||
                string.IsNullOrEmpty(name.Value) ||
                !name.Value.StartsWith(LevelPropPrefix, StringComparison.Ordinal))
            {
                return null;
            }

            string character = name.Value[LevelPropPrefix.Length..];
            int end = character.Length;
            while (end > 0 && character[end - 1] is >= '0' and <= '9')
                end--;
            character = character[..end];
            if (character.Length == 0)
                return null;

            uint skinId = properties.TryGetValue(ObjectSkinIdField, out BinTreeProperty skinProperty) &&
                          skinProperty is BinTreeU32 skin
                ? skin.Value
                : 0u;
            return $"Characters/{character}/Skins/Skin{skinId}";
        }

        private static uint? ReadTeam(IReadOnlyDictionary<uint, BinTreeProperty> properties)
        {
            if (properties == null ||
                !properties.TryGetValue(TeamField, out BinTreeProperty teamProperty) ||
                teamProperty is not BinTreeStruct team ||
                !team.Properties.TryGetValue(TeamField, out BinTreeProperty valueProperty) ||
                valueProperty is not BinTreeU32 value)
            {
                return null;
            }

            return value.Value;
        }

        private static string ReadDefaultAnimation(IReadOnlyDictionary<uint, BinTreeProperty> properties)
        {
            if (properties == null ||
                !properties.TryGetValue(ExtraInfoField, out BinTreeProperty extraProperty) ||
                extraProperty is not BinTreeContainer extra)
            {
                return null;
            }

            foreach (BinTreeProperty item in extra.Elements)
            {
                if (item is not BinTreeStruct info ||
                    info.ClassHash != AnimationInfoClass ||
                    !info.Properties.TryGetValue(DefaultAnimationField, out BinTreeProperty animationProperty) ||
                    animationProperty is not BinTreeString animation ||
                    string.IsNullOrEmpty(animation.Value))
                {
                    continue;
                }

                return animation.Value;
            }

            return null;
        }
    }
}
