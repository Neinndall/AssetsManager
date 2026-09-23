using System;
using System.Collections.Generic;
using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Viewer.Animation;
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
        public void FullBackdropTextureWaveReplacesThePreviewSet()
        {
            using var runtime = new MapSceneRuntime(
                Scene(),
                Array.Empty<MapCharacterRuntimeGroup>(),
                new MapParticleSceneRuntime(Array.Empty<MapParticleRuntime>()));
            var full = new Dictionary<string, MapTextureImage>();

            runtime.SetBackdropTextures(full);

            Assert.Same(full, runtime.BackdropTextures);
        }

        [Fact]
        public void IncrementalBackdropTextureMergePreservesLandedEntriesAndReplacesOnlyTheMatchingKey()
        {
            using var runtime = new MapSceneRuntime(
                Scene(),
                Array.Empty<MapCharacterRuntimeGroup>(),
                new MapParticleSceneRuntime(Array.Empty<MapParticleRuntime>()));
            MapTextureImage first = Image(10);
            MapTextureImage second = Image(20);
            MapTextureImage sharpened = Image(30);

            runtime.MergeBackdropTextures(new Dictionary<string, MapTextureImage>
            {
                ["material/a"] = first,
                ["material/b"] = second
            });
            runtime.MergeBackdropTextures(new Dictionary<string, MapTextureImage>
            {
                ["material/a"] = sharpened
            });

            Assert.Equal(2, runtime.BackdropTextures.Count);
            Assert.Same(sharpened, runtime.BackdropTextures["material/a"]);
            Assert.Same(second, runtime.BackdropTextures["material/b"]);
        }

        [Fact]
        public void IncrementalProgramAndLightmapMergesKeepTheirAuthoredKeyComparison()
        {
            using var runtime = new MapSceneRuntime(
                Scene(),
                Array.Empty<MapCharacterRuntimeGroup>(),
                new MapParticleSceneRuntime(Array.Empty<MapParticleRuntime>()));
            MapTextureImage raw = Image(40);
            MapTextureImage firstLight = Image(50);
            MapTextureImage replacementLight = Image(60);

            runtime.MergeBackdropProgramTextures(new Dictionary<string, MapTextureImage>
            {
                ["program:Material:Diffuse"] = raw
            });
            runtime.MergeBackdropLightmaps(new Dictionary<string, MapTextureImage>
            {
                ["ASSETS/Maps/Light.TEX"] = firstLight
            });
            runtime.MergeBackdropLightmaps(new Dictionary<string, MapTextureImage>
            {
                ["assets/maps/light.tex"] = replacementLight
            });

            Assert.Same(raw, runtime.BackdropProgramTextures["program:Material:Diffuse"]);
            Assert.Single(runtime.BackdropLightmaps);
            Assert.Same(replacementLight, runtime.BackdropLightmaps["ASSETS/Maps/Light.TEX"]);
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

        [Fact]
        public void ReplacingCharacterGroupsKeepsSceneIdentityAndDisposesPreviousOwners()
        {
            MapSceneData scene = Scene();
            var previousAnimation = new MapCharacterAnimationRuntime(null, null);
            var previous = new MapCharacterRuntimeGroup(
                null,
                previousAnimation,
                Array.Empty<MapCharacterData>());
            using var runtime = new MapSceneRuntime(
                scene,
                new[] { previous },
                new MapParticleSceneRuntime(Array.Empty<MapParticleRuntime>()));
            var replacement = new MapCharacterRuntimeGroup(
                null,
                new MapCharacterAnimationRuntime(null, null),
                Array.Empty<MapCharacterData>());

            runtime.SetCharacterGroups(new[] { replacement });

            Assert.Same(scene, runtime.Scene);
            Assert.Same(replacement, Assert.Single(runtime.CharacterGroups));
            Assert.Throws<ObjectDisposedException>(() => previousAnimation.Evaluate(null, null, 0f));
        }

        [Fact]
        public void ReplacingParticleRuntimeKeepsSceneIdentityAndDisposesPreviousOwner()
        {
            MapSceneData scene = Scene();
            var previous = new MapParticleSceneRuntime(Array.Empty<MapParticleRuntime>());
            using var runtime = new MapSceneRuntime(
                scene,
                Array.Empty<MapCharacterRuntimeGroup>(),
                previous);
            var replacement = new MapParticleSceneRuntime(Array.Empty<MapParticleRuntime>());

            runtime.SetParticles(replacement);

            Assert.Same(scene, runtime.Scene);
            Assert.Same(replacement, runtime.Particles);
            Assert.Throws<ObjectDisposedException>(() => previous.Restart());
        }

        private static MapTextureImage Image(byte value)
        {
            BitmapSource bitmap = BitmapSource.Create(
                1,
                1,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                new byte[] { value, value, value, 255 },
                4);
            bitmap.Freeze();
            return new MapTextureImage(new[] { bitmap });
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
                new Dictionary<string, MapTextureImage>(),
                Array.Empty<MapPlaceableChunkData>(),
                Array.Empty<MapCharacterData>(),
                Array.Empty<MapParticleData>(),
                catalog,
                null);
        }
    }
}
