using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Services.Viewer.Parsing;
using AssetsManager.Services.Viewer.Runtime;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Map
{
    public sealed class MapParticleSceneRuntimeTests
    {
        [Fact]
        public void UpdateAdvancesOnlyVisiblePlacementsAndClampsLongFrames()
        {
            MapParticleRuntime visible = Runtime("Visible", new Vector3(0f, 0f, -10f));
            MapParticleRuntime outside = Runtime("Outside", new Vector3(10000f, 0f, -10f));
            using var scene = new MapParticleSceneRuntime(new[] { visible, outside });
            Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
                MathF.PI / 2f,
                1f,
                1f,
                100f);

            scene.Update(projection, 0.5f);

            Assert.Equal(new[] { visible }, scene.VisibleRuntimes);
            Assert.Equal(MapParticleSemantics.LongestStepSeconds, visible.Graph.Root.CurrentTime, 5);
            Assert.Equal(0f, outside.Graph.Root.CurrentTime, 5);
        }

        [Fact]
        public void RestartRewindsPlacedDriversToTheirInitialSeededState()
        {
            MapParticleRuntime runtime = Runtime("Restart", new Vector3(0f, 0f, -10f));
            using var scene = new MapParticleSceneRuntime(new[] { runtime });

            runtime.Advance(0.25f);
            Assert.Equal(0.25f, runtime.Graph.Root.CurrentTime, 5);

            scene.Restart();

            Assert.Equal(0f, runtime.Graph.Root.CurrentTime, 5);
            Assert.Empty(scene.VisibleRuntimes);
        }

        [Fact]
        public void HiddenPlacementNeitherAdvancesNorDraws()
        {
            MapParticleRuntime runtime = Runtime("Hidden", new Vector3(0f, 0f, -10f));
            using var scene = new MapParticleSceneRuntime(new[] { runtime });
            Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
                MathF.PI / 2f,
                1f,
                1f,
                100f);

            var hidden = new HashSet<string>
            {
                MapOutlineSemantics.ItemId(runtime.Particle.ChunkHash, runtime.Particle.KeyHash)
            };
            scene.Update(projection, 1f / 60f, hidden);

            Assert.Empty(scene.VisibleRuntimes);
            Assert.Equal(0f, runtime.Graph.Root.CurrentTime, 5);
        }

        [Fact]
        public void OutlineVisibilityUsesChunkAndChunkKeyIds()
        {
            const uint chunk = 0x11223344;
            const uint key = 0xaabbccdd;
            var itemHidden = new HashSet<string> { "0x11223344/0xaabbccdd" };
            var chunkHidden = new HashSet<string> { "0x11223344" };

            Assert.Equal("0x11223344", MapOutlineSemantics.ChunkId(chunk));
            Assert.Equal("0x11223344/0xaabbccdd", MapOutlineSemantics.ItemId(chunk, key));
            Assert.True(MapOutlineSemantics.IsHidden(itemHidden, chunk, key));
            Assert.True(MapOutlineSemantics.IsHidden(chunkHidden, chunk, key));
            Assert.False(MapOutlineSemantics.IsHidden(itemHidden, chunk, 0x01020304));
        }

        [Fact]
        public void ViewportMatricesMirrorEngineXBeforeTheCamera()
        {
            Matrix4x4 view = Matrix4x4.CreateLookAt(
                new Vector3(25f, 40f, 80f),
                Vector3.Zero,
                Vector3.UnitY);
            Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
                MathF.PI / 3f,
                16f / 9f,
                1f,
                500f);
            Matrix4x4 viewProjection = view * projection;
            var enginePoint = new Vector4(20f, 5f, -30f, 1f);
            var viewportPoint = new Vector4(-20f, 5f, -30f, 1f);

            Vector4 throughMapVfx = Vector4.Transform(
                enginePoint,
                MapParticleSemantics.ViewportViewProjection(viewProjection));
            Vector4 expected = Vector4.Transform(viewportPoint, viewProjection);

            AssertVector(expected, throughMapVfx);
        }

        [Fact]
        public void CameraCullingUsesTheMirroredMapPosition()
        {
            Matrix4x4 view = Matrix4x4.CreateLookAt(
                new Vector3(-100f, 0f, 0f),
                new Vector3(-100f, 0f, -1f),
                Vector3.UnitY);
            Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
                MathF.PI / 4f,
                1f,
                1f,
                1000f);
            Matrix4x4 viewProjection = view * projection;

            Assert.True(MapParticleSemantics.IsVisible(
                viewProjection,
                new Vector3(100f, 0f, -20f),
                1f));
            Assert.False(MapParticleSemantics.IsVisible(
                viewProjection,
                new Vector3(-100f, 0f, -20f),
                1f));
        }

        private static MapParticleRuntime Runtime(string name, Vector3 position)
        {
            const uint systemHash = 0x30000001;
            var placeable = new MapPlaceableData(
                0x10000001,
                unchecked((uint)name.GetHashCode()),
                MapParticleParser.MapParticleClass,
                name,
                Matrix4x4.CreateTranslation(position),
                MapPlaceableData.EveryLayer,
                null,
                new Dictionary<uint, BinTreeProperty>());
            var particle = new MapParticleData(placeable, systemHash, false, false);
            var system = new VfxSystemDefinition(
                systemHash,
                "MapVfx",
                "Maps/Test/MapVfx",
                Array.Empty<VfxEmitterDefinition>());
            return MapParticleRuntime.Create(
                particle,
                system,
                new Dictionary<uint, VfxSystemDefinition> { [systemHash] = system },
                new Dictionary<uint, uint>());
        }

        private static void AssertVector(Vector4 expected, Vector4 actual)
        {
            Assert.Equal(expected.X, actual.X, 4);
            Assert.Equal(expected.Y, actual.Y, 4);
            Assert.Equal(expected.Z, actual.Z, 4);
            Assert.Equal(expected.W, actual.W, 4);
        }
    }
}
