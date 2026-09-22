using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Services.Viewer.Runtime;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Map
{
    public sealed class MapSceneRuntimeTests
    {
        [Fact]
        public void UpdateAdvancesTheStructureClockIndependentlyOfParticles()
        {
            using var runtime = new MapSceneRuntime(
                Scene(),
                Array.Empty<MapCharacterRuntimeGroup>(),
                new MapParticleSceneRuntime(Array.Empty<MapParticleRuntime>()));
            runtime.ShowParticles = false;

            runtime.Update(Matrix4x4.Identity, 0.25f);
            runtime.Update(Matrix4x4.Identity, 0.5f);

            Assert.Equal(0.75f, runtime.CharacterTimeSeconds, 5);
            Assert.Empty(runtime.Particles.VisibleRuntimes);
        }

        [Fact]
        public void StructureToggleRestartsItsIndependentClock()
        {
            using var runtime = new MapSceneRuntime(
                Scene(),
                Array.Empty<MapCharacterRuntimeGroup>(),
                new MapParticleSceneRuntime(Array.Empty<MapParticleRuntime>()));

            runtime.Update(Matrix4x4.Identity, 0.75f);
            Assert.Equal(0.75f, runtime.CharacterTimeSeconds, 5);

            runtime.ShowStructures = false;
            runtime.Update(Matrix4x4.Identity, 1f);
            Assert.Equal(0f, runtime.CharacterTimeSeconds, 5);

            runtime.ShowStructures = true;
            runtime.Update(Matrix4x4.Identity, 0.25f);
            Assert.Equal(0.25f, runtime.CharacterTimeSeconds, 5);
        }

        [Fact]
        public void HiddenIdsCanBeAddedAndRemovedWithoutRebuildingTheScene()
        {
            using var runtime = new MapSceneRuntime(
                Scene(),
                Array.Empty<MapCharacterRuntimeGroup>(),
                new MapParticleSceneRuntime(Array.Empty<MapParticleRuntime>()));
            const string id = "0x00000001/0x00000002";

            runtime.SetHidden(id, true);
            Assert.Contains(id, runtime.Hidden);

            runtime.SetHidden(id, false);
            Assert.DoesNotContain(id, runtime.Hidden);
        }

        private static MapSceneData Scene()
        {
            MapPath path = MapPath.FromEntryPath("maps/mapgeometry/map11/base_srx");
            var source = new MapSceneSource(path, @"C:\map\base_srx.mapgeo", @"C:\map");
            var assets = new MapSceneAssets(source, null, null);
            var geometry = new MapGeometryData(
                Array.Empty<Vector3>(),
                Array.Empty<Vector3>(),
                Array.Empty<Vector2>(),
                null,
                Array.Empty<uint>(),
                Array.Empty<MapGeometryMeshData>(),
                Array.Empty<MapGeometrySubmeshData>(),
                Array.Empty<string>());
            var catalog = new MapParticleSystemCatalog(
                new Dictionary<uint, VfxSystemDefinition>(),
                new Dictionary<uint, uint>(),
                Array.Empty<MapParticleSystemGroupData>());

            return new MapSceneData(
                source,
                assets,
                geometry,
                null,
                Array.Empty<MapMaterialDefinition>(),
                new Dictionary<string, System.Windows.Media.Imaging.BitmapSource>(),
                Array.Empty<MapPlaceableChunkData>(),
                Array.Empty<MapCharacterData>(),
                Array.Empty<MapParticleData>(),
                catalog,
                null);
        }
    }
}
