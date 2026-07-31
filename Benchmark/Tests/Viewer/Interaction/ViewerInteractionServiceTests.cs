using System;
using System.Collections.Generic;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using AssetsManager.Services.Viewer.Interaction;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.BenchmarkTests.Services.Viewer.Interaction
{
    public sealed class ViewerInteractionServiceTests
    {
        [Fact]
        public void CenteredPositionsRespectWidthsOrderAndGap()
        {
            IReadOnlyList<double> positions =
                ViewerInteractionService.CalculateCenteredPositions(
                    new[] { 100d, 200d, 100d },
                    20);

            Assert.Equal(new[] { -170d, 0d, 170d }, positions);
        }

        [Fact]
        public void ProjectionMapsWorldOriginToViewportCenter()
        {
            Matrix4x4 view = Matrix4x4.CreateLookAt(
                new Vector3(0, 0, 1000),
                Vector3.Zero,
                Vector3.UnitY);
            Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
                MathF.PI / 4,
                16f / 9f,
                10,
                100000);

            bool projected = ViewerInteractionService.TryProject(
                Vector3.Zero,
                view * projection,
                1920,
                1080,
                out Point point);

            Assert.True(projected);
            Assert.Equal(960, point.X, 6);
            Assert.Equal(540, point.Y, 6);
        }

        [Fact]
        public void PickingChoosesVisibleModelUnderPointer()
        {
            SceneModel centered = CreateModel("Centered", 0);
            SceneModel offset = CreateModel("Offset", 500);
            var camera = new PerspectiveCamera(
                new Point3D(0, 0, 1000),
                new Vector3D(0, 0, -1000),
                new Vector3D(0, 1, 0),
                45);

            SceneModel picked = ViewerInteractionService.PickModel(
                new[] { offset, centered },
                new Point(400, 300),
                800,
                600,
                camera);

            Assert.Same(centered, picked);
            centered.Dispose();
            offset.Dispose();
        }

        private static SceneModel CreateModel(string name, double positionX)
        {
            var geometry = new MeshGeometry3D
            {
                Positions = new Point3DCollection
                {
                    new(-50, -50, -50),
                    new(50, 50, 50)
                },
                TriangleIndices = new Int32Collection()
            };
            var model = new SceneModel
            {
                Name = name,
                PositionX = positionX
            };
            model.Parts.Add(new ModelPart(name, new GeometryModel3D { Geometry = geometry }));
            return model;
        }
    }
}
