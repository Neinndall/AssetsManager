using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Media3D;
using AssetsManager.Views.Helpers;
using Xunit;

namespace AssetsManager.Tests.xUnit.Views.Helpers;

[Collection("Viewport native graphics")]
public sealed class CameraFrameInputTests
{
    [Fact]
    public void FrameConsumesAllRecentMouseInputOnceWithoutWaitingForComposition()
    {
        RunSta(() =>
        {
            var camera = Camera();
            using var controller = new CustomCameraController(new Viewport3D { Camera = camera }, new Grid());
            controller.QueueRotation(new Vector(180, 0));
            controller.QueueRotation(new Vector(180, 0));
            Assert.Equal(new Vector3D(0, 0, -500), camera.LookDirection);

            controller.ApplyPendingRotation();

            Assert.Equal(500, camera.LookDirection.X, 6);
            Assert.Equal(0, camera.LookDirection.Z, 6);
            Assert.Equal(new Point3D(0, 100, 500), camera.Position);
            Vector3D drawn = camera.LookDirection;
            controller.ApplyPendingRotation();
            Assert.Equal(drawn, camera.LookDirection);
        });
    }

    [Fact]
    public void SwitchingCameraDiscardsInputQueuedForThePreviousScene()
    {
        RunSta(() =>
        {
            using var controller = new CustomCameraController(new Viewport3D { Camera = Camera() }, new Grid());
            controller.QueueRotation(new Vector(360, 120));
            var camera = Camera();
            controller.SetCamera(camera);
            controller.ApplyPendingRotation();
            Assert.Equal(new Vector3D(0, 0, -500), camera.LookDirection);
            Assert.Equal(new Point3D(0, 100, 500), camera.Position);
        });
    }

    private static PerspectiveCamera Camera() =>
        new(new Point3D(0, 100, 500), new Vector3D(0, 0, -500), new Vector3D(0, 1, 0), 45);

    private static void RunSta(Action action)
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Camera input test timed out.");
        Assert.Null(failure);
    }
}
