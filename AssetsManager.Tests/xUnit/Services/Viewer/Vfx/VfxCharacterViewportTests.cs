using System;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx
{
    public sealed class VfxCharacterViewportTests
    {
        [Theory]
        [InlineData("data/maps/mapgeometry/map11/base_srx.mapgeo", "Maps/MapGeometry/Map11/Base_SRX")]
        [InlineData("DATA/Maps/MapGeometry/Map22/Base.mapgeo", "Maps/MapGeometry/Map22/Base")]
        public void InstallationMapCatalogRecognizesCanonicalMapGeometry(string path, string expected)
        {
            Assert.True(VfxInstallationMapCatalog.TryMapGeometryPath(path, out MapPath map));
            Assert.Equal(expected, map.Value, ignoreCase: true);
        }

        [Theory]
        [InlineData("data/maps/mapgeometry/map11/base_srx.materials.bin")]
        [InlineData("data/maps/mapgeometry/map11/readme.txt")]
        [InlineData("characters/aatrox/skins/skin0/aatrox.skn")]
        public void InstallationMapCatalogRejectsNonGeometryAssets(string path)
        {
            Assert.False(VfxInstallationMapCatalog.TryMapGeometryPath(path, out _));
        }

        [Fact]
        public void InstallationMapCatalogFallbackUsesMapWadIdentity()
        {
            string[] paths = VfxInstallationMapCatalog.ConventionalGeometryPaths(@"C:\Game\Map11.wad.client").ToArray();

            Assert.Contains("data/maps/mapgeometry/map11/base.mapgeo", paths, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("data/maps/mapgeometry/map11/base_srx.mapgeo", paths, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("data/maps/mapgeometry/map11/base_tft.mapgeo", paths, StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public void BackdropKeyUsesLogicalMapInsteadOfPhysicalSource()
        {
            MapPath map = MapPath.FromEntryPath("Maps/MapGeometry/Map11/Base_SRX");
            var extracted = new MapSceneSource(map, @"C:\Project\data\maps\mapgeometry\map11\base_srx.mapgeo", @"C:\Project");
            var installed = new MapSceneSource(map, null, @"D:\Mods\Aatrox");

            Assert.Equal(
                VfxInstallationMapCatalog.BackdropKey(extracted),
                VfxInstallationMapCatalog.BackdropKey(installed));
            Assert.Contains("map11", VfxInstallationMapCatalog.Label(installed, projectSource: false), StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData(true, false, false, true)]
        [InlineData(false, false, true, false)]
        [InlineData(true, true, false, false)]
        [InlineData(false, true, true, true)]
        public void ManualSubmeshOverrideWinsOverClipVisibility(
            bool authoredVisible,
            bool overridden,
            bool manualVisible,
            bool expected)
        {
            Assert.Equal(
                expected,
                VfxCharacterViewportSemantics.ResolveSubmeshVisibility(authoredVisible, overridden, manualVisible));
        }

        [Fact]
        public void AutoRotateMatchesViewerSpeedAndWrapsWithoutChangingPlacementYaw()
        {
            double phase = VfxCharacterViewportSemantics.AdvanceAutoRotation(350d, 1d);
            Assert.Equal(20d, phase, 8);

            double manualYaw = 35d;
            Assert.Equal(55d, manualYaw + phase, 8);
            Assert.Equal(35d, manualYaw, 8);
        }

        [Theory]
        [InlineData(true, "maps/map11/base", "MAPS/MAP11/BASE", true)]
        [InlineData(false, "maps/map11/base", "maps/map11/base", false)]
        [InlineData(true, "maps/map11/base", "maps/map12/base", false)]
        [InlineData(true, "", "maps/map11/base", false)]
        [InlineData(true, "maps/map11/base", "", false)]
        public void LoadedMapIsAdoptedOnlyByTheSkinTabThatOwnsTheSameBackdrop(
            bool enabled,
            string requested,
            string loaded,
            bool expected)
        {
            Assert.Equal(
                expected,
                VfxCharacterViewportSemantics.CanAdoptLoadedBackdrop(enabled, requested, loaded));
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(true, false, true)]
        [InlineData(false, true, true)]
        [InlineData(true, true, true)]
        public void MapCleanupTouchesSharedVfxRendererOnlyWhenAMapCharacterClipOwnsIt(
            bool activeClip,
            bool groupPreviewClip,
            bool expected)
        {
            Assert.Equal(
                expected,
                VfxCharacterViewportSemantics.MapCharacterClipOwnsVfxRenderer(activeClip, groupPreviewClip));
        }

        [Fact]
        public void TangentBuilderCreatesFiniteFallbackForDegenerateUvTriangles()
        {
            Vector3[] positions =
            {
                new(0f, 0f, 0f),
                new(1f, 0f, 0f),
                new(0f, 1f, 0f)
            };
            Vector3[] normals = { Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ };
            Vector2[] uv = { Vector2.Zero, Vector2.Zero, Vector2.Zero };
            uint[] indices = { 0, 1, 2 };

            Vector4[] tangents = MeshTangentBuilder.Build(positions, normals, uv, indices);

            Assert.Equal(3, tangents.Length);
            Assert.All(tangents, tangent =>
            {
                Assert.True(float.IsFinite(tangent.X));
                Assert.True(float.IsFinite(tangent.Y));
                Assert.True(float.IsFinite(tangent.Z));
                Assert.Equal(1f, new Vector3(tangent.X, tangent.Y, tangent.Z).Length(), 5);
                Assert.True(MathF.Abs(tangent.W) == 1f);
            });
        }

        [Fact]
        public void CharacterPlacementWorldMirrorsXAxisToMatchCharacterMeshRenderConvention()
        {
            Matrix4x4 placement = VfxCharacterViewportSemantics.CharacterPlacementWorld(
                rotationX: 0d,
                rotationY: 0d,
                rotationZ: 0d,
                scaleMultiplier: 1d,
                positionX: 0d,
                positionY: 0d,
                positionZ: 0d);

            // X scale must be -1 to mirror character mesh along the X axis.
            Assert.Equal(-1f, placement.M11);
            Assert.Equal(1f, placement.M22);
            Assert.Equal(1f, placement.M33);
            Assert.Equal(1f, placement.M44);
            Assert.True(placement.GetDeterminant() < 0f);

            // A point with positive X (e.g. Mordekaiser's mace bone at X = +147.75)
            // must be mapped to negative X (-147.75) matching the mirrored mesh.
            var maceBonePosition = new Vector3(147.74594f, 134.79817f, -94.46582f);
            Vector3 transformed = Vector3.Transform(maceBonePosition, placement);

            Assert.Equal(-147.74594f, transformed.X, 3);
            Assert.Equal(134.79817f, transformed.Y, 3);
            Assert.Equal(-94.46582f, transformed.Z, 3);
        }

        [Fact]
        public void CharacterPlacementWorldMatchesGlMeshRendererMirroredMatrix()
        {
            var model = new SceneModel
            {
                RotationX = 15d,
                RotationY = 45d,
                RotationZ = 30d,
                Scale = 1.25d,
                PositionX = 100d,
                PositionY = 50d,
                PositionZ = -200d
            };

            Matrix4x4 meshWorld = GlMeshRenderer.CreateWorldMatrix(model, mirrorCharacterX: true);
            Matrix4x4 vfxPlacement = VfxCharacterViewportSemantics.CharacterPlacementWorld(
                model.RotationX,
                model.RotationY,
                model.RotationZ,
                model.Scale,
                model.PositionX,
                model.PositionY,
                model.PositionZ);

            Assert.Equal(meshWorld.M11, vfxPlacement.M11, 4);
            Assert.Equal(meshWorld.M12, vfxPlacement.M12, 4);
            Assert.Equal(meshWorld.M13, vfxPlacement.M13, 4);
            Assert.Equal(meshWorld.M14, vfxPlacement.M14, 4);
            Assert.Equal(meshWorld.M21, vfxPlacement.M21, 4);
            Assert.Equal(meshWorld.M22, vfxPlacement.M22, 4);
            Assert.Equal(meshWorld.M23, vfxPlacement.M23, 4);
            Assert.Equal(meshWorld.M24, vfxPlacement.M24, 4);
            Assert.Equal(meshWorld.M31, vfxPlacement.M31, 4);
            Assert.Equal(meshWorld.M32, vfxPlacement.M32, 4);
            Assert.Equal(meshWorld.M33, vfxPlacement.M33, 4);
            Assert.Equal(meshWorld.M34, vfxPlacement.M34, 4);
            Assert.Equal(meshWorld.M41, vfxPlacement.M41, 4);
            Assert.Equal(meshWorld.M42, vfxPlacement.M42, 4);
            Assert.Equal(meshWorld.M43, vfxPlacement.M43, 4);
            Assert.Equal(meshWorld.M44, vfxPlacement.M44, 4);
        }
    }
}
