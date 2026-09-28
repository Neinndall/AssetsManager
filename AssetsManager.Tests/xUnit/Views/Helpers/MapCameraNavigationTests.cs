using System.Windows;
using System.Windows.Media.Media3D;
using AssetsManager.Views.Helpers;
using Xunit;

namespace AssetsManager.Tests.xUnit.Views.Helpers
{
    /// <summary>
    /// Ground navigation math for MAP scenes: cursor rays match the OpenGL projection, zoom reaches
    /// any point without stalling and WASD travel stays on the ground.
    /// </summary>
    public class MapCameraNavigationTests
    {
        private const double Ground = 50.0;
        private static readonly Size Surface = new(1600, 900);

        [Fact]
        public void ScreenCentreRayFollowsTheLookDirection()
        {
            MapCameraPose pose = Looking(new Point3D(0, 1050, 1000), new Vector3D(0, -1000, -1000));

            Assert.True(MapCameraNavigation.TryGetGroundPoint(pose, Surface, new Point(800, 450), Ground, out Point3D hit));

            AssertPoint(new Point3D(0, Ground, 0), hit);
        }

        [Fact]
        public void TopEdgeRayUsesTheVerticalFieldOfViewLikeOpenGl()
        {
            // Straight down from 1000 above the ground with a 90° vertical FOV: the top edge meets
            // the ground 1000 units ahead of the screen centre along the screen up axis.
            var pose = new MapCameraPose(
                new Point3D(0, 1000 + Ground, 0),
                new Vector3D(0, -1, 0),
                new Vector3D(0, 0, -1),
                90,
                0,
                false);

            Assert.True(MapCameraNavigation.TryGetGroundPoint(pose, Surface, new Point(800, 0), Ground, out Point3D hit));

            AssertPoint(new Point3D(0, Ground, -1000), hit);
        }

        [Fact]
        public void RaysAboveTheHorizonDoNotHitTheGround()
        {
            MapCameraPose pose = Looking(new Point3D(0, 200, 0), new Vector3D(0, 0, -1));

            Assert.False(MapCameraNavigation.TryGetGroundPoint(pose, Surface, new Point(800, 0), Ground, out _));
        }

        [Fact]
        public void ZoomInApproachesThePointUnderTheCursor()
        {
            var position = new Point3D(0, 3000, 3000);
            var focus = new Point3D(5000, Ground, -2000);

            Point3D next = MapCameraNavigation.Zoom(position, focus, 1, 1.0, Ground, 50000);

            Assert.True((focus - next).Length < (focus - position).Length);
            Vector3D moved = next - position;
            Vector3D toFocus = focus - position;
            Assert.True(Vector3D.AngleBetween(moved, toFocus) < 0.01);
        }

        [Fact]
        public void RepeatedZoomInNeverStallsAndGlidesPastTheClosestApproach()
        {
            Point3D position = new(0, 400, 400);
            var focus = new Point3D(0, Ground, 0);
            for (int notch = 0; notch < 40; notch++)
                position = MapCameraNavigation.Zoom(position, focus, 1, 1.0, Ground, 50000);

            Assert.True(position.Y >= Ground + MapCameraNavigation.MinimumHeight - 1e-9);
            Point3D before = position;
            Point3D after = MapCameraNavigation.Zoom(before, focus, 1, 1.0, Ground, 50000);
            Assert.True((after - before).Length >= MapCameraNavigation.MinimumZoomStep - 1e-6);
        }

        [Fact]
        public void ZoomOutStopsAtTheMaximumDistance()
        {
            var focus = new Point3D(0, Ground, 0);
            Point3D position = new(0, 1000, 1000);
            for (int notch = 0; notch < 200; notch++)
                position = MapCameraNavigation.Zoom(position, focus, -1, 5.0, Ground, 20000);

            Assert.InRange((position - focus).Length, 19999.0, 20000.0 + 1e-6);
        }

