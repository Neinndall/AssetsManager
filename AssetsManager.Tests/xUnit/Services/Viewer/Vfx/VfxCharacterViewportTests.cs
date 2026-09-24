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
    }
}
