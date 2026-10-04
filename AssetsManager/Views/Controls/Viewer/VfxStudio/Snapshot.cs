using System.Windows;
using AssetsManager.Services.Viewer.Rendering;

namespace AssetsManager.Views.Controls.Viewer
{
    public partial class VfxInspectorControl
    {
        private readonly OpenGlSnapshotService _snapshotService = new();
        private OpenGlSnapshotService.SnapshotRequest _pendingSnapshot;

        private void ViewportSnapshotButton_Click(object sender, RoutedEventArgs e)
        {
            _pendingSnapshot = OpenGlSnapshotService.RequestUhdSnapshot(
                _gl != null && _isActive && !_isCleanedUp && OpenTkControl.IsVisible,
                OpenTkControl.FrameBufferWidth, OpenTkControl.FrameBufferHeight,
                _model.SelectedWorkspaceTab?.Title ?? "VFXStudio", LogService);
            if (_pendingSnapshot != null) OpenTkControl.InvalidateVisual();
        }
    }
}
