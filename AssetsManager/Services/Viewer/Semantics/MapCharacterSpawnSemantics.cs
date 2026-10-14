using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Viewer.Semantics
{
    /// <summary>
    /// Where a map stands a Character. Monsters and structures carry a map entity with their transform
    /// (Summoner's Rift's Baron sits in its pit). Monsters a neutral camp spawns may instead be held off the
    /// playfield: the Rift keeps its seven drakes near Z = -1500 and SR_DragonLevelScript's CreateNeutralCamp
    /// brings the chosen one to the Dragon camp's location, facing its authored direction. The camp names the
    /// monsters it spawns (CampName "Dragon", variants "DragonAir", ...), which matches the Character's name.
    /// </summary>
    internal static class MapCharacterSpawnSemantics
    {
        /// <summary>An entity this far from the camp that spawns it is held elsewhere until spawned.</summary>
        internal const float HeldDistance = 1500f;

        internal static readonly uint NeutralCampField = Fnv1a.HashLower("NeutralCamp");
        internal const uint CampDefinitionField = 0x5a4ef4e7;
        internal static readonly uint CampNameField = Fnv1a.HashLower("CampName");
        internal const uint CampVariantsField = 0x7511a599;
        internal const uint CampVariantNameField = 0x19a9984e;

        internal sealed record Camp(string Name, IReadOnlyList<string> Variants, Matrix4x4 Transform)
        {
            public Vector3 Position => Transform.Translation;
        }

        /// <summary>Authored (engine space) transform the map spawns the Character with, or null.</summary>
        internal static Matrix4x4? SpawnTransform(MapSceneData scene, string character)
        {
            if (scene == null || string.IsNullOrWhiteSpace(character))
                return null;

            IReadOnlyList<Camp> camps = Camps(scene);
            Camp camp = camps.FirstOrDefault(candidate => candidate.Variants.Any(variant => Names(variant, character)))
                        ?? camps.FirstOrDefault(candidate => Names(candidate.Name, character));
            MapCharacterData entity = (scene.Characters ?? Array.Empty<MapCharacterData>())
                .Where(placement => string.Equals(CharacterOfSkin(placement?.Skin), character, StringComparison.OrdinalIgnoreCase))
                .OrderBy(placement => camp == null ? 0f : Vector3.Distance(placement.Placeable.Position, camp.Position))
                .FirstOrDefault();

            // Level props (scenery such as the backdrop drake under the Rift) stand where authored; only
            // gameplay entities are ever held away from the camp that spawns them.
            bool levelProp = entity?.Placeable.ClassHash == Parsing.MapCharacterParser.GdsMapObjectClass;
            if (entity != null && (camp == null || levelProp || Vector3.Distance(entity.Placeable.Position, camp.Position) <= HeldDistance))
                return entity.Transform;
            return camp?.Transform;
        }

        /// <summary>Neutral camps of the scene's placeables, with the names their definition declares.</summary>
        internal static IReadOnlyList<Camp> Camps(MapSceneData scene)
        {
            var camps = new List<Camp>();
            foreach (MapPlaceableData placed in (scene?.Placeables ?? Array.Empty<MapPlaceableChunkData>())
                         .SelectMany(chunk => chunk?.Items ?? Array.Empty<MapPlaceableData>()))
            {
                if (placed?.Properties == null ||
                    !placed.Properties.TryGetValue(NeutralCampField, out BinTreeProperty campProperty) ||
                    campProperty is not BinTreeStruct camp ||
                    !camp.Properties.TryGetValue(CampDefinitionField, out BinTreeProperty linkProperty) ||
                    linkProperty is not BinTreeObjectLink link)
                    continue;

                BinTreeObject definition = Find(scene.MaterialsDocument, link.Value) ?? Find(scene.SharedMaterials, link.Value);
                if (definition == null ||
                    !definition.Properties.TryGetValue(CampNameField, out BinTreeProperty nameProperty) ||
                    nameProperty is not BinTreeString name)
                    continue;

                var variants = new List<string>();
                if (definition.Properties.TryGetValue(CampVariantsField, out BinTreeProperty variantsProperty) &&
                    variantsProperty is BinTreeContainer list)
                {
                    foreach (BinTreeStruct variant in list.Elements.OfType<BinTreeStruct>())
                        if (variant.Properties.TryGetValue(CampVariantNameField, out BinTreeProperty variantName) &&
                            variantName is BinTreeString text && !string.IsNullOrWhiteSpace(text.Value))
                            variants.Add(text.Value);
                }
                camps.Add(new Camp(name.Value, variants, placed.Transform));
            }
            return camps;
        }

        /// <summary>The Character folder of a skin path or BIN: "sru_dragon_air" for "Characters/sru_dragon_air/Skins/Skin0".</summary>
        internal static string CharacterOfSkin(string skin)
        {
            if (string.IsNullOrWhiteSpace(skin)) return null;
            Match match = Regex.Match(skin, @"characters[\\/]([^\\/]+)[\\/]skins", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>
        /// Whether a camp or variant name names the Character: every word of "DragonAir" (dragon, air) is a
        /// word of "sru_dragon_air".
        /// </summary>
        internal static bool Names(string campName, string character)
        {
            string[] wanted = Words(campName);
            if (wanted.Length == 0) return false;
            var words = new HashSet<string>(Words(character), StringComparer.Ordinal);
            return wanted.All(words.Contains);
        }

        private static string[] Words(string text) =>
            string.IsNullOrWhiteSpace(text)
                ? Array.Empty<string>()
                : Regex.Split(Regex.Replace(text, "([a-z0-9])([A-Z])", "$1 $2"), @"[^A-Za-z0-9]+")
                    .Where(word => word.Length > 0)
                    .Select(word => word.ToLowerInvariant())
                    .ToArray();

        private static BinTreeObject Find(BinTree tree, uint pathHash) =>
            tree?.Objects != null && tree.Objects.TryGetValue(pathHash, out BinTreeObject found) ? found : null;
    }
}
