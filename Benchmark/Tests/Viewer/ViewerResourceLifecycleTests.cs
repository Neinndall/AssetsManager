using System.Collections.Generic;
using System.Numerics;
using System.Windows.Media.Media3D;
using AssetsManager.Services.Viewer;
using AssetsManager.Views.Controls.Viewer;
using AssetsManager.Views.Dialogs;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.BenchmarkTests.Services.Viewer
{
    public sealed class ViewerResourceLifecycleTests
    {
        [Fact]
        public void SceneDisposalReleasesOwnedVisualsAndPartResources()
        {
            var textures = new Dictionary<string, System.Windows.Media.Imaging.BitmapSource>
            {
                ["body"] = null
            };
            var part = new ModelPart(
                "Body",
                new GeometryModel3D
                {
                    Geometry = new MeshGeometry3D(),
                    Material = new DiffuseMaterial()
                })
            {
                AllTextures = textures,
                SourceVertexIndices = new[] { 0, 1, 2 }
            };
            var scene = new SceneModel();
            scene.Parts.Add(part);

            scene.Dispose();

            Assert.Null(scene.RootVisual);
            Assert.Null(scene.SkinnedMesh);
            Assert.Null(scene.Skeleton);
            Assert.Empty(scene.Parts);
            Assert.Empty(scene.Animations);
            Assert.Empty(textures);
            Assert.Null(part.Visual);
            Assert.Null(part.Geometry);
            Assert.Null(part.AllTextures);
            Assert.Null(part.SourceVertexIndices);
        }

        [Fact]
        public void SceneDisposalIsIdempotentAndDetachesPartEvents()
        {
            var part = new ModelPart { Name = "Eyes" };
            var scene = new SceneModel { IsMeshSyncEnabled = true };
            int visibilityChanges = 0;
            scene.MeshVisibilityChanged += _ => visibilityChanges++;
            scene.Parts.Add(part);

            scene.Dispose();
            scene.Dispose();
            part.IsVisible = true;

            Assert.Equal(0, visibilityChanges);
        }

        [Fact]
        public void ProjectionFarPlaneExpandsForMapScaleCameraDistances()
        {
            float farPlane = ViewerViewportControl.CalculateProjectionFarPlane(
                new Vector3(0f, -18000f, -15000f));

            Assert.True(farPlane > 90000f);
        }

        [Theory]
        [InlineData(1920, 1080, 3840, 2160)]
        [InlineData(800, 600, 2880, 2160)]
        [InlineData(3440, 1440, 3840, 1607)]
        public void UhdSnapshotPreservesViewportAspectRatio(
            int sourceWidth,
            int sourceHeight,
            int expectedWidth,
            int expectedHeight)
        {
            (int width, int height) = OpenGlSnapshotService.CalculateUhdSize(
                sourceWidth,
                sourceHeight);

            Assert.Equal(expectedWidth, width);
            Assert.Equal(expectedHeight, height);
        }

        [Fact]
        public void VfxStudioCameraPreservesItsRealDistanceToTheOrigin()
        {
            PerspectiveCamera camera = VfxInspectorWindow.CreateVfxCamera();
            Point3D target = camera.Position + camera.LookDirection;

            Assert.Equal(new Point3D(0, 0, 0), target);
            Assert.True(camera.LookDirection.Length > 800);
            Assert.True(camera.Position.X > 0);
            Assert.Equal(0, camera.Position.Z);
        }
    }
}
