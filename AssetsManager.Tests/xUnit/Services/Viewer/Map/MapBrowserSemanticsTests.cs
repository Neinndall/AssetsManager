using System;
using System.Collections.Generic;
using System.Numerics;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Viewer.Runtime;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Map
{
    public sealed class MapBrowserSemanticsTests
    {
        [Fact]
        public void SemanticTreeKeepsMapDataCharactersClipsAndParticlesTogether()
        {
            MapSceneRuntime runtime = CreateRuntime();
            try
            {
                MapBrowserNode root = MapBrowserSemantics.Build(runtime);

                Assert.Equal("Base_SRX", root.Title);
                Assert.Equal(MapBrowserNodeKind.Map, root.Kind);
                Assert.True(root.IsExpanded);
                Assert.Equal(5, root.Children.Count);

                MapBrowserNode geometry = Assert.IsType<MapBrowserNode>(root.Children[0]);
                Assert.Equal(MapBrowserNodeKind.Geometry, geometry.Kind);
                Assert.Contains("vertices", geometry.InspectorSummary);

                MapBrowserNode materials = Assert.IsType<MapBrowserNode>(root.Children[1]);
                Assert.Equal(MapBrowserNodeKind.Materials, materials.Kind);
                MapBrowserNode material = Assert.IsType<MapBrowserNode>(Assert.Single(materials.Children));
                Assert.Equal("Maps/Test/Stone", material.Title);
                Assert.Contains("Test", material.InspectorSummary);

                MapBrowserNode chunks = Assert.IsType<MapBrowserNode>(root.Children[2]);
                MapBrowserNode chunk = Assert.IsType<MapBrowserNode>(Assert.Single(chunks.Children));
                Assert.Equal("Chunk A", chunk.Title);
                Assert.True(chunk.CanHide);
                MapBrowserNode outlined = Assert.IsType<MapBrowserNode>(Assert.Single(chunk.Children));
                Assert.Equal("Turret_A", outlined.Title);
                Assert.True(outlined.CanHide);

                MapBrowserNode characters = Assert.IsType<MapBrowserNode>(root.Children[3]);
                MapBrowserNode skin = Assert.IsType<MapBrowserNode>(Assert.Single(characters.Children));
                Assert.Equal(MapBrowserNodeKind.CharacterSkin, skin.Kind);
                Assert.Contains("1 placements", skin.InspectorSummary);
                Assert.Contains("1 clips", skin.InspectorSummary);
                MapBrowserNode clips = Assert.IsType<MapBrowserNode>(skin.Children[0]);
                Assert.Equal(MapBrowserNodeKind.Clips, clips.Kind);
                MapBrowserNode clip = Assert.IsType<MapBrowserNode>(Assert.Single(clips.Children));
                Assert.Equal("Idle", clip.Title);
                Assert.Equal("idle.anm", clip.Subtitle);
                Assert.Contains("0 particle events", clip.InspectorSummary);
                MapCharacterClipSelection clipSelection = Assert.IsType<MapCharacterClipSelection>(clip.Payload);
                Assert.Same(skin.Payload, clipSelection.Group);
                Assert.Same(clipSelection.Group.Asset.AnimationGraph.Clips[0], clipSelection.Clip);
                MapBrowserNode characterPlacement = Assert.IsType<MapBrowserNode>(skin.Children[1]);
                Assert.Equal(MapBrowserNodeKind.CharacterPlacement, characterPlacement.Kind);
                Assert.True(characterPlacement.CanHide);

                MapBrowserNode particles = Assert.IsType<MapBrowserNode>(root.Children[4]);
                MapBrowserNode system = Assert.IsType<MapBrowserNode>(Assert.Single(particles.Children));
                Assert.Equal("MapSpark", system.Title);
                Assert.Contains("0 emitters", system.InspectorSummary);
                Assert.Contains("1 placements", system.InspectorSummary);
                MapBrowserNode particlePlacement = Assert.IsType<MapBrowserNode>(Assert.Single(system.Children));
                Assert.Equal(MapBrowserNodeKind.ParticlePlacement, particlePlacement.Kind);
                Assert.True(particlePlacement.CanHide);
                Assert.Contains("X 40", particlePlacement.InspectorSummary);

                var inspector = new VfxInspectorModel
                {
                    SelectedMapNode = system
                };
                Assert.Equal("MapSpark", inspector.ViewportSelectionTitle);
                Assert.Equal(system.InspectorSummary, inspector.ViewportSelectionDetail);
            }
            finally
            {
                runtime.Dispose();
            }
        }

        private static MapSceneRuntime CreateRuntime()
        {
            const uint chunkHash = 0x10000001;
            const uint characterKey = 0x20000001;
            const uint particleKey = 0x20000002;
            const uint systemHash = 0x30000001;

            var characterPlaceable = new MapPlaceableData(
                chunkHash,
                characterKey,
                0x40000001,
                "Turret_A",
                Matrix4x4.CreateTranslation(10f, 20f, 30f),
                MapPlaceableData.EveryLayer,
                null,
                new Dictionary<uint, BinTreeProperty>());
            var character = new MapCharacterData(
                characterPlaceable,
                "Characters/Turret/Skins/Skin0",
                100,
                "Idle");

            var particlePlaceable = new MapPlaceableData(
                chunkHash,
                particleKey,
                0x40000002,
                "Spark_A",
                Matrix4x4.CreateTranslation(40f, 50f, 60f),
                MapPlaceableData.EveryLayer,
                null,
                new Dictionary<uint, BinTreeProperty>());
            var particle = new MapParticleData(particlePlaceable, systemHash, false, false);
            var system = new VfxSystemDefinition(
                systemHash,
                "MapSpark",
                "Maps/Test/Particles/MapSpark",
                Array.Empty<VfxEmitterDefinition>());
            var particleGroup = new MapParticleSystemGroupData(systemHash, system, new[] { particle });
            var particleCatalog = new MapParticleSystemCatalog(
                new Dictionary<uint, VfxSystemDefinition> { [systemHash] = system },
                new Dictionary<uint, uint>(),
                new[] { particleGroup });

            var graph = new AnimationGraphDefinition(
                0x50000001,
                new[]
                {
                    new AnimationClipDefinition(
                        0x50000002,
                        0,
                        1f / 30f,
                        0f,
                        30f,
                        Array.Empty<AnimationClipEventDefinition>(),
                        ClipName: "Idle",
                        AnimationFilePath: "assets/characters/turret/animations/idle.anm")
                },
                Array.Empty<AnimationTrackDefinition>(),
                Array.Empty<AnimationMaskDefinition>(),
                Array.Empty<AnimationSyncGroupDefinition>());
            var skin = new MapCharacterSkinData(
                "Characters/Turret/Skins/Skin0",
                null,
                null,
                1f,
                MapCharacterSkinData.NoHiddenSubmeshes,
                graph.PathHash);
            var characterAsset = new MapCharacterAssetData(
                skin,
                null,
                null,
                null,
                new Dictionary<string, BitmapSource>(),
                Array.Empty<BinTree>(),
                graph);
            var characterGroup = new MapCharacterRuntimeGroup(
                characterAsset,
                null,
                new[] { character });

            MapPath map = MapPath.FromEntryPath("Maps/MapGeometry/Map11/Base_SRX");
            var source = new MapSceneSource(map, null, null);
            var geometry = new MapGeometryData(
                Array.Empty<Vector3>(),
                Array.Empty<Vector3>(),
                Array.Empty<Vector2>(),
                null,
                Array.Empty<uint>(),
                Array.Empty<MapGeometryMeshData>(),
                Array.Empty<MapGeometrySubmeshData>(),
                Array.Empty<string>());
            var material = new MapMaterialDefinition(
                "Maps/Test/Stone",
                0x60000001,
                false,
                false,
                "Shaders/Test",
                null,
                null,
                null,
                null,
                null,
                null,
                MapMaterialRenderState.Default,
                Array.Empty<string>());
            var outline = new[]
            {
                new MapOutlineChunkData(
                    "chunk:10000001",
                    chunkHash,
                    "Chunk A",
                    "Chunk A",
                    new[]
                    {
                        new MapOutlineItemData(
                            "item:10000001:20000001",
                            chunkHash,
                            characterKey,
                            "Turret_A",
                            "GameplayObject",
                            MapOutlineItemKind.Character,
                            characterPlaceable.Position,
                            MapPlaceableData.EveryLayer,
                            null)
                    })
            };
            var scene = new MapSceneData(
                source,
                new MapSceneAssets(source, null, null),
                geometry,
                null,
                new[] { material },
                new Dictionary<string, MapTextureImage>(),
                Array.Empty<MapPlaceableChunkData>(),
                new[] { character },
                new[] { particle },
                particleCatalog,
                Vector3.Zero,
                outline);

            return new MapSceneRuntime(
                scene,
                new[] { characterGroup },
                new MapParticleSceneRuntime(Array.Empty<MapParticleRuntime>()));
        }
    }
}