        [Fact]
        public void ShiftAndCtrlScaleTheZoomStep()
        {
            var position = new Point3D(0, 3000, 3000);
            var focus = new Point3D(0, Ground, 0);

            double normal = (MapCameraNavigation.Zoom(position, focus, 1, 1.0, Ground, 50000) - position).Length;
            double turbo = (MapCameraNavigation.Zoom(position, focus, 1, 5.0, Ground, 50000) - position).Length;
            double precise = (MapCameraNavigation.Zoom(position, focus, 1, 0.2, Ground, 50000) - position).Length;

            Assert.Equal(normal * 5.0, turbo, 6);
            Assert.Equal(normal * 0.2, precise, 6);
        }

        [Fact]
        public void WalkMovesOverTheGroundInTheViewDirection()
        {
            MapCameraPose pose = Looking(new Point3D(0, 1050, 1000), new Vector3D(0, -1000, -1000));

            Vector3D forward = MapCameraNavigation.Walk(pose, 1, 0, 0.5, 1.0, Ground);
            Vector3D right = MapCameraNavigation.Walk(pose, 0, 1, 0.5, 1.0, Ground);
            Vector3D fast = MapCameraNavigation.Walk(pose, 1, 0, 0.5, 3.0, Ground);

            Assert.Equal(0, forward.Y, 9);
            Assert.True(forward.Z < 0 && System.Math.Abs(forward.X) < 1e-9);
            Assert.True(right.X > 0 && System.Math.Abs(right.Z) < 1e-9);
            Assert.Equal(forward.Length * 3.0, fast.Length, 6);
            Assert.Equal(default, MapCameraNavigation.Walk(pose, 0, 0, 0.5, 1.0, Ground));
        }

        [Fact]
        public void TopDownWalkUsesScreenUpAsForward()
        {
            var pose = new MapCameraPose(
                new Point3D(0, 5000, 0),
                new Vector3D(0, -1, 0),
                new Vector3D(0, 0, -1),
                45,
                0,
                false);

            Vector3D forward = MapCameraNavigation.Walk(pose, 1, 0, 1.0, 1.0, Ground);

            Assert.True(forward.Z < 0);
            Assert.Equal(0, forward.Y, 9);
        }

        [Fact]
        public void GroundedLookEndsOnTheGroundAlongTheSameDirection()
        {
            var position = new Point3D(0, 1050, 1000);
            Vector3D look = MapCameraNavigation.GroundedLook(position, new Vector3D(0, -1, -1), Ground);

            AssertPoint(new Point3D(0, Ground, 0), position + look);
            Vector3D horizon = new(0, 0, -250);
            Assert.Equal(horizon, MapCameraNavigation.GroundedLook(position, horizon, Ground));
        }

        [Fact]
        public void OrthographicZoomKeepsTheCursorPointFixed()
        {
            var pose = new MapCameraPose(
                new Point3D(0, 5000, 0),
                new Vector3D(0, -1, 0),
                new Vector3D(0, 0, -1),
                45,
                4000,
                true);
            var cursor = new Point(1200, 200);
            Assert.True(MapCameraNavigation.TryGetGroundPoint(pose, Surface, cursor, Ground, out Point3D before));
            Assert.True(MapCameraNavigation.TryGetRay(pose, Surface, cursor, out Point3D anchor, out _));

            Point3D position = MapCameraNavigation.OrthographicZoomPosition(pose.Position, anchor, 0.5);
            MapCameraPose zoomed = pose with { Position = position, OrthographicWidth = 2000 };

            Assert.True(MapCameraNavigation.TryGetGroundPoint(zoomed, Surface, cursor, Ground, out Point3D after));
            AssertPoint(before, after);
        }

        private static MapCameraPose Looking(Point3D position, Vector3D look) =>
            new(position, look, new Vector3D(0, 1, 0), 45, 0, false);

        private static void AssertPoint(Point3D expected, Point3D actual)
        {
            Assert.Equal(expected.X, actual.X, 6);
            Assert.Equal(expected.Y, actual.Y, 6);
            Assert.Equal(expected.Z, actual.Z, 6);
        }
    }
}
