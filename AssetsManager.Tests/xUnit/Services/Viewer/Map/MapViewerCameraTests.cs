using System.Numerics;
using System.Windows.Media.Media3D;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Views.Controls.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Map
{
    public sealed class MapViewerCameraTests
    {
        [Fact]
        public void MapFrameDistanceMatchesPerspectiveSphereFraming()
        {
            double radius = System.Math.Sqrt(1500d * 1500d + 300d * 300d + 1500d * 1500d);

            double distance = ViewerViewportControl.CalculateMapFrameDistance(
                radius,
                45d,
                16d / 9d);

            Assert.InRange(distance, 6435d, 6445d);
        }

        [Fact]
        public void NarrowViewportMovesTheMapCameraFartherAway()
        {
            double radius = System.Math.Sqrt(1500d * 1500d + 300d * 300d + 1500d * 1500d);

            double wide = ViewerViewportControl.CalculateMapFrameDistance(radius, 45d, 16d / 9d);
            double narrow = ViewerViewportControl.CalculateMapFrameDistance(radius, 45d, 0.5d);

            Assert.True(narrow > wide);
        }

        [Fact]
        public void MapFocusMirrorsXPreservesLookAndClampsDistanceToReferenceReach()
        {
            var pose = ViewerViewportControl.CalculateMapFocusPose(
                new Vector3(100f, 200f, 300f),
                new Vector3D(0d, 0d, -3000d));

            Assert.NotNull(pose);
            Assert.Equal(new Point3D(-100d, 200d, 300d), pose.Value.Target);
            Assert.Equal(new Vector3D(0d, 0d, -1500d), pose.Value.LookDirection);
            Assert.Equal(new Point3D(-100d, 200d, 1800d), pose.Value.Position);
        }

        [Fact]
        public void MapFocusKeepsCurrentDistanceWhenAlreadyInsideReferenceReach()
        {
            var pose = ViewerViewportControl.CalculateMapFocusPose(
                Vector3.Zero,
                new Vector3D(300d, -400d, 0d));

            Assert.NotNull(pose);
            Assert.Equal(500d, pose.Value.LookDirection.Length, 6);
        }

        [Fact]
        public void MapFocusMarkerGeometryUsesReferenceRadius()
        {
            float[] vertices = MapFocusMarkerRenderer.BuildWireSphere();

            Assert.NotEmpty(vertices);
            Assert.Equal(0, vertices.Length % 3);
            for (int index = 0; index < vertices.Length; index += 3)
            {
                float radius = new Vector3(vertices[index], vertices[index + 1], vertices[index + 2]).Length();
                Assert.InRange(radius, MapFocusMarkerRenderer.Radius - 0.001f, MapFocusMarkerRenderer.Radius + 0.001f);
            }
        }
    }
}
