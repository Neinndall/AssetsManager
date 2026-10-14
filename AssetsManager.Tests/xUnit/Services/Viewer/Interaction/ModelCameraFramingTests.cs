using System;
using System.Numerics;
using System.Windows.Media.Media3D;
using AssetsManager.Services.Viewer.Interaction;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Utils.Viewport;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Interaction
{
    public sealed class ModelCameraFramingTests
    {
        [Theory]
        [InlineData(1.31)]
        [InlineData(2.0)]
        [InlineData(0.65)]
        public void WorldBoundsIncludeSkinScaleAndPlacement(double scale)
        {
            var model = CreateModel(scale);
            Rect3D bounds = ViewerInteractionService.GetWorldBounds(model);

            Assert.Equal(model.PositionX - 100d * scale, bounds.X, 3);
            Assert.Equal(model.PositionY, bounds.Y, 3);
            Assert.Equal(model.PositionZ - 40d * scale, bounds.Z, 3);
            Assert.Equal(200d * scale, bounds.SizeX, 3);
            Assert.Equal(300d * scale, bounds.SizeY, 3);
            Assert.Equal(80d * scale, bounds.SizeZ, 3);
            model.Dispose();
        }

        [Theory]
        [InlineData(1.31, 1.2)]
        [InlineData(1.31, 0.35)]
        [InlineData(0.65, 2.4)]
        [InlineData(2.0, 0.6)]
        public void ResetFrameKeepsRenderedModelInsideWideAndNarrowViewports(double scale, double aspect)
        {
            var model = CreateModel(scale);
            model.RotationX = 15d;
            model.RotationY = 31d;
            model.RotationZ = 12d;
            Rect3D bounds = ViewerInteractionService.GetWorldBounds(model);
            var center = new Vector3(
                (float)(bounds.X + bounds.SizeX / 2d),
                (float)(bounds.Y + bounds.SizeY / 2d),
                (float)(bounds.Z + bounds.SizeZ / 2d));
            double radius = new Vector3D(bounds.SizeX, bounds.SizeY, bounds.SizeZ).Length / 2d;
            double distance = CameraPresets.CalculatePerspectiveFrameDistance(radius, 45d, aspect);
            Vector3 eye = center + new Vector3(0f, (float)(distance * 0.15d), (float)distance);
            Matrix4x4 viewProjection = Matrix4x4.CreateLookAt(eye, center, Vector3.UnitY) *
                Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4f, (float)aspect, 0.1f, 100000f);
            Matrix4x4 renderedWorld = GlMeshRenderer.CreateWorldMatrix(model);
            var mesh = (MeshGeometry3D)model.Parts[0].Geometry.Geometry;

            foreach (Point3D vertex in mesh.Positions)
            {
                Vector3 renderedPoint = Vector3.Transform(
                    new Vector3((float)vertex.X, (float)vertex.Y, (float)vertex.Z), renderedWorld);
                Vector4 clip = Vector4.Transform(new Vector4(renderedPoint, 1f), viewProjection);
                Assert.True(clip.W > 0f);
                Assert.InRange(clip.X / clip.W, -0.9f, 0.9f);
                Assert.InRange(clip.Y / clip.W, -0.9f, 0.9f);
                Assert.InRange(clip.Z / clip.W, 0f, 1f);
            }
            model.Dispose();
        }

        private static SceneModel CreateModel(double scale)
        {
            var mesh = new MeshGeometry3D();
            for (int corner = 0; corner < 8; corner++)
                mesh.Positions.Add(new Point3D(
                    (corner & 1) != 0 ? 100d : -100d,
                    (corner & 2) != 0 ? 300d : 0d,
                    (corner & 4) != 0 ? 40d : -40d));
            var model = new SceneModel
            {
                Scale = scale,
                PositionX = 140d,
                PositionY = 1000d,
                PositionZ = -25d
            };
            model.Parts.Add(new ModelPart("Body", new GeometryModel3D { Geometry = mesh }));
            return model;
        }
    }
}
