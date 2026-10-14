using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Services.Viewer.Vfx.Session;

namespace AssetsManager.Views.Controls.Viewer
{
    public partial class StudioControl
    {
        internal void ViewportSnapshotButton_Click(object sender, RoutedEventArgs e)
        {
            _pendingSnapshot = OpenGlSnapshotService.RequestUhdSnapshot(
                _gl != null && _isActive && !_isCleanedUp && StudioViewportView.OpenTkControl.IsVisible,
                StudioViewportView.OpenTkControl.FrameBufferWidth, StudioViewportView.OpenTkControl.FrameBufferHeight,
                _model.SelectedWorkspaceTab?.Title ?? "3DStudio", LogService);
            if (_pendingSnapshot != null) StudioViewportView.OpenTkControl.InvalidateVisual();
        }

        internal void ChancePinButton_Click(object sender, RoutedEventArgs e)
        {
            if (_model?.HasStandaloneSystem != true || StudioTimelineView.ChancePinPopup == null ||
                Environment.TickCount64 - _chancePinClosedTicks < 250) return;
            SyncChancePinControls();
            CloseAllToolbarPopups(StudioTimelineView.ChancePinPopup);
            StudioTimelineView.ChancePinPopup.IsOpen = !StudioTimelineView.ChancePinPopup.IsOpen;
        }

        internal void ChancePinPopup_Closed(object sender, EventArgs e)
            => _chancePinClosedTicks = Environment.TickCount64;

        internal void TimelineOptionsButton_Click(object sender, RoutedEventArgs e)
        {
            if (StudioTimelineView.TimelineOptionsPopup == null || Environment.TickCount64 - _timelineOptionsClosedTicks < 250) return;
            CloseAllToolbarPopups(StudioTimelineView.TimelineOptionsPopup);
            StudioTimelineView.TimelineOptionsPopup.IsOpen = !StudioTimelineView.TimelineOptionsPopup.IsOpen;
        }

        internal void TimelineOptionsPopup_Closed(object sender, EventArgs e)
            => _timelineOptionsClosedTicks = Environment.TickCount64;

