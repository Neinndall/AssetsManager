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
        private void ViewportSnapshotButton_Click(object sender, RoutedEventArgs e)
        {
            _pendingSnapshot = OpenGlSnapshotService.RequestUhdSnapshot(
                _gl != null && _isActive && !_isCleanedUp && OpenTkControl.IsVisible,
                OpenTkControl.FrameBufferWidth, OpenTkControl.FrameBufferHeight,
                _model.SelectedWorkspaceTab?.Title ?? "3DStudio", LogService);
            if (_pendingSnapshot != null) OpenTkControl.InvalidateVisual();
        }

        private void ChancePinButton_Click(object sender, RoutedEventArgs e)
        {
            if (_model?.HasStandaloneSystem != true || ChancePinPopup == null ||
                Environment.TickCount64 - _chancePinClosedTicks < 250) return;
            SyncChancePinControls();
            CloseAllToolbarPopups(ChancePinPopup);
            ChancePinPopup.IsOpen = !ChancePinPopup.IsOpen;
        }

        private void ChancePinPopup_Closed(object sender, EventArgs e)
            => _chancePinClosedTicks = Environment.TickCount64;

        private void TimelineOptionsButton_Click(object sender, RoutedEventArgs e)
        {
            if (TimelineOptionsPopup == null || Environment.TickCount64 - _timelineOptionsClosedTicks < 250) return;
            CloseAllToolbarPopups(TimelineOptionsPopup);
            TimelineOptionsPopup.IsOpen = !TimelineOptionsPopup.IsOpen;
        }

        private void TimelineOptionsPopup_Closed(object sender, EventArgs e)
            => _timelineOptionsClosedTicks = Environment.TickCount64;

        private void BgMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_model == null) return;
            if (BgComboBox?.SelectedItem is ComboBoxItem item)
            {
                _model.BgMode = item.Tag?.ToString() ?? item.Content?.ToString() ?? "Dark";
            }
        }

        private void PreviewBindPoseToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton toggle) return;
            SetBindPosePreview(toggle.IsChecked == true);
            toggle.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateTarget();
        }

        private void CloseAllToolbarPopups(Popup exceptPopup = null)
        {
            if (PreviewShowPopup != null && PreviewShowPopup != exceptPopup && PreviewShowPopup.IsOpen)
                PreviewShowPopup.IsOpen = false;
            if (PreviewViewModePopup != null && PreviewViewModePopup != exceptPopup && PreviewViewModePopup.IsOpen)
                PreviewViewModePopup.IsOpen = false;
            if (PreviewCameraPopup != null && PreviewCameraPopup != exceptPopup && PreviewCameraPopup.IsOpen)
                PreviewCameraPopup.IsOpen = false;
            if (ChancePinPopup != null && ChancePinPopup != exceptPopup && ChancePinPopup.IsOpen)
                ChancePinPopup.IsOpen = false;
            if (TimelineOptionsPopup != null && TimelineOptionsPopup != exceptPopup && TimelineOptionsPopup.IsOpen)
                TimelineOptionsPopup.IsOpen = false;
            if (RigPresetButton?.ContextMenu != null && RigPresetButton.ContextMenu.IsOpen)
                RigPresetButton.ContextMenu.IsOpen = false;
        }

        private void PreviewShowPopup_Closed(object sender, EventArgs e)
        {
            _previewShowClosedTicks = Environment.TickCount64;
        }

        private void PreviewViewModePopup_Closed(object sender, EventArgs e)
        {
            _previewViewModeClosedTicks = Environment.TickCount64;
        }

        private void PreviewCameraPopup_Closed(object sender, EventArgs e)
        {
            _previewCameraClosedTicks = Environment.TickCount64;
        }

        private void RigPresetContextMenu_Closed(object sender, RoutedEventArgs e)
        {
            _rigMenuClosedTicks = Environment.TickCount64;
        }

        private void PreviewShow_Click(object sender, RoutedEventArgs e)
        {
            if (PreviewShowPopup == null) return;
            if (Environment.TickCount64 - _previewShowClosedTicks < 250)
            {
                // The popup was just closed by clicking on this trigger button: keep it closed
                return;
            }

            CloseAllToolbarPopups(PreviewShowPopup);
            PreviewShowPopup.IsOpen = true;
        }

        private void PreviewViewMode_Click(object sender, RoutedEventArgs e)
        {
            if (PreviewViewModePopup == null) return;
            if (Environment.TickCount64 - _previewViewModeClosedTicks < 250)
            {
                return;
            }

            CloseAllToolbarPopups(PreviewViewModePopup);
            PreviewViewModePopup.IsOpen = true;
        }

        private void PreviewCamera_Click(object sender, RoutedEventArgs e)
        {
            if (PreviewCameraPopup == null) return;
            if (Environment.TickCount64 - _previewCameraClosedTicks < 250)
            {
                return;
            }

            CloseAllToolbarPopups(PreviewCameraPopup);
            PreviewCameraPopup.IsOpen = true;
        }

        private void RigPreset_Click(object sender, RoutedEventArgs e)
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



        private void TimelineLoopToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingRigControls || sender is not ToggleButton toggleButton) return;
            SetPreviewLoopEnabled(toggleButton.IsChecked == true);
        }

        private void ResetCamera_Click(object sender, RoutedEventArgs e)
        {
            ResetCamera();
        }

        private void StepBack_Click(object sender, RoutedEventArgs e)
            => StepPlayback(-1);

        private void StepForward_Click(object sender, RoutedEventArgs e)
            => StepPlayback(1);

        private void PlayPauseToggle_Click(object sender, RoutedEventArgs e)
            => TogglePlayback();

        private void SetRigPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string tag } &&
                Enum.TryParse(tag, out VfxRigPreset preset))
                SetRigPreset(preset);
        }

        private void Speed_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_model == null) return;
            if (SpeedComboBox?.SelectedItem is ComboBoxItem item &&
                float.TryParse(item.Tag?.ToString(), System.Globalization.CultureInfo.InvariantCulture, out float speed))
            {
                SetPlaybackSpeed(speed, updateControl: false);
            }
        }

        private void ChancePinToggle_Click(object sender, RoutedEventArgs e)
        {
            ApplyPinnedBirthChance(ChancePinToggle.IsChecked == true
                ? (float)ChancePinSlider.Value : null);
        }

        private void ChancePinSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingChancePinControls || !IsLoaded || ChancePinToggle?.IsChecked != true ||
                _model?.HasStandaloneSystem != true || _vfxRenderer == null) return;
            ApplyPinnedBirthChance((float)e.NewValue);
        }
    }
}
