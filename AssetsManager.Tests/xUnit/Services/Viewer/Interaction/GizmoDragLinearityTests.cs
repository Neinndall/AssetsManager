using System;
using System.Numerics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Media3D;
using AssetsManager.Services.Viewer.Interaction;
using AssetsManager.Views.Controls.Shared;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Interaction
{
    public sealed class GizmoDragLinearityTests
    {
        [Fact]
        public void DraggingAxisAcrossRenderUpdatesRemainsLinearWithoutFeedback()
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    var root = new Grid { Width = 800, Height = 600 };
                    var input = new Border();
                    var canvas = new ViewportTransformGizmo();
                    root.Children.Add(input);
                    root.Children.Add(canvas);

                    var camera = new PerspectiveCamera(
                        new Point3D(450, 220, 450),
                        new Vector3D(-450, -220, -450),
                        new Vector3D(0, 1, 0),
                        45);
                    var view = Matrix4x4.CreateLookAt(new Vector3(450, 220, 450), Vector3.Zero, Vector3.UnitY);
                    var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4, 4f / 3, 1, 10000);
                    Matrix4x4 viewProjection = view * projection;

                    using var model = new SceneModel();
                    using var controller = new ViewportModelInteractionController(
                        input,
                        canvas,
                        () => camera,
                        new[] { model });

                    root.Measure(new Size(800, 600));
                    root.Arrange(new Rect(0, 0, 800, 600));
                    root.UpdateLayout();

                    controller.SetSelection(new[] { model }, model);
                    controller.Update(viewProjection);

                    Assert.True(ViewerInteractionService.TryCalculateGizmoAxisLength(
                        Vector3.Zero, viewProjection, 600, out double initialAxisLength));
                    Assert.True(ViewerInteractionService.TryProject(
                        Vector3.Zero, viewProjection, 800, 600, out Point originScreen));
                    Assert.True(ViewerInteractionService.TryProject(
                        Vector3.UnitZ * (float)initialAxisLength, viewProjection, 800, 600, out Point zTipScreen));

                    // Grab halfway along the Z axis and drag away from the camera across 20 render frames.
                    Point grabPoint = new((originScreen.X + zTipScreen.X) * 0.5, (originScreen.Y + zTipScreen.Y) * 0.5);
                    Assert.True(controller.TryBeginAxisDrag(grabPoint));

                    System.Windows.Vector zDir = zTipScreen - originScreen;
                    zDir.Normalize();
                    Point dragTarget = grabPoint - zDir * 120;

                    Assert.True(controller.ApplyDragAtScreenPoint(dragTarget));
                    double firstFrameZ = model.PositionZ;
                    Assert.True(Math.Abs(firstFrameZ) > 10, $"Expected meaningful Z translation, got {firstFrameZ}.");

                    for (int frame = 0; frame < 20; frame++)
                    {
                        controller.Update(viewProjection);
                        Assert.True(controller.ApplyDragAtScreenPoint(dragTarget));
                        Assert.Equal(firstFrameZ, model.PositionZ, 4);
                    }

                    // Doubling the screen drag distance must double the world translation linearly.
                    Point doubleDragTarget = grabPoint - zDir * 240;
                    Assert.True(controller.ApplyDragAtScreenPoint(doubleDragTarget));
                    Assert.Equal(firstFrameZ * 2.0, model.PositionZ, 3);
                }
                catch (Exception ex) { failure = ex; }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Gizmo drag test timed out.");
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