        internal void BgMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_model == null) return;
            if (StudioViewportView.BgComboBox?.SelectedItem is ComboBoxItem item)
            {
                _model.BgMode = item.Tag?.ToString() ?? item.Content?.ToString() ?? "Dark";
            }
        }

        internal void PreviewBindPoseToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton toggle) return;
            SetBindPosePreview(toggle.IsChecked == true);
            toggle.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateTarget();
        }

        private void CloseAllToolbarPopups(Popup exceptPopup = null)
        {
            if (StudioViewportView.PreviewShowPopup != null && StudioViewportView.PreviewShowPopup != exceptPopup && StudioViewportView.PreviewShowPopup.IsOpen)
                StudioViewportView.PreviewShowPopup.IsOpen = false;
            if (StudioViewportView.PreviewViewModePopup != null && StudioViewportView.PreviewViewModePopup != exceptPopup && StudioViewportView.PreviewViewModePopup.IsOpen)
                StudioViewportView.PreviewViewModePopup.IsOpen = false;
            if (StudioViewportView.PreviewCameraPopup != null && StudioViewportView.PreviewCameraPopup != exceptPopup && StudioViewportView.PreviewCameraPopup.IsOpen)
                StudioViewportView.PreviewCameraPopup.IsOpen = false;
            if (StudioTimelineView.ChancePinPopup != null && StudioTimelineView.ChancePinPopup != exceptPopup && StudioTimelineView.ChancePinPopup.IsOpen)
                StudioTimelineView.ChancePinPopup.IsOpen = false;
            if (StudioTimelineView.TimelineOptionsPopup != null && StudioTimelineView.TimelineOptionsPopup != exceptPopup && StudioTimelineView.TimelineOptionsPopup.IsOpen)
                StudioTimelineView.TimelineOptionsPopup.IsOpen = false;
            if (StudioViewportView.RigPresetButton?.ContextMenu != null && StudioViewportView.RigPresetButton.ContextMenu.IsOpen)
                StudioViewportView.RigPresetButton.ContextMenu.IsOpen = false;
        }

        internal void PreviewShowPopup_Closed(object sender, EventArgs e)
        {
            _previewShowClosedTicks = Environment.TickCount64;
        }

        internal void PreviewViewModePopup_Closed(object sender, EventArgs e)
        {
            _previewViewModeClosedTicks = Environment.TickCount64;
        }

        internal void PreviewCameraPopup_Closed(object sender, EventArgs e)
        {
            _previewCameraClosedTicks = Environment.TickCount64;
        }

        internal void RigPresetContextMenu_Closed(object sender, RoutedEventArgs e)
        {
            _rigMenuClosedTicks = Environment.TickCount64;
        }

        internal void PreviewShow_Click(object sender, RoutedEventArgs e)
        {
            if (StudioViewportView.PreviewShowPopup == null) return;
            if (Environment.TickCount64 - _previewShowClosedTicks < 250)
            {
                // The popup was just closed by clicking on this trigger button: keep it closed
                return;
            }

            CloseAllToolbarPopups(StudioViewportView.PreviewShowPopup);
            StudioViewportView.PreviewShowPopup.IsOpen = true;
        }

        internal void PreviewViewMode_Click(object sender, RoutedEventArgs e)
        {
            if (StudioViewportView.PreviewViewModePopup == null) return;
            if (Environment.TickCount64 - _previewViewModeClosedTicks < 250)
            {
                return;
            }

            CloseAllToolbarPopups(StudioViewportView.PreviewViewModePopup);
            StudioViewportView.PreviewViewModePopup.IsOpen = true;
        }

        internal void PreviewCamera_Click(object sender, RoutedEventArgs e)
        {
            if (StudioViewportView.PreviewCameraPopup == null) return;
            if (Environment.TickCount64 - _previewCameraClosedTicks < 250)
            {
                return;
            }

            CloseAllToolbarPopups(StudioViewportView.PreviewCameraPopup);
            StudioViewportView.PreviewCameraPopup.IsOpen = true;
        }

        internal void RigPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.ContextMenu != null)
            {
                if (Environment.TickCount64 - _rigMenuClosedTicks < 250)
                {
                    return;
                }

                CloseAllToolbarPopups();
                UpdateRigControlValues();
                btn.ContextMenu.PlacementTarget = btn;
                btn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
                btn.ContextMenu.IsOpen = true;
            }
        }



        internal void TimelineLoopToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingRigControls || sender is not ToggleButton toggleButton) return;
            SetPreviewLoopEnabled(toggleButton.IsChecked == true);
        }

        internal void ResetCamera_Click(object sender, RoutedEventArgs e)
        {
            ResetCamera();
        }

        internal void StepBack_Click(object sender, RoutedEventArgs e)
            => StepPlayback(-1);

        internal void StepForward_Click(object sender, RoutedEventArgs e)
            => StepPlayback(1);

        internal void PlayPauseToggle_Click(object sender, RoutedEventArgs e)
            => TogglePlayback();

        internal void SetRigPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string tag } &&
                Enum.TryParse(tag, out VfxRigPreset preset))
                SetRigPreset(preset);
        }

        internal void Speed_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_model == null) return;
            if (StudioTimelineView.SpeedComboBox?.SelectedItem is ComboBoxItem item &&
                float.TryParse(item.Tag?.ToString(), System.Globalization.CultureInfo.InvariantCulture, out float speed))
            {
                SetPlaybackSpeed(speed, updateControl: false);
            }
        }

        internal void ChancePinToggle_Click(object sender, RoutedEventArgs e)
        {
            ApplyPinnedBirthChance(StudioTimelineView.ChancePinToggle.IsChecked == true
                ? (float)StudioTimelineView.ChancePinSlider.Value : null);
        }

        internal void ChancePinSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingChancePinControls || !IsLoaded || StudioTimelineView.ChancePinToggle?.IsChecked != true ||
                _model?.HasStandaloneSystem != true || _vfxRenderer == null) return;
            ApplyPinnedBirthChance((float)e.NewValue);
        }
    }
}
