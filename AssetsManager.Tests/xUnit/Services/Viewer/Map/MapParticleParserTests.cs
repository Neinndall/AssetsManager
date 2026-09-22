using System;
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
    public sealed class MapParticleParserTests
    {
        [Fact]
        public void ParserReadsOnlyMapParticlesThatLinkASystem()
        {
            BinTree tree = Tree(Container(
                0x10000001,
                Item(
                    0x20000001,
                    MapParticleParser.MapParticleClass,
                    new BinTreeString(MapPlaceableParser.NameField, "Brazier1"),
                    new BinTreeObjectLink(MapParticleParser.SystemField, 0x30000001),
                    new BinTreeBool(MapParticleParser.TransitionalField, true),
                    new BinTreeBool(MapParticleParser.StartDisabledField, false)),
                Item(0x20000002, MapParticleParser.MapParticleClass),
                Item(
                    0x20000003,
                    0x12345678,
                    new BinTreeObjectLink(MapParticleParser.SystemField, 0x30000002))));

            IReadOnlyList<MapPlaceableChunkData> chunks = new MapPlaceableParser().Parse(tree);
            MapParticleData particle = Assert.Single(new MapParticleParser().Parse(chunks));

            Assert.Equal(0x10000001u, particle.ChunkHash);
            Assert.Equal(0x20000001u, particle.KeyHash);
            Assert.Equal("Brazier1", particle.Name);
            Assert.Equal(0x30000001u, particle.SystemHash);
            Assert.True(particle.Transitional);
            Assert.False(particle.StartDisabled);
        }

        [Fact]
        public void ParserReadsFlagPropertiesLikeLtk()
        {
            BinTree tree = Tree(Container(
                0x10000001,
                Item(
                    0x20000001,
                    MapParticleParser.MapParticleClass,
                    new BinTreeObjectLink(MapParticleParser.SystemField, 0x30000001),
                    new BinTreeBitBool(MapParticleParser.TransitionalField, true),
                    new BinTreeBitBool(MapParticleParser.StartDisabledField, true))));

            IReadOnlyList<MapPlaceableChunkData> chunks = new MapPlaceableParser().Parse(tree);
            MapParticleData particle = Assert.Single(new MapParticleParser().Parse(chunks));

            Assert.True(particle.Transitional);
            Assert.True(particle.StartDisabled);
        }

        [Fact]
        public void PlayedParticlesMatchLtkBackdropRules()
        {
            MapParticleData near = Particle("Near", visibility: 255);
            MapParticleData far = Particle("Far", visibility: 255);
            MapParticleData mountain = Particle("Mountain", visibility: 4);
            MapParticleData transition = Particle("Transition", visibility: 255, transitional: true);
            MapParticleData scripted = Particle("Scripted", visibility: 255, startDisabled: true);
            MapParticleData trophy = Particle("Trophy", visibility: 255, controller: 0x8f1ab207);

            IReadOnlyList<MapParticleData> played = MapParticleSemantics.PlayedOnLayer(
                new[] { near, far, mountain, transition, scripted, trophy },
                0);

            Assert.Equal(new[] { near, far }, played);
        }

        [Fact]
        public void GroupingPreservesFirstSystemAndPlacementOrder()
        {
            MapParticleData first = Particle("Brazier1", system: 1);
            MapParticleData other = Particle("Mushroom1", system: 2);
            MapParticleData second = Particle("Brazier2", system: 1);

            IReadOnlyList<MapParticleGroupData> groups = MapParticleSemantics.GroupBySystem(
                new[] { first, other, second });

            Assert.Collection(
                groups,
                group =>
                {
                    Assert.Equal(1u, group.SystemHash);
                    Assert.Equal(new[] { first, second }, group.Particles);
                },
                group =>
                {
                    Assert.Equal(2u, group.SystemHash);
                    Assert.Equal(new[] { other }, group.Particles);
                });
        }

        [Fact]
        public void RigidTransformKeepsOriginAndRotationButDropsScale()
        {
            var authored = new Matrix4x4(
                0, 0, -2, 0,
                0, 2, 0, 0,
                2, 0, 0, 0,
                10145, -73, 3866, 1);

            Matrix4x4 rigid = MapParticleSemantics.RigidTransform(authored);

            Assert.Equal(new Vector3(10145, -73, 3866), rigid.Translation);
            Assert.Equal(new Vector3(0, 0, -1), Vector3.TransformNormal(Vector3.UnitX, rigid));
            Assert.Equal(Vector3.UnitY, Vector3.TransformNormal(Vector3.UnitY, rigid));
            Assert.Equal(Vector3.UnitX, Vector3.TransformNormal(Vector3.UnitZ, rigid));
        }

        [Fact]
        public void SeedIsStablePerPlaceableNameAndStepIsClamped()
        {
            Assert.Equal(MapParticleSemantics.Seed("Brazier1"), MapParticleSemantics.Seed("Brazier1"));
            Assert.NotEqual(MapParticleSemantics.Seed("Brazier1"), MapParticleSemantics.Seed("Brazier2"));
            Assert.Equal(0.1f, MapParticleSemantics.ClampFrameStep(5f));
            Assert.Equal(0.05f, MapParticleSemantics.ClampFrameStep(0.05f));
            Assert.Equal(0f, MapParticleSemantics.ClampFrameStep(float.NaN));
        }

        private static MapParticleData Particle(
            string name,
            uint system = 1,
            byte visibility = 255,
            uint? controller = null,
            bool transitional = false,
            bool startDisabled = false)
        {
            var placed = new MapPlaceableData(
                0x0000000c,
                unchecked((uint)name.GetHashCode()),
                MapParticleParser.MapParticleClass,
                name,
                Matrix4x4.Identity,
                visibility,
                controller,
                new Dictionary<uint, BinTreeProperty>());
            return new MapParticleData(placed, system, transitional, startDisabled);
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
