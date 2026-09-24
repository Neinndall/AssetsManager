using System;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using AssetsManager.Services.Viewer.Vfx.Session;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx
{
    public sealed class VfxPreviewReferenceTests
    {
        [Fact]
        public void GroundUsesThePreviewWorldScale()
        {
            Assert.Equal(3200f, VfxPreviewSurfaceRenderer.GroundSize);
            Assert.Equal(-0.5f, VfxPreviewSurfaceRenderer.GroundDrop);
        }

        [Fact]
        public void CameraPresetsUseTheReferenceLensesAndAxes()
        {
            VfxCameraStand game = VfxPreviewCamera.Stand(VfxPreviewCameraPreset.Game);
            Assert.False(game.Orthographic);
            Assert.Equal(40f, game.FieldOfView);
            Assert.Equal(1000f, game.Nearest);
            Assert.Equal(2250f, game.Farthest);
            Assert.Equal(2f, VfxPreviewCamera.NearPlane);
            Assert.Equal(20000f, VfxPreviewCamera.FarPlane);

            float pitch = 56f * MathF.PI / 180f;
            Vector3 expectedGame = Vector3.Normalize(new Vector3(0f, MathF.Sin(pitch), -MathF.Cos(pitch)));
            AssertVector(expectedGame, game.Direction);
            AssertVector(Vector3.UnitY, game.Up);

            VfxCameraStand orbit = VfxPreviewCamera.Stand(VfxPreviewCameraPreset.Orbit);
            Assert.False(orbit.Orthographic);
            Assert.Equal(45f, orbit.FieldOfView);
            Assert.Null(orbit.Nearest);
            Assert.Null(orbit.Farthest);

            VfxCameraStand top = VfxPreviewCamera.Stand(VfxPreviewCameraPreset.Top);
            Assert.True(top.Orthographic);
            AssertVector(Vector3.UnitY, top.Direction);
            AssertVector(Vector3.UnitZ, top.Up);

            VfxCameraStand front = VfxPreviewCamera.Stand(VfxPreviewCameraPreset.Front);
            Assert.True(front.Orthographic);
            AssertVector(Vector3.UnitZ, front.Direction);
            AssertVector(Vector3.UnitY, front.Up);

            VfxCameraStand side = VfxPreviewCamera.Stand(VfxPreviewCameraPreset.Side);
            Assert.True(side.Orthographic);
            AssertVector(Vector3.UnitX, side.Direction);
            AssertVector(Vector3.UnitY, side.Up);
        }

        [Fact]
        public void OrthographicAndPerspectiveFramingPreserveTheSameVisibleScale()
        {
            const float width = 920f;
            const float aspect = 16f / 9f;

            float reach = VfxPreviewCamera.ReachOfOrthographicWidth(width, aspect);
            float restoredWidth = VfxPreviewCamera.OrthographicWidthOfReach(reach, aspect);

            Assert.True(float.IsFinite(reach));
            Assert.True(reach > 0f);
            Assert.Equal(width, restoredWidth, precision: 4);
        }

        [Fact]
        public void SkyGroundGridAndStageDisplayPreferencesAreIndependent()
        {
            var model = new VfxInspectorModel();

            Assert.False(model.ShowPreviewSky);
            Assert.True(model.ShowPreviewGrid);
            Assert.False(model.ShowPreviewGround);
            Assert.False(model.ShowPreviewStage);
            Assert.Equal(1, model.PreviewDisplayCount);

            model.ShowPreviewSky = true;
            Assert.True(model.ShowPreviewSky);
            Assert.Equal(2, model.PreviewDisplayCount);

            model.ShowPreviewGround = true;
            Assert.True(model.ShowPreviewGround);
            Assert.Equal(3, model.PreviewDisplayCount);

            model.ShowPreviewStage = true;
            Assert.True(model.ShowPreviewStage);
            Assert.Equal(4, model.PreviewDisplayCount);

            model.ShowPreviewGrid = false;
            Assert.False(model.ShowPreviewGrid);
            Assert.True(model.ShowPreviewSky);
            Assert.True(model.ShowPreviewGround);
            Assert.True(model.ShowPreviewStage);
            Assert.Equal(3, model.PreviewDisplayCount);
        }

        [Fact]
        public void StageUsesReferenceViewportScale()
        {
            Assert.Equal(VfxRigMotion.ChampionHeight * 16f, VfxPreviewSurfaceRenderer.StageSize);
            Assert.Equal(-0.5f, VfxPreviewSurfaceRenderer.GroundDrop);
            Assert.Equal(100f, VfxPreviewSurfaceRenderer.GridCellSize);
            Assert.Equal(500f, VfxPreviewSurfaceRenderer.GridSectionSize);
            Assert.Equal(1.5f, VfxPreviewSurfaceRenderer.GridFadeStrength);
        }

        [Fact]
        public void ViewModesUseIndependentWireOverlayLikeCurrentLtk()
        {
            Assert.Equal((true, false, 0.35f), VfxRenderSession.ResolvePreviewPasses(VfxPreviewViewMode.Lit, false, true));
            Assert.Equal((true, true, 0.35f), VfxRenderSession.ResolvePreviewPasses(VfxPreviewViewMode.Lit, true, true));
            Assert.Equal((true, false, 0.35f), VfxRenderSession.ResolvePreviewPasses(VfxPreviewViewMode.Unshaded, true, true));
            Assert.Equal((true, true, 0.35f), VfxRenderSession.ResolvePreviewPasses(VfxPreviewViewMode.Untextured, true, true));
            Assert.Equal((false, true, 1f), VfxRenderSession.ResolvePreviewPasses(VfxPreviewViewMode.Wireframe, false, true));
            Assert.Equal((true, false, 1f), VfxRenderSession.ResolvePreviewPasses(VfxPreviewViewMode.Wireframe, false, false));
            Assert.Equal(1f, VfxRenderSession.WireframeOpacity(VfxPreviewViewMode.Wireframe));
            Assert.Equal(0.35f, VfxRenderSession.WireframeOpacity(VfxPreviewViewMode.Lit, true));

            Assert.Contains("uniform int uWireframePass;", VfxShaderSource.ParticleFragment);
            Assert.Contains("uniform vec4 uWireframeColor;", VfxShaderSource.ParticleFragment);
            Assert.Contains("if (uWireframePass != 0)", VfxShaderSource.ParticleFragment);
            Assert.Contains("fragColor = uWireframeColor;", VfxShaderSource.ParticleFragment);

            Assert.Contains("uniform int uWireframePass;", VfxShaderSource.MeshFragment);
            Assert.Contains("uniform vec4 uWireframeColor;", VfxShaderSource.MeshFragment);
            Assert.Contains("if (uWireframePass != 0)", VfxShaderSource.MeshFragment);
            Assert.Contains("fragColor = uWireframeColor;", VfxShaderSource.MeshFragment);
        }

        private static void AssertVector(Vector3 expected, Vector3 actual)
        {
            Assert.Equal(expected.X, actual.X, precision: 5);
            Assert.Equal(expected.Y, actual.Y, precision: 5);
            Assert.Equal(expected.Z, actual.Z, precision: 5);
        }
    }
}
