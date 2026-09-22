using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Map
{
    public sealed class MapOutlineSemanticsTests
    {
        [Fact]
        public void BuildKeepsAuthoredChunkAndItemOrderAndUsesChunkLeafLabel()
        {
            MapPlaceableData first = Placeable(0x10000001, 0x20000001, 0x30000001, "First", 10, 20, 30);
            MapPlaceableData second = Placeable(0x10000001, 0x20000002, 0x30000002, "Second", 40, 50, 60);
            MapPlaceableData third = Placeable(0x10000002, 0x20000003, 0x30000003, "Third", 70, 80, 90);
            var chunks = new[]
            {
                new MapPlaceableChunkData(0x10000001, "Maps/MapGeometry/Test/Center", new[] { first, second }),
                new MapPlaceableChunkData(0x10000002, "Maps/MapGeometry/Test/Outer", new[] { third })
            };

            IReadOnlyList<MapOutlineChunkData> outline = MapOutlineSemantics.Build(
                chunks,
                characters: null,
                particles: null,
                hashResolver: null);

            Assert.Collection(
                outline,
                chunk =>
                {
                    Assert.Equal("Center", chunk.Label);
                    Assert.Equal("0x10000001", chunk.Id);
                    Assert.Collection(
                        chunk.Items,
                        item => Assert.Equal("First", item.Name),
                        item => Assert.Equal("Second", item.Name));
                },
                chunk => Assert.Equal("Outer", chunk.Label));
        }

        [Fact]
        public void BuildClassifiesDrawnRowsFromResolvedCharacterAndParticleSets()
        {
            MapPlaceableData characterPlaceable = Placeable(1, 11, 0xdead0001, "Tower", 1, 2, 3);
            MapPlaceableData particlePlaceable = Placeable(1, 12, 0x592ef6c3, "Glow", 4, 5, 6);
            MapPlaceableData locatorPlaceable = Placeable(1, 13, 0xa844df61, "Camera", 7, 8, 9);
            MapPlaceableData groupPlaceable = Placeable(1, 14, 0xf3726d48, "Group", 10, 11, 12);
            MapPlaceableData audioPlaceable = Placeable(1, 15, 0xa783cfd5, "Audio", 13, 14, 15);
            MapPlaceableData otherPlaceable = Placeable(1, 16, 0x01020304, "Other", 16, 17, 18);
            var chunk = new MapPlaceableChunkData(
                1,
                "Chunk",
                new[] { characterPlaceable, particlePlaceable, locatorPlaceable, groupPlaceable, audioPlaceable, otherPlaceable });
            var characters = new[] { new MapCharacterData(characterPlaceable, "Characters/Test/Skins/Skin0", null, null) };
            var particles = new[] { new MapParticleData(particlePlaceable, 0x12345678, false, false) };

            MapOutlineItemData[] items = Assert.Single(MapOutlineSemantics.Build(
                    new[] { chunk },
                    characters,
                    particles,
                    hashResolver: null))
                .Items
                .ToArray();

            Assert.Equal(MapOutlineItemKind.Character, items[0].Kind);
            Assert.True(items[0].IsDrawable);
            Assert.Equal(MapOutlineItemKind.Particle, items[1].Kind);
            Assert.True(items[1].IsDrawable);
            Assert.Equal(MapOutlineItemKind.Locator, items[2].Kind);
            Assert.Equal(MapOutlineItemKind.Group, items[3].Kind);
            Assert.Equal(MapOutlineItemKind.Audio, items[4].Kind);
            Assert.Equal(MapOutlineItemKind.Other, items[5].Kind);
            Assert.All(items[2..], item => Assert.False(item.IsDrawable));
        }

        [Fact]
        public void ItemHiddenStateInheritsItsChunkLikeLtk()
        {
            const uint chunk = 0x11223344;
            const uint key = 0x55667788;
            var hidden = new HashSet<string> { MapOutlineSemantics.ChunkId(chunk) };

            Assert.True(MapOutlineSemantics.IsHidden(hidden, chunk, key));

            hidden.Clear();
            hidden.Add(MapOutlineSemantics.ItemId(chunk, key));
            Assert.True(MapOutlineSemantics.IsHidden(hidden, chunk, key));
            Assert.False(MapOutlineSemantics.IsHidden(hidden, chunk, 0x01020304));
        }

        private static MapPlaceableData Placeable(
            uint chunk,
            uint key,
            uint classHash,
            string name,
            float x,
            float y,
            float z) =>
            new(
                chunk,
                key,
                classHash,
                name,
                Matrix4x4.CreateTranslation(x, y, z),
                MapPlaceableData.EveryLayer,
                null,
                new Dictionary<uint, BinTreeProperty>());
    }
}
