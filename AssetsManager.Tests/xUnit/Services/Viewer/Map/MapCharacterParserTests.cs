using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Services.Viewer.Map.Parsing;
using AssetsManager.Services.Viewer.Map.Semantics;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Map
{
    public sealed class MapCharacterParserTests
    {
        [Fact]
        public void GameplayObjectWearsCharacterComponentSkinAndReadsTeam()
        {
            BinTree tree = Tree(Container(
                0x10000001,
                Item(0x20000001, Fnv1a.HashLower("GameplayObject"), GameplayProperties(null)),
                Item(0x20000002, Fnv1a.HashLower("GameplayObject"), GameplayProperties(200))));
            IReadOnlyList<MapPlaceableChunkData> chunks = new MapPlaceableParser().Parse(tree);

            IReadOnlyList<MapCharacterData> characters = new MapCharacterParser().Parse(chunks);

            Assert.Equal(2, characters.Count);
            Assert.Equal("Characters/Turret/Skins/Skin0", characters[0].Skin);
            Assert.Equal(MapPlaceableParser.Hex(Fnv1a.HashLower("Turret_T1_C_01")), characters[0].Name);
            Assert.Null(characters[0].Team);
            Assert.Equal(200u, characters[1].Team);
        }

        [Fact]
        public void LevelPropDerivesSkinFromNameAndSkinId()
        {
            BinTree tree = Tree(Container(
                0x10000001,
                Item(0x20000001, MapCharacterParser.GdsMapObjectClass, LevelPropProperties("LevelProp_sru_snail9", 0)),
                Item(0x20000002, MapCharacterParser.GdsMapObjectClass, LevelPropProperties("LevelProp_Srx_Banner_VerticalThin12", 3))));

            IReadOnlyList<MapCharacterData> characters = new MapCharacterParser().Parse(new MapPlaceableParser().Parse(tree));

            Assert.Equal("Characters/sru_snail/Skins/Skin0", characters[0].Skin);
            Assert.Equal("Characters/Srx_Banner_VerticalThin/Skins/Skin3", characters[1].Skin);
        }

        [Fact]
        public void LevelPropReadsDefaultAnimationFromAnimationInfo()
        {
            var properties = new List<BinTreeProperty>(LevelPropProperties("LevelProp_sru_bird3", 0))
            {
                new BinTreeContainer(
                    MapCharacterParser.ExtraInfoField,
                    BinPropertyType.Struct,
                    new BinTreeProperty[]
                    {
                        new BinTreeStruct(
                            0,
                            MapCharacterParser.AnimationInfoClass,
                            new BinTreeProperty[]
                            {
                                new BinTreeString(MapCharacterParser.DefaultAnimationField, "Idle2")
                            })
                    })
            };
            BinTree tree = Tree(Container(
                0x10000001,
                Item(0x20000001, MapCharacterParser.GdsMapObjectClass, properties.ToArray()),
                Item(0x20000002, MapCharacterParser.GdsMapObjectClass, LevelPropProperties("LevelProp_sru_snail1", 0))));

            IReadOnlyList<MapCharacterData> characters = new MapCharacterParser().Parse(new MapPlaceableParser().Parse(tree));

            Assert.Equal("Idle2", characters[0].Animation);
            Assert.Null(characters[1].Animation);
        }

        [Fact]
        public void NonLevelPropAndPlaceableWithoutCharacterAreIgnored()
        {
            BinTree tree = Tree(Container(
                0x10000001,
                Item(0x20000001, MapCharacterParser.GdsMapObjectClass, GdsProperties("Info_BrazierLocation4", 9)),
                Item(0x20000002, MapCharacterParser.GdsMapObjectClass, GdsProperties("LevelProp_12", 10)),
                Item(0x20000003, Fnv1a.HashLower("MapLocator"), Array.Empty<BinTreeProperty>())));

            Assert.Empty(new MapCharacterParser().Parse(new MapPlaceableParser().Parse(tree)));
        }

        [Fact]
        public void StoodCharactersExcludeNeutralTeamOtherLayerAndController()
        {
            MapCharacterData order = Character("Order", visibility: 255, controller: null, team: null);
            MapCharacterData chaos = Character("Chaos", visibility: 255, controller: null, team: 200);
            MapCharacterData dragon = Character("Dragon", visibility: 255, controller: null, team: 300);
            MapCharacterData mountain = Character("Mountain", visibility: 4, controller: null, team: null);
            MapCharacterData banner = Character("Banner", visibility: 255, controller: 0x76c50391, team: null);

            IReadOnlyList<MapCharacterData> stood = MapCharacterSemantics.StoodOnLayer(
                new[] { order, chaos, dragon, mountain, banner },
                0);

            Assert.Equal(new[] { order, chaos }, stood);
        }

        [Fact]
        public void SceneTransformConjugatesTheViewportXMirror()
        {
            Matrix4x4 translated = Matrix4x4.CreateTranslation(1748, 95, 2270);
            Matrix4x4 mirrored = MapCharacterSemantics.SceneTransform(translated);
            Assert.Equal(new Vector3(-1748, 95, 2270), new Vector3(mirrored.M41, mirrored.M42, mirrored.M43));
            Assert.Equal(1f, mirrored.M11);
            Assert.Equal(1f, mirrored.M22);
            Assert.Equal(1f, mirrored.M33);

            var quarterYaw = new Matrix4x4(
                0, 0, -1, 0,
                0, 1, 0, 0,
                1, 0, 0, 0,
                0, 0, 0, 1);
            Matrix4x4 yaw = MapCharacterSemantics.SceneTransform(quarterYaw);
            Assert.Equal(1f, yaw.M13);
            Assert.Equal(-1f, yaw.M31);
        }

        [Fact]
        public void SkinFileMatchesLtkPathSpelling()
        {
            Assert.Equal(
                "data/characters/srx_banner_verticalthin/skins/skin0.bin",
                MapCharacterSemantics.SkinFile("Characters/Srx_Banner_VerticalThin/Skins/Skin0"));
        }

        private static BinTreeProperty[] GameplayProperties(uint? team)
        {
            var properties = new List<BinTreeProperty>
            {
                new BinTreeHash(MapPlaceableParser.NameField, Fnv1a.HashLower("Turret_T1_C_01")),
                new BinTreeEmbedded(
                    MapCharacterParser.CharacterField,
                    Fnv1a.HashLower("CharacterComponent"),
                    new BinTreeProperty[]
                    {
                        new BinTreeString(MapCharacterParser.SkinField, "Characters/Turret/Skins/Skin0")
                    })
            };
            if (team.HasValue)
            {
                properties.Add(new BinTreeEmbedded(
                    MapCharacterParser.TeamField,
                    Fnv1a.HashLower("TeamComponent"),
                    new BinTreeProperty[]
                    {
                        new BinTreeU32(MapCharacterParser.TeamField, team.Value)
                    }));
            }
            return properties.ToArray();
        }

        private static BinTreeProperty[] LevelPropProperties(string name, uint skinId)
        {
            var properties = new List<BinTreeProperty>(GdsProperties(name, 10));
            if (skinId != 0)
                properties.Add(new BinTreeU32(MapCharacterParser.ObjectSkinIdField, skinId));
            return properties.ToArray();
        }

        private static BinTreeProperty[] GdsProperties(string name, byte type) =>
            new BinTreeProperty[]
            {
                new BinTreeString(MapPlaceableParser.NameField, name),
                new BinTreeU8(MapCharacterParser.ObjectTypeField, type)
            };

        private static MapCharacterData Character(string name, byte visibility, uint? controller, uint? team)
        {
            var placed = new MapPlaceableData(
                0x0000000c,
                0x00000001,
                0,
                name,
                Matrix4x4.Identity,
                visibility,
                controller,
                new Dictionary<uint, BinTreeProperty>());
            return new MapCharacterData(placed, "Characters/Turret/Skins/Skin0", team, null);
        }

        private static BinTree Tree(params BinTreeObject[] objects) =>
            new(objects, Array.Empty<string>());

        private static BinTreeObject Container(
            uint chunkHash,
            params KeyValuePair<BinTreeProperty, BinTreeProperty>[] items) =>
            new(
                chunkHash,
                MapPlaceableParser.PlaceableContainerClass,
                new BinTreeProperty[]
                {
                    new BinTreeMap(
                        MapPlaceableParser.ItemsField,
                        BinPropertyType.Hash,
                        BinPropertyType.Struct,
                        items)
                });

        private static KeyValuePair<BinTreeProperty, BinTreeProperty> Item(
            uint keyHash,
            uint classHash,
            params BinTreeProperty[] properties) =>
            new(
                new BinTreeHash(0, keyHash),
                new BinTreeStruct(0, classHash, properties));
    }
}
