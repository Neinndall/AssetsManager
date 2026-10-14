using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace AssetsManager.Views.Controls.Viewer
{
    public partial class StudioViewportControl : UserControl
    {
        internal StudioControl Owner { get; set; }

        public StudioViewportControl() => InitializeComponent();

        private void BgMode_SelectionChanged(object sender, SelectionChangedEventArgs e) => Owner?.BgMode_SelectionChanged(sender, e);
        private void OpenTkControl_Ready() => Owner?.OpenTkControl_Ready();
        private void OpenTkControl_Render(TimeSpan delta) => Owner?.OpenTkControl_Render(delta);
        private void PreviewBindPoseToggle_Click(object sender, RoutedEventArgs e) => Owner?.PreviewBindPoseToggle_Click(sender, e);
        private void PreviewCameraPopup_Closed(object sender, EventArgs e) => Owner?.PreviewCameraPopup_Closed(sender, e);
        private void PreviewCamera_Click(object sender, RoutedEventArgs e) => Owner?.PreviewCamera_Click(sender, e);
        private void PreviewShowPopup_Closed(object sender, EventArgs e) => Owner?.PreviewShowPopup_Closed(sender, e);
        private void PreviewShow_Click(object sender, RoutedEventArgs e) => Owner?.PreviewShow_Click(sender, e);
        private void PreviewViewModePopup_Closed(object sender, EventArgs e) => Owner?.PreviewViewModePopup_Closed(sender, e);
        private void PreviewViewMode_Click(object sender, RoutedEventArgs e) => Owner?.PreviewViewMode_Click(sender, e);
        private void ResetCamera_Click(object sender, RoutedEventArgs e) => Owner?.ResetCamera_Click(sender, e);
        private void RigPresetContextMenu_Closed(object sender, RoutedEventArgs e) => Owner?.RigPresetContextMenu_Closed(sender, e);
        private void RigPreset_Click(object sender, RoutedEventArgs e) => Owner?.RigPreset_Click(sender, e);
        private void SetRigPreset_Click(object sender, RoutedEventArgs e) => Owner?.SetRigPreset_Click(sender, e);
        private void ViewportClipGrid_SizeChanged(object sender, SizeChangedEventArgs e) => Owner?.ViewportClipGrid_SizeChanged(sender, e);
        private void ViewportSnapshotButton_Click(object sender, RoutedEventArgs e) => Owner?.ViewportSnapshotButton_Click(sender, e);
    }
}
