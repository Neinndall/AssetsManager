using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AssetsManager.Services.Viewer.Vfx.Session;

namespace AssetsManager.Views.Controls.Viewer
{
    public partial class VfxInspectorControl
    {
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



        private void RerollSeed_Click(object sender, RoutedEventArgs e)
        {
            if (_inspectedSystem == null) return;

            RememberStandaloneRun(_inspectedSystem);
            string key = StandaloneRunKey(_inspectedSystem);
            if (_standaloneRunMemory.TryGetValue(key, out StandaloneRunMemory memory))
                _standaloneRunMemory[key] = memory with { Seed = NextPlaybackSeed(memory.Seed) };

            InspectSystem(_inspectedSystem);
        }

        private void SetRigPreset_Click(object sender, RoutedEventArgs e)
        {
            if (!_model.IsRawSystemsMode) return;
            if (sender is not FrameworkElement { Tag: string tagStr } ||
                !Enum.TryParse(tagStr, out VfxRigPreset preset))
            {
                return;
            }

            VfxRigSettings current = _vfxRenderer?.RigSettings ?? VfxRigSettings.ForPreset(_model.RigPreset);
            VfxRigSettings settings = current.WithPreset(preset) with
            {
                IsLooping = _model.IsPreviewLoopEnabled
            };
            _model.RigPreset = preset;
            if (_vfxRenderer != null)
            {
                _vfxRenderer.RigSettings = settings;
                double duration = ResolveTimelineDuration(_vfxRenderer.RigDuration);
                UpdatePreviewLoopRangeForDuration(duration);
                _model.CurrentTime = _vfxRenderer.PlaybackTime;
                _vfxRenderer.Play();
                _model.IsPlaying = true;
            }
            UpdateRigControlValues();
        }

        private void ApplyRigTuning(VfxRigSettings settings)
        {
            if (_isUpdatingRigControls || _vfxRenderer == null || !_model.IsRawSystemsMode) return;

            _vfxRenderer.RigSettings = settings;
            _model.RigPreset = settings.Preset;

            double duration = ResolveTimelineDuration(_vfxRenderer.RigDuration);
            UpdatePreviewLoopRangeForDuration(duration);

            _model.CurrentTime = _vfxRenderer.PlaybackTime;
            UpdateRigControlValues();
            UpdateTimelineTrackMetrics();
            UpdatePlayheadPosition();
        }

        private void UpdateRigControlValues()
        {
            if (RigHeightSlider == null ||
                RigDistancePanel == null ||
                RigOrbitPanel == null)
            {
                return;
            }

            VfxRigSettings settings = _vfxRenderer?.RigSettings ?? VfxRigSettings.ForPreset(_model.RigPreset);
            try
            {
                _isUpdatingRigControls = true;
                RigHeightSlider.Value = settings.Height;
                RigHeightValueText.Text = $"{Math.Round(settings.Height)} u";

                // Show only the tuning controls used by the selected motion preset.
                RigDistancePanel.Visibility = settings.MotionKind == VfxRigMotionKind.Path
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                RigDistanceSlider.Value = settings.FlightRange;
                RigDistanceValueText.Text = $"{Math.Round(settings.FlightRange)} u";
                RigSpeedSlider.Value = settings.FlightSpeed;
                RigSpeedValueText.Text = $"{Math.Round(settings.FlightSpeed)} u/s";

                RigOrbitPanel.Visibility = settings.MotionKind == VfxRigMotionKind.Orbit
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                RigRadiusSlider.Value = settings.OrbitRadius;
                RigRadiusValueText.Text = $"{Math.Round(settings.OrbitRadius)} u";
                RigPeriodSlider.Value = settings.OrbitPeriod;
                RigPeriodValueText.Text = $"{settings.OrbitPeriod:F2} s";

                // TimelineLoopToggleButton binds directly to IsPreviewLoopEnabled so
                // Systems, Clips, and Spells share one loop state.
            }
            finally
            {
                _isUpdatingRigControls = false;
            }
        }

        private void RigHeightSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingRigControls || _vfxRenderer == null) return;
            ApplyRigTuning(_vfxRenderer.RigSettings with { Height = (float)e.NewValue });
        }

        private void RigDistanceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingRigControls || _vfxRenderer == null) return;
            ApplyRigTuning(_vfxRenderer.RigSettings with { FlightRange = (float)e.NewValue });
        }

        private void RigSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingRigControls || _vfxRenderer == null) return;
            ApplyRigTuning(_vfxRenderer.RigSettings with { FlightSpeed = (float)e.NewValue });
        }

        private void RigRadiusSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingRigControls || _vfxRenderer == null) return;
            ApplyRigTuning(_vfxRenderer.RigSettings with { OrbitRadius = (float)e.NewValue });
        }

        private void RigPeriodSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingRigControls || _vfxRenderer == null) return;
            ApplyRigTuning(_vfxRenderer.RigSettings with { OrbitPeriod = (float)e.NewValue });
        }

        private void TimelineLoopToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingRigControls || sender is not ToggleButton toggleButton) return;
            SetPreviewLoopEnabled(toggleButton.IsChecked == true);
        }

        private void SetPreviewLoopEnabled(bool enabled)
        {
            if (enabled)
            {
                double span = ResolveTimelineDuration(_model.TotalDuration);
                _model.ActiveLoopStart = 0d;
                _model.ActiveLoopDuration = span;
            }

            _model.IsPreviewLoopEnabled = enabled;

            // Standalone Systems also use the rig lifecycle loop. Clips and Spells are
            // replayed by the common timeline loop and do not need a separate engine path.
            if (_model.IsRawSystemsMode && _vfxRenderer != null &&
                _vfxRenderer.RigSettings.IsLooping != enabled)
            {
                _vfxRenderer.RigSettings = _vfxRenderer.RigSettings with { IsLooping = enabled };
            }

            UpdateTimelineTrackMetrics();
            UpdatePlayheadPosition();
        }

        private void ResetPreviewLoopRange(double duration)
        {
            double span = ResolveTimelineDuration(duration);
            _model.TotalDuration = span;
            _model.ActiveLoopStart = 0d;
            _model.ActiveLoopDuration = span;
        }

        private void UpdatePreviewLoopRangeForDuration(double duration)
        {
            double span = ResolveTimelineDuration(duration);
            _model.TotalDuration = span;
            if (!_model.IsPreviewLoopEnabled)
            {
                _model.ActiveLoopStart = 0d;
                _model.ActiveLoopDuration = span;
                return;
            }

            (double from, double to) = ClampPreviewLoop(
                _model.ActiveLoopStart,
                _model.ActiveLoopDuration > 0d ? _model.ActiveLoopDuration : span,
                span);
            _model.ActiveLoopStart = from;
            _model.ActiveLoopDuration = to;
        }
    }
}
