using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Services.Viewer.Parsing;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Map
{
    public sealed class MapCharacterSpawnSemanticsTests
    {
        private const uint CampClass = 0xd178749c;
        private const uint EntityClass = 0xad65d8c4;
        private const uint DragonCampDefinition = 0x7faa12bd;
        private static readonly Vector3 DragonCamp = new(9837.674f, -82.277f, 4397.761f);

        [Fact]
        public void HeldDrakeSpawnsAtItsCampFacingTheCampDirection()
        {
            Matrix4x4 camp = Matrix4x4.CreateRotationY(0.77f) * Matrix4x4.CreateTranslation(DragonCamp);
            MapSceneData scene = Scene(
                new[] { Place(CampClass, camp, CampProperties()) },
                new[] { Character("Characters/sru_dragon_air/Skins/Skin0", EntityClass, new Vector3(11357.94f, -82.27f, -1852.19f)) });

            Matrix4x4? spawn = MapCharacterSpawnSemantics.SpawnTransform(scene, "sru_dragon_air");

            Assert.Equal(camp, spawn);
        }

        [Fact]
        public void EntityNearItsCampKeepsItsOwnTransform()
        {
            Vector3 nearby = DragonCamp + new Vector3(30f, 0f, -100f);
            MapSceneData scene = Scene(
                new[] { Place(CampClass, Matrix4x4.CreateTranslation(DragonCamp), CampProperties()) },
                new[] { Character("Characters/SRU_Dragon/Skins/Skin0", EntityClass, nearby) });

            Assert.Equal(nearby, MapCharacterSpawnSemantics.SpawnTransform(scene, "SRU_Dragon")?.Translation);
        }

        [Fact]
        public void LevelPropKeepsItsAuthoredPlaceEvenWhenACampNamesIt()
        {
            var prop = new Vector3(-4157f, -5639f, 2518f);
            MapSceneData scene = Scene(
                new[] { Place(CampClass, Matrix4x4.CreateTranslation(DragonCamp), CampProperties()) },
                new[] { Character("Characters/sru_dragon_prop/Skins/Skin0", MapCharacterParser.GdsMapObjectClass, prop) });

            Assert.Equal(prop, MapCharacterSpawnSemantics.SpawnTransform(scene, "sru_dragon_prop")?.Translation);
        }

        [Fact]
        public void CampSpawnsACharacterTheMapDoesNotPlaceAndOthersFallBack()
        {
            MapSceneData scene = Scene(
                new[] { Place(CampClass, Matrix4x4.CreateTranslation(DragonCamp), CampProperties()) },
                new MapCharacterData[0]);

            Assert.Equal(DragonCamp, MapCharacterSpawnSemantics.SpawnTransform(scene, "sru_dragon_elder")?.Translation);
            Assert.Null(MapCharacterSpawnSemantics.SpawnTransform(scene, "Aatrox"));
        }

        [Theory]
        [InlineData("DragonAir", "sru_dragon_air", true)]
        [InlineData("Dragon", "SRU_Dragon", true)]
        [InlineData("SRU_RiftHerald", "SRU_RiftHerald", true)]
        [InlineData("DragonAir", "sru_dragon_fire", false)]
        [InlineData("Order Red", "SRU_Red", false)]
        public void CampNamesMatchEveryWordOfTheCharacter(string camp, string character, bool expected) =>
            Assert.Equal(expected, MapCharacterSpawnSemantics.Names(camp, character));

        [Fact]
        public void CharacterFolderComesFromSkinPathsAndBinFiles()
        {
            Assert.Equal("sru_dragon_air", MapCharacterSpawnSemantics.CharacterOfSkin("Characters/sru_dragon_air/Skins/Skin0"));
            Assert.Equal("SRU_Gromp", MapCharacterSpawnSemantics.CharacterOfSkin(@"C:\Map11.wad.client\data\characters\SRU_Gromp\skins\skin0.bin"));
            Assert.Null(MapCharacterSpawnSemantics.CharacterOfSkin("data/maps/shipping/map11/map11.bin"));
        }

        private static Dictionary<uint, BinTreeProperty> CampProperties() => new()
        {
            [MapCharacterSpawnSemantics.NeutralCampField] = new BinTreeEmbedded(
                MapCharacterSpawnSemantics.NeutralCampField,
                0,
                new BinTreeProperty[] { new BinTreeObjectLink(MapCharacterSpawnSemantics.CampDefinitionField, DragonCampDefinition) })
        };

        private static MapPlaceableData Place(uint classHash, Matrix4x4 transform, IReadOnlyDictionary<uint, BinTreeProperty> properties) =>
            new(1, 2, classHash, null, transform, MapPlaceableData.EveryLayer, null, properties);

        private static MapCharacterData Character(string skin, uint classHash, Vector3 position) =>
            new(Place(classHash, Matrix4x4.CreateTranslation(position), new Dictionary<uint, BinTreeProperty>()), skin, 300, null);

        private static MapSceneData Scene(IReadOnlyList<MapPlaceableData> placeables, IReadOnlyList<MapCharacterData> characters)
        {
            var definition = new BinTreeObject(DragonCampDefinition, 0x3f04641e, new BinTreeProperty[]
            {
                new BinTreeString(MapCharacterSpawnSemantics.CampNameField, "Dragon"),
                new BinTreeContainer(MapCharacterSpawnSemantics.CampVariantsField, BinPropertyType.Embedded, new BinTreeProperty[]
                {
                    Variant("DragonFire"),
                    Variant("DragonAir"),
                    Variant("DragonElder")
                })
            });
            return new MapSceneData(
                null, null, null,
                new BinTree(new[] { definition }, new string[0]),
                null, null,
                new[] { new MapPlaceableChunkData(3, "Container", placeables) },
                characters,
                null, null);
        }

        private static BinTreeEmbedded Variant(string name) =>
            new(0, 0xdb26669a, new BinTreeProperty[] { new BinTreeString(MapCharacterSpawnSemantics.CampVariantNameField, name) });
    }
}
