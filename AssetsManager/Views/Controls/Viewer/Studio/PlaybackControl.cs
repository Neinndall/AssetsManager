using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Services.Viewer.Vfx.Session;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Views.Controls.Viewer
{
    public partial class StudioControl
    {
        private bool HasSelectedSystemReady()
            => _model.SelectedSystem != null &&
               _pendingSystem == null &&
               ReferenceEquals(_inspectedSystem, _model.SelectedSystem) &&
               _vfxRenderer?.ActiveSystem != null;

        private bool HasSelectedSpellReady()
            => _model.SelectedSpell != null &&
               _activeSpellPlan?.Availability == VfxSpellAvailability.Supported &&
               _vfxRenderer?.ActiveSystem != null;

        private bool HasSelectedAnimationReady()
            => _model.IsAnimationMode &&
               _model.SelectedAnimation != null &&
               SameAnimationClip(_activeAnimationClip, _model.SelectedAnimation) &&
               _activeAnimationClip?.AnimationAsset != null &&
               _vfxRenderer?.ActiveSystem != null;

        private bool TryPlaySelectedTimedPreview(bool restartWhenPlaying)
        {
            bool ended = _model.CurrentTime >= _model.TotalDuration;
            bool restart = ended || (restartWhenPlaying && _model.IsPlaying);
            if (_model.SelectedMapNode?.Kind == MapBrowserNodeKind.Clip &&
                _model.SelectedMapNode.Payload is MapCharacterClipSelection mapClip)
            {
                if (!HasSelectedMapClipReady())
                    _ = PlayMapCharacterClipAsync(mapClip);
                else
                {
                    if (restart)
                        SeekTimeline(0d);
                    _model.IsPlaying = true;
                    _vfxRenderer?.Play();
                }
                return true;
            }

            if (_model.SelectedSpell != null)
            {
                if (!HasSelectedSpellReady())
                    RequestSpellPreview(_model.SelectedSpell);
                else
                    ResumeTimedPreview(restart);
                return true;
            }

            if (_model.IsAnimationMode && _model.SelectedAnimation != null)
            {
                if (_model.SelectedAnimation.IsBindPose)
                    return false;
                if (!HasSelectedAnimationReady())
                    _ = PlaySelectedAnimationAsync(_model.SelectedAnimation);
                else
                    ResumeTimedPreview(restart);
                return true;
            }

            return false;
        }

        private void ResumeTimedPreview(bool restartFromBeginning)
        {
            if (restartFromBeginning)
                SeekTimeline(0d);
            _model.IsPlaying = true;
            _vfxRenderer?.Play();
        }

        private bool _isUserSeeking;

        internal void TimeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_model.IsPlaying || _isUserSeeking)
                SeekTimeline(e.NewValue);
        }

        internal void TimeSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _isUserSeeking = true;
        }

        internal void TimeSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            _isUserSeeking = false;
        }

        private void SyncChancePinControls()
        {
            if (StudioTimelineView.ChancePinToggle == null || StudioTimelineView.ChancePinSlider == null) return;
            _isUpdatingChancePinControls = true;
            try
            {
                float? chance = _vfxRenderer?.PinnedBirthChance;
                StudioTimelineView.ChancePinToggle.IsChecked = chance.HasValue;
                if (chance.HasValue) StudioTimelineView.ChancePinSlider.Value = chance.Value;
                StudioTimelineView.ChancePinValueText.Text = StudioTimelineView.ChancePinSlider.Value.ToString("F2", CultureInfo.InvariantCulture);
            }
            finally
            {
                _isUpdatingChancePinControls = false;
            }
        }

        private void ApplyPinnedBirthChance(float? chance)
        {
            if (_model?.HasStandaloneSystem != true || _vfxRenderer == null) return;
            if (chance.HasValue) chance = Math.Clamp(chance.Value, 0f, 1f);
            _vfxRenderer.SetPinnedBirthChance(chance);
            if (chance.HasValue && StudioTimelineView.ChancePinValueText != null)
                StudioTimelineView.ChancePinValueText.Text = chance.Value.ToString("F2", CultureInfo.InvariantCulture);
        }

        private void SetPlaybackSpeed(double speed, bool updateControl = true)
        {
            float normalized = VfxRenderSession.NormalizePlaybackSpeed(speed);
            _model.Speed = normalized;
            if (_vfxRenderer?.ActiveSystem != null)
                _vfxRenderer.ActiveSystem.Speed = normalized;

            if (!updateControl || StudioTimelineView.SpeedComboBox == null) return;
            for (int index = 0; index < StudioTimelineView.SpeedComboBox.Items.Count; index++)
            {
                if (StudioTimelineView.SpeedComboBox.Items[index] is not ComboBoxItem item ||
                    !double.TryParse(
                        item.Tag?.ToString(),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out double candidate))
                {
                    continue;
                }

                if (Math.Abs(candidate - normalized) <= 1e-6)
                {
                    StudioTimelineView.SpeedComboBox.SelectedIndex = index;
                    break;
                }
            }
        }

        internal static float ResolveSimulationFrameDelta(TimeSpan delta, bool discard)
            => discard ? 0f : (float)Math.Max(0d, delta.TotalSeconds);

        internal static double PlaybackSpeedDetent(double speed, int direction)
        {
            if (direction >= 0)
            {
                foreach (double detent in PlaybackSpeedDetents)
                    if (detent > speed + 1e-6) return detent;
                return PlaybackSpeedDetents[^1];
            }

            for (int index = PlaybackSpeedDetents.Length - 1; index >= 0; index--)
                if (PlaybackSpeedDetents[index] < speed - 1e-6) return PlaybackSpeedDetents[index];
            return PlaybackSpeedDetents[0];
        }

        private void TogglePlayback()
        {
            if (_model == null) return;

            if (_model.IsPlaying)
            {
                _vfxRenderer?.Pause();
                _model.IsPlaying = false;
                return;
            }

            if (TryPlaySelectedTimedPreview(restartWhenPlaying: false))
                return;

            if (_model.SelectedSystem != null)
            {
                if (!HasSelectedSystemReady())
                {
                    RequestSystemInspection(_model.SelectedSystem);
                }
                else if (_model.CurrentTime >= _model.TotalDuration)
                {
                    _vfxRenderer.Stop();
                    _model.CurrentTime = 0;
                    _vfxRenderer.Seek(0);
                    _vfxRenderer.Play();
                    _model.IsPlaying = true;
                }
                else
                {
                    _vfxRenderer.Play();
                    _model.IsPlaying = true;
                }
            }
        }
    }
}
