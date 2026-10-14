using System;
using System.Numerics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using AssetsManager.Views.Controls.Viewer;
using AssetsManager.Views.Controls.Shared;
using AssetsManager.Services.Viewer.Interaction;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Interaction
{
    public sealed class ViewportModelInteractionControllerTests
    {
        [Fact]
        public void NativeExtendedActorListKeepsGroupSelectionAcrossFocusChanges()
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    var first = new StudioSceneActor(new StudioSkinItem { BinPath = @"C:\p\first.bin" });
                    var second = new StudioSceneActor(new StudioSkinItem { BinPath = @"C:\p\second.bin" });
                    var tab = new StudioWorkspaceTab { Kind = StudioWorkspaceTabKind.Skin };
                    tab.Actors.Add(first); tab.Actors.Add(second); tab.FocusedActor = first;
                    var list = new ListBox
                    {
                        ItemsSource = tab.Actors, SelectionMode = SelectionMode.Extended
                    };
                    list.SelectionChanged += (_, _) =>
                    {
                        foreach (StudioSceneActor actor in tab.Actors)
                            actor.IsSelected = list.SelectedItems.Contains(actor);
                    };
                    list.Measure(new Size(300, 200)); list.Arrange(new Rect(0, 0, 300, 200)); list.UpdateLayout();
                    list.SelectedItems.Add(first);
                    list.SelectedItems.Add(second);
                    Assert.True(first.IsSelected);
                    Assert.True(second.IsSelected);

                    tab.FocusedActor = second;
                    Assert.Equal(2, list.SelectedItems.Count);
                    list.SelectedItems.Remove(first);
                    Assert.False(first.IsSelected);
                    Assert.True(second.IsSelected);

                    list.SelectedItems.Add(first);
                    Assert.True(list.SelectedItems.Contains(first));
                    Assert.Equal(2, list.SelectedItems.Count);
                }
                catch (Exception ex) { failure = ex; }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Actor selection test timed out.");
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

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
                    var canvas = new ViewportTransformGizmo();
                    root.Children.Add(input); root.Children.Add(canvas);
                    var camera = new PerspectiveCamera(new Point3D(0, 150, 800),
                        new Vector3D(0, -50, -800), new Vector3D(0, 1, 0), 45);
                    var view = Matrix4x4.CreateLookAt(new Vector3(0, 150, 800), new Vector3(0, 100, 0), Vector3.UnitY);
                    var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4, 4f / 3, 1, 10000);
                    using var first = new SceneModel { PositionX = -150 };
                    using var second = new SceneModel { PositionX = 150 };
                    using var controller = new ViewportModelInteractionController(input, canvas,
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
                            if (pixels[i + 2] > 200 && pixels[i + 1] < 150) red++;
                            if (pixels[i + 1] > 200 && pixels[i + 2] < 100) green++;
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
