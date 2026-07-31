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
        [InlineData("Front", 0, 0, 25, 0, 0, -25, 0, 1, 0)]
        [InlineData("Back", 0, 0, -25, 0, 0, 25, 0, 1, 0)]
        [InlineData("Left", -25, 0, 0, 25, 0, 0, 0, 1, 0)]
        [InlineData("Right", 25, 0, 0, -25, 0, 0, 0, 1, 0)]
        [InlineData("Top", 0, 25, 0, 0, -25, 0, 0, 0, -1)]
        [InlineData("Bottom", 0, -25, 0, 0, 25, 0, 0, 0, 1)]
        public void CardinalCameraViewsKeepTheModelAtTheirExactTarget(
            string view,
            double positionX,
            double positionY,
            double positionZ,
            double lookX,
            double lookY,
            double lookZ,
            double upX,
            double upY,
            double upZ)
        {
            var target = new Point3D(10, 20, 30);

            var pose = ViewerViewportControl.CalculateCameraView(view, target, 25);

            Assert.NotNull(pose);
            Assert.Equal(
                target + new Vector3D(positionX, positionY, positionZ),
                pose.Value.Position);
            Assert.Equal(new Vector3D(lookX, lookY, lookZ), pose.Value.LookDirection);
            Assert.Equal(new Vector3D(upX, upY, upZ), pose.Value.UpDirection);
            Assert.Equal(target, pose.Value.Position + pose.Value.LookDirection);
            Assert.Equal(
                0,
                Vector3D.DotProduct(pose.Value.LookDirection, pose.Value.UpDirection));
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
