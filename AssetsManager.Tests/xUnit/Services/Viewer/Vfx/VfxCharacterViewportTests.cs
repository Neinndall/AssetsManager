using System;
using System.Linq;
using System.Numerics;
using System.Windows.Input;
using AssetsManager.Services.Viewer.Animation;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Services.Viewer.Vfx.Session;
using AssetsManager.Views.Helpers;
using AssetsManager.Views.Models.Viewer;
using Xunit;
using AssetsManager.Services.Viewer.Vfx.Semantics;

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
        public void SharedAutoRotationKeepsItsAngleWhenStoppedAndResumesAtViewerSpeed()
        {
            double yaw = ViewerAutoRotation.Advance(350d, 1d);
            Assert.Equal(20d, yaw, 8);
            Assert.Equal(yaw, ViewerAutoRotation.Advance(yaw, 0d));
            Assert.Equal(50d, ViewerAutoRotation.Advance(yaw, 1d), 8);
            Assert.Equal(345d, ViewerAutoRotation.Advance(-45d, 1d), 8);
            double stepped = 350d;
            for (int i = 0; i < 4; i++) stepped = ViewerAutoRotation.Advance(stepped, .25d);
            Assert.Equal(yaw, stepped, 8);
        }

        [Fact]
        public void AutoRotateKeepsActorAnglesAcrossStopsSelectionChangesAndResume()
        {
            using var loading = new VfxLoadingService();
            var first = new VfxSceneActor(new VfxSkinItem { OwnerName = "Aatrox", SkinIndex = 0 })
            {
                RotationY = 35d, PositionX = -100d
            };
            var second = new VfxSceneActor(new VfxSkinItem { OwnerName = "Aatrox", SkinIndex = 1 })
            {
                RotationY = 90d, PositionX = 100d
            };
            var tab = new VfxWorkspaceTab { Kind = VfxWorkspaceTabKind.Skin };
            tab.Actors.Add(first);
            tab.Actors.Add(second);
            tab.FocusedActor = first;
            VfxSceneActorRuntime CreateRuntime() => new(loading, new VfxLoadingService.Bundle(), null,
                new SceneModel(), null, new AnimationService(null), new VfxRenderSession(loadingService: loading), null, null);
            var runtimes = new[] { CreateRuntime(), CreateRuntime() };
            void Select(VfxSceneActor actor, ModifierKeys modifiers = ModifierKeys.None)
            {
                tab.SelectionAnchor = SelectionBehavior.SelectItems(tab.Actors, tab.SelectionAnchor, actor,
                    modifiers, item => item.IsSelected, (item, selected) => item.IsSelected = selected);
                if (actor?.IsSelected == true) tab.FocusedActor = actor;
            }
            void Verify(double deltaSeconds, double firstYaw, double secondYaw, bool enabled = true)
            {
                if (enabled)
                    VfxCharacterViewportSemantics.RotateSelectedActors(tab.Actors, deltaSeconds, "stage");
                for (int i = 0; i < runtimes.Length; i++)
                    runtimes[i].ApplyPlacement(tab.Actors[i]);
                Assert.Equal(firstYaw, runtimes[0].Model.RotationY);
                Assert.Equal(secondYaw, runtimes[1].Model.RotationY);
                Assert.Equal(-100d, runtimes[0].Model.PositionX);
                Assert.Equal(100d, runtimes[1].Model.PositionX);
                Assert.Equal(firstYaw, first.RotationY);
                Assert.Equal(secondYaw, second.RotationY);
            }
            try
            {
                Verify(1d, 65d, 90d);
                Assert.True(first.PlacementCustomized);
                Assert.Equal("stage", first.PlacedOnKey);
                Assert.False(second.PlacementCustomized);
                Verify(1d, 65d, 90d, enabled: false);
                Verify(0d, 65d, 90d);
                Verify(.5d, 80d, 90d);
                Select(second);
                Verify(1d, 80d, 120d);
                Select(first, ModifierKeys.Control);
                Verify(1d, 110d, 150d);
                Verify(1d, 110d, 150d, enabled: false);
                Select(null);
                Verify(1d, 110d, 150d);
                Select(first);
                Verify(1d, 140d, 150d);
                var copy = tab.CopyForBackdrop("copy", "map", "Map");
                Assert.Equal(140d, copy.Actors[0].RotationY);
                Assert.Equal(150d, copy.Actors[1].RotationY);
            }
            finally
            {
                foreach (var runtime in runtimes)
                {
                    runtime.ReleaseCpuState();
                    runtime.Session.Dispose();
                }
            }
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
