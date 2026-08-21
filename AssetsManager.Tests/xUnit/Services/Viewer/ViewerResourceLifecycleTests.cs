using System.Collections.Generic;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Views.Controls.Viewer;
using AssetsManager.Views.Helpers;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer
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
                SourceVertexIndices = new[] { 0, 1, 2 },
                Lightmap = new MapLightmapBinding("lightmap", new float[6])
            };
            var scene = new SceneModel();
            scene.Parts.Add(part);

            scene.Dispose();

            Assert.Null(scene.RootVisual);
            Assert.Null(scene.SkinnedMesh);
            Assert.Null(scene.Skeleton);
            Assert.Null(scene.MapLightingProfile);
            Assert.Empty(scene.Parts);
            Assert.Empty(scene.Animations);
            Assert.Empty(textures);
            Assert.Null(part.Visual);
            Assert.Null(part.Geometry);
            Assert.Null(part.AllTextures);
            Assert.Null(part.SourceVertexIndices);
            Assert.Null(part.Lightmap);
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
        public void ProjectionPlanesPreserveDepthPrecisionAtMapScale()
        {
            var lookDirection = new Vector3(0f, -18000f, -15000f);
            float nearPlane = ViewerViewportControl.CalculateProjectionNearPlane(lookDirection);
            float farPlane = ViewerViewportControl.CalculateProjectionFarPlane(lookDirection);

            Assert.InRange(nearPlane, 200f, 250f);
            Assert.True(farPlane > 90000f);
            Assert.True(farPlane / nearPlane < 500f);
        }

        [Fact]
        public void ProjectionNearPlaneAllowsCloseMapSurfaceViewing()
        {
            var lookDirection = new Vector3(0f, -100f, 0f);

            float nearPlane = ViewerViewportControl.CalculateProjectionNearPlane(
                lookDirection,
                isMapGeometry: true);

            Assert.Equal(0.1f, nearPlane);
        }

        [Fact]
        public void ProjectionNearPlaneAllowsSubUnitMapDistance()
        {
            float nearPlane = ViewerViewportControl.CalculateProjectionNearPlane(
                Vector3.UnitY,
                isMapGeometry: true);

            Assert.Equal(0.01f, nearPlane);
        }

        [Fact]
        public void MapCameraCollisionPreservesHorizontalMovementAboveGround()
        {
            var requestedPosition = new Point3D(120, -50, -340);

            Point3D constrained = CustomCameraController.ConstrainMapPosition(
                requestedPosition,
                collisionEnabled: true);

            Assert.Equal(120, constrained.X);
            Assert.Equal(CustomCameraController.MapGroundHeight, constrained.Y);
            Assert.Equal(-340, constrained.Z);
        }

        [Fact]
        public void TexturePremultiplicationRemovesInvisibleRgbFromGeneratedMipmaps()
        {
            byte[] pixels =
            {
                255, 255, 255, 0,
                100, 50, 200, 128,
                10, 20, 30, 255
            };

            GlMeshRenderer.PremultiplyBgra(pixels);

            Assert.Equal(new byte[]
            {
                0, 0, 0, 0,
                50, 25, 100, 128,
                10, 20, 30, 255
            }, pixels);
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

        [Fact]
        public void MeshUploadDataReusesStaticUvsWhileRefreshingAnimatedGeometry()
        {
            var mesh = new MeshGeometry3D
            {
                Positions = new Point3DCollection
                {
                    new(0, 0, 0),
                    new(1, 0, 0),
                    new(0, 1, 0)
                },
                TriangleIndices = new Int32Collection { 0, 1, 2 },
                TextureCoordinates = new PointCollection
                {
                    new(0, 0),
                    new(1, 0),
                    new(0, 1)
                }
            };
            var vertexData = new GlMeshVertexData(mesh.Positions.Count);

            vertexData.Update(mesh, updateTextureCoordinates: true);
            float[] originalUvs =
            {
                vertexData.Data[6], vertexData.Data[7],
                vertexData.Data[14], vertexData.Data[15],
                vertexData.Data[22], vertexData.Data[23]
            };

            mesh.Positions = new Point3DCollection
            {
                new(0, 0, 1),
                new(1, 0, 1),
                new(0, 1, 1)
            };
            vertexData.Update(mesh, updateTextureCoordinates: false);

            Assert.Equal(1f, vertexData.Data[2]);
            Assert.Equal(1f, vertexData.Data[10]);
            Assert.Equal(1f, vertexData.Data[18]);
            Assert.Equal(originalUvs, new[]
            {
                vertexData.Data[6], vertexData.Data[7],
                vertexData.Data[14], vertexData.Data[15],
                vertexData.Data[22], vertexData.Data[23]
            });
            Assert.Equal(
                Vector3.UnitZ,
                new Vector3(vertexData.Data[3], vertexData.Data[4], vertexData.Data[5]));
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
            PerspectiveCamera camera = VfxInspectorControl.CreateVfxCamera();
            Point3D target = camera.Position + camera.LookDirection;

            Assert.Equal(new Point3D(0, 0, 0), target);
            Assert.True(camera.LookDirection.Length > 200);
            Assert.Equal(0, camera.Position.X);
            Assert.True(camera.Position.Z > 0);
        }
    }
}
