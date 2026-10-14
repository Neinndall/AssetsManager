using System;
using System.Windows;
using System.Windows.Controls;
using AssetsManager.Services.Viewer.Vfx.Session;

namespace AssetsManager.Views.Controls.Viewer
{
    public partial class StudioControl
    {
        internal void InspectorRigPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingRigControls || _model?.HasStandaloneSystem != true ||
                sender is not ComboBox { SelectedItem: ComboBoxItem item } ||
                item.Tag is not string preset || preset == _model.RigPresetText)
                return;

            if (Enum.TryParse(preset, out VfxRigPreset rigPreset))
                SetRigPreset(rigPreset);
        }

        internal void RerollSeed_Click(object sender, RoutedEventArgs e)
        {
            if (_inspectedSystem == null) return;

            RememberStandaloneRun(_inspectedSystem);
            string key = StandaloneRunKey(_inspectedSystem);
            if (_standaloneRunMemory.TryGetValue(key, out StandaloneRunMemory memory))
                _standaloneRunMemory[key] = memory with { Seed = NextPlaybackSeed(memory.Seed) };

            InspectSystem(_inspectedSystem);
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
            if (StudioInspectorView.RigHeightSlider == null ||
                StudioInspectorView.RigDistancePanel == null ||
                StudioInspectorView.RigOrbitPanel == null)
            {
                return;
            }

            VfxRigSettings settings = _vfxRenderer?.RigSettings ?? VfxRigSettings.ForPreset(_model.RigPreset);
            try
            {
                _isUpdatingRigControls = true;
                StudioInspectorView.RigHeightSlider.Value = settings.Height;
                StudioInspectorView.RigHeightValueText.Text = $"{Math.Round(settings.Height)} u";

                // Show only the tuning controls used by the selected motion preset.
                StudioInspectorView.RigDistancePanel.Visibility = settings.MotionKind == VfxRigMotionKind.Path
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                StudioInspectorView.RigDistanceSlider.Value = settings.FlightRange;
                StudioInspectorView.RigDistanceValueText.Text = $"{Math.Round(settings.FlightRange)} u";
                StudioInspectorView.RigSpeedSlider.Value = settings.FlightSpeed;
                StudioInspectorView.RigSpeedValueText.Text = $"{Math.Round(settings.FlightSpeed)} u/s";

                StudioInspectorView.RigOrbitPanel.Visibility = settings.MotionKind == VfxRigMotionKind.Orbit
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                StudioInspectorView.RigRadiusSlider.Value = settings.OrbitRadius;
                StudioInspectorView.RigRadiusValueText.Text = $"{Math.Round(settings.OrbitRadius)} u";
                StudioInspectorView.RigPeriodSlider.Value = settings.OrbitPeriod;
                StudioInspectorView.RigPeriodValueText.Text = $"{settings.OrbitPeriod:F2} s";

                // StudioTimelineView.TimelineLoopToggleButton binds directly to IsPreviewLoopEnabled so
                // Systems, Clips, and Spells share one loop state.
            }
            finally
            {
                _isUpdatingRigControls = false;
            }
        }

        internal void RigHeightSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingRigControls || _vfxRenderer == null) return;
            ApplyRigTuning(_vfxRenderer.RigSettings with { Height = (float)e.NewValue });
        }

        internal void RigDistanceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingRigControls || _vfxRenderer == null) return;
            ApplyRigTuning(_vfxRenderer.RigSettings with { FlightRange = (float)e.NewValue });
        }

        internal void RigSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingRigControls || _vfxRenderer == null) return;
            ApplyRigTuning(_vfxRenderer.RigSettings with { FlightSpeed = (float)e.NewValue });
        }

        internal void RigRadiusSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingRigControls || _vfxRenderer == null) return;
            ApplyRigTuning(_vfxRenderer.RigSettings with { OrbitRadius = (float)e.NewValue });
        }

        internal void RigPeriodSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingRigControls || _vfxRenderer == null) return;
            ApplyRigTuning(_vfxRenderer.RigSettings with { OrbitPeriod = (float)e.NewValue });
        }

        private void SetRigPreset(VfxRigPreset preset)
        {
            if (!_model.IsRawSystemsMode) return;
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
    }
}
