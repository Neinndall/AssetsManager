using System;
using System.Numerics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using AssetsManager.Services.Viewer.Interaction;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Interaction
{
    public sealed class ViewportModelInteractionControllerTests
    {
        [Fact]
        public void SwitchingActorsKeepsAxesRenderedAtTheirNewOrigin()
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    var root = new Grid { Width = 800, Height = 600 };
                    var input = new Border();
                    var canvas = new Canvas { Visibility = Visibility.Collapsed };
                    Line Axis(Brush brush) => new() { Stroke = brush, StrokeThickness = 4,
                        StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Triangle };
                    var x = Axis(Brushes.Red);
                    var y = Axis(Brushes.Lime);
                    var z = Axis(Brushes.Blue);
                    var marker = new Ellipse { Width = 10, Height = 10, Fill = Brushes.White };
                    canvas.Children.Add(x); canvas.Children.Add(y); canvas.Children.Add(z); canvas.Children.Add(marker);
                    root.Children.Add(input); root.Children.Add(canvas);
                    var camera = new PerspectiveCamera(new Point3D(0, 150, 800),
                        new Vector3D(0, -50, -800), new Vector3D(0, 1, 0), 45);
                    var view = Matrix4x4.CreateLookAt(new Vector3(0, 150, 800), new Vector3(0, 100, 0), Vector3.UnitY);
                    var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4, 4f / 3, 1, 10000);
                    using var first = new SceneModel { PositionX = -150 };
                    using var second = new SceneModel { PositionX = 150 };
                    using var controller = new ViewportModelInteractionController(input, canvas, x, y, z, marker,
                        () => camera, new[] { first, second });
                    root.Measure(new Size(800, 600)); root.Arrange(new Rect(0, 0, 800, 600));
                    bool firstFrame = true;
                    foreach (var actor in new[] { first, second, first, second })
                    {
                        controller.SetSelection(new[] { actor }, actor);
                        controller.Update(view * projection);
                        if (firstFrame) root.UpdateLayout();
                        firstFrame = false;
                        var bitmap = new RenderTargetBitmap(800, 600, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(root);
                        var pixels = new byte[800 * 600 * 4];
                        bitmap.CopyPixels(pixels, 800 * 4, 0);
                        Assert.True(ViewerInteractionService.TryProject(new Vector3((float)actor.PositionX, 0, 0),
                            view * projection, 800, 600, out Point expectedOrigin));
                        int red = 0, green = 0;
                        for (int i = 0; i < pixels.Length; i += 4)
                        {
                            int px = (i / 4) % 800;
                            int py = (i / 4) / 800;
                            if (Math.Abs(px - expectedOrigin.X) > 120 || Math.Abs(py - expectedOrigin.Y) > 120) continue;
                            if (pixels[i + 2] > 200 && pixels[i + 1] < 50) red++;
                            if (pixels[i + 1] > 200 && pixels[i + 2] < 50) green++;
                        }
                        Assert.True(red > 100, $"Missing red axis for actor X={actor.PositionX}: {red} pixels");
                        Assert.True(green > 100, $"Missing green axis for actor X={actor.PositionX}: {green} pixels");
                    }
                }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start(); thread.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
