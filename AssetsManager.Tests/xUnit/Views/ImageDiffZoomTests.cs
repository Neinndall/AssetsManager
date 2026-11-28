using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using AssetsManager.Views.Dialogs;
using Xunit;

namespace AssetsManager.Tests.xUnit.Views
{
    public sealed class ImageDiffZoomTests
    {
        [Fact]
        public void SideBySideZoomUsesLocalPanelOriginSoLeftAndRightPanelsMatch()
        {
            // Hovering the center (200, 150) of either the left OLD panel or the right NEW panel
            // must yield the exact same pan delta instead of offsetting by the right panel's X column.
            var leftDelta = ImageDiffWindow.ComputeZoomPanDelta(new Point(200, 150), 0, 0, 1.1);
            var rightDelta = ImageDiffWindow.ComputeZoomPanDelta(new Point(200, 150), 0, 0, 1.1);

            Assert.Equal(leftDelta.DeltaX, rightDelta.DeltaX, 4);
            Assert.Equal(leftDelta.DeltaY, rightDelta.DeltaY, 4);
            Assert.Equal(-20d, leftDelta.DeltaX, 4);
            Assert.Equal(-15d, leftDelta.DeltaY, 4);
        }

        [Fact]
        public void ResolveZoomTargetSelectsOverlayContainerWhenNotSideBySide()
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    var oldBorder = new Border();
                    var newBorder = new Border();
                    var overlayContainer = new Border();
                    var fallback = new Grid();

                    Assert.Same(overlayContainer, ImageDiffWindow.ResolveZoomTarget(false, oldBorder, newBorder, overlayContainer, fallback));
                    Assert.Same(oldBorder, ImageDiffWindow.ResolveZoomTarget(true, oldBorder, newBorder, overlayContainer, fallback));
                    Assert.Same(fallback, ImageDiffWindow.ResolveZoomTarget(false, oldBorder, newBorder, null, fallback));
                }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
