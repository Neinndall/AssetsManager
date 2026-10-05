using System;
using System.Windows;
using System.Windows.Controls;
using AssetsManager.Services.Viewer.Vfx.Session;

namespace AssetsManager.Views.Controls.Viewer
{
    public partial class StudioControl
    {
        private void InspectorRigPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingRigControls || _model?.HasStandaloneSystem != true ||
                sender is not ComboBox { SelectedItem: ComboBoxItem item } ||
                item.Tag is not string preset || preset == _model.RigPresetText)
                return;

            if (Enum.TryParse(preset, out VfxRigPreset rigPreset))
                SetRigPreset(rigPreset);
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
