using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Services.Viewer.Parsing;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Map
{
    public sealed class MapPlaceableParserTests
    {
        [Fact]
        public void ParserReadsCommonPlaceableContractAndTransposesTransform()
        {
            const uint chunkHash = 0x11111111;
            const uint keyHash = 0x22222222;
            const uint classHash = 0x33333333;
            const uint controllerHash = 0x44444444;
            Matrix4x4 stored = Matrix4x4.CreateTranslation(10145, -73, 3866);
            BinTree tree = Tree(Container(
                chunkHash,
                Item(
                    keyHash,
                    classHash,
                    new BinTreeString(MapPlaceableParser.NameField, "Sandfall1"),
                    new BinTreeMatrix44(MapPlaceableParser.TransformField, stored),
                    new BinTreeU8(MapPlaceableParser.VisibilityFlagsField, 4),
                    new BinTreeObjectLink(MapPlaceableParser.VisibilityControllerField, controllerHash))));

            IReadOnlyList<MapPlaceableChunkData> chunks = new MapPlaceableParser().Parse(tree);

            MapPlaceableChunkData chunk = Assert.Single(chunks);
            Assert.Equal(chunkHash, chunk.EntryHash);
            MapPlaceableData placed = Assert.Single(chunk.Items);
            Assert.Equal(keyHash, placed.KeyHash);
            Assert.Equal(classHash, placed.ClassHash);
            Assert.Equal("Sandfall1", placed.Name);
            Assert.Equal(new Vector3(10145, -73, 3866), placed.Position);
            Assert.Equal(stored, placed.Transform);
            Assert.Equal((byte)4, placed.Visibility);
            Assert.Equal(controllerHash, placed.VisibilityController);
            Assert.True(placed.IsVisibleOnLayer(2));
            Assert.False(placed.IsVisibleOnLayer(0));
        }

        [Fact]
        public void MissingCommonFieldsUseLtkDefaults()
        {
            BinTree tree = Tree(Container(
                0x11111111,
                Item(0x22222222, 0x33333333)));

            MapPlaceableData placed = Assert.Single(Assert.Single(new MapPlaceableParser().Parse(tree)).Items);

            Assert.Equal(string.Empty, placed.Name);
            Assert.Equal(Matrix4x4.Identity, placed.Transform);
            Assert.Equal(MapPlaceableData.EveryLayer, placed.Visibility);
            Assert.Null(placed.VisibilityController);
            for (int layer = 0; layer < 8; layer++)
                Assert.True(placed.IsVisibleOnLayer(layer));
            Assert.False(placed.IsVisibleOnLayer(8));
        }

        [Fact]
        public void HashedNameUsesZeroPrefixedHexLikeLtk()
        {
            BinTree tree = Tree(Container(
                0x11111111,
                Item(
                    0x22222222,
                    0x33333333,
                    new BinTreeHash(MapPlaceableParser.NameField, 0x00abcdef))));

            MapPlaceableData placed = Assert.Single(Assert.Single(new MapPlaceableParser().Parse(tree)).Items);

            Assert.Equal("0x00abcdef", placed.Name);
        }

        [Fact]
        public void ParserKeepsFileOrderAndOmitsEmptyContainers()
        {
            BinTree tree = Tree(
                Container(0x10000001, Item(0x20000001, 0x30000001)),
                Container(0x10000002),
                Container(0x10000003, Item(0x20000003, 0x30000003)));

            IReadOnlyList<MapPlaceableChunkData> chunks = new MapPlaceableParser().Parse(tree);

            Assert.Collection(
                chunks,
                first => Assert.Equal(0x10000001u, first.EntryHash),
                second => Assert.Equal(0x10000003u, second.EntryHash));
        }

        private static BinTree Tree(params BinTreeObject[] objects) =>
            new(objects, Array.Empty<string>());

        private static BinTreeObject Container(uint chunkHash, params KeyValuePair<BinTreeProperty, BinTreeProperty>[] items) =>
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
