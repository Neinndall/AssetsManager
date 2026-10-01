using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Views.Controls.Viewer
{
    public partial class VfxInspectorControl
    {
        private void TracksCanvasContainer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateTimelineTrackMetrics();
            UpdatePlayheadPosition();
        }

        private const double TimelineLiveColumnWidth = 40d;

        private double GetTimelineTrackWidth()
            => TracksCanvasContainer == null
                ? 0d
                : Math.Max(0d, TracksCanvasContainer.ActualWidth - TimelineLiveColumnWidth);

        private void UpdateTimelineTrackMetrics()
        {
            if (_model == null || TracksCanvasContainer == null) return;
            double availableWidth = GetTimelineTrackWidth();
            if (availableWidth <= 0) return;

            double totalDur = _model.TotalDuration > 0 ? _model.TotalDuration : 3.0;

            // Soft, refined translucent slate-blue palette matching LTK-Manager timeline reference
            Brush[] fillPalette = new Brush[]
            {
                new SolidColorBrush(Color.FromArgb(60, 59, 130, 246)),  // Soft Accent Blue
                new SolidColorBrush(Color.FromArgb(60, 14, 165, 233)),  // Soft Sky Blue
                new SolidColorBrush(Color.FromArgb(60, 99, 102, 241)),  // Soft Indigo
                new SolidColorBrush(Color.FromArgb(60, 45, 212, 191)),  // Soft Teal
                new SolidColorBrush(Color.FromArgb(60, 168, 85, 247))   // Soft Purple
            };
            Brush[] borderPalette = new Brush[]
            {
                new SolidColorBrush(Color.FromArgb(160, 59, 130, 246)),
                new SolidColorBrush(Color.FromArgb(160, 14, 165, 233)),
                new SolidColorBrush(Color.FromArgb(160, 99, 102, 241)),
                new SolidColorBrush(Color.FromArgb(160, 45, 212, 191)),
                new SolidColorBrush(Color.FromArgb(160, 168, 85, 247))
            };

            foreach (var b in fillPalette) b.Freeze();
            foreach (var b in borderPalette) b.Freeze();

            int idx = 1;
            foreach (var emitter in _model.Emitters)
            {
                emitter.IndexNumber = idx;
                emitter.TrackBrush = fillPalette[(idx - 1) % fillPalette.Length];
                emitter.TrackBorderBrush = borderPalette[(idx - 1) % borderPalette.Length];

                double delay = emitter.EmitterDef?.TimeBeforeFirstEmission ?? 0;
                VfxEmitterDefinition definition = emitter.EmitterDef;
                double partLife = definition == null ? 1.5 : GetMaximumParticleLifetime(definition);
                double duration = definition?.IsSingleParticle == true
                    ? partLife
                    : definition?.EmitterLifetime is { } emitterLife
                        ? emitterLife + partLife
                        : Math.Max(0, totalDur - delay);
                var metrics = CalculateEmitterTrackMetrics(delay, duration, totalDur, availableWidth);

                emitter.TrackMargin = new Thickness(metrics.BarLeft, 0, 0, 0);
                emitter.TrackWidth = metrics.BarWidth;

                // Yellow Keyframe Marker Dot for Emission Delay
                if (delay > 0.05)
                {
                    emitter.HasDelay = true;
                    emitter.DelayTime = delay;
                    emitter.DelayMarkerMargin = new Thickness(metrics.MarkerLeft, 0, 0, 0);
                }
                else
                {
                    emitter.HasDelay = false;
                    emitter.DelayTime = 0;
                    emitter.DelayMarkerMargin = new Thickness(0);
                }

                idx++;
            }
        }

        internal static (double BarLeft, double BarWidth, double MarkerLeft) CalculateEmitterTrackMetrics(
            double delay,
            double duration,
            double totalDuration,
            double availableWidth)
        {
            double safeTotal = Math.Max(0.001, totalDuration);
            double safeWidth = Math.Max(0, availableWidth);
            double barLeft = Math.Clamp(Math.Max(0, delay) / safeTotal * safeWidth, 0, safeWidth);
            double rawWidth = Math.Max(0, duration) / safeTotal * safeWidth;
            double remainingWidth = Math.Max(0, safeWidth - barLeft);
            double barWidth = Math.Min(Math.Max(remainingWidth > 0 ? 2 : 0, rawWidth), remainingWidth);
            double markerLeft = Math.Clamp(barLeft - 4, 0, Math.Max(0, safeWidth - 8));
            return (barLeft, barWidth, markerLeft);
        }

        private static double GetMaximumParticleLifetime(VfxEmitterDefinition emitter)
            => VfxDurationCalculator.GetMaximumParticleLifetime(emitter);

        private void UpdatePlayheadPosition()
        {
            if (_model == null || TracksCanvasContainer == null || PlayheadLine == null) return;
            double availableWidth = GetTimelineTrackWidth();
            if (availableWidth <= 0) return;

            double totalDur = _model.TotalDuration > 0 ? _model.TotalDuration : 3.0;
            double ratio = Math.Clamp(_model.CurrentTime / totalDur, 0.0, 1.0);
            double posX = ratio * availableWidth;

            PlayheadLine.X1 = posX;
            PlayheadLine.X2 = posX;
            if (PlayheadHandle != null)
                Canvas.SetLeft(PlayheadHandle, posX - 5);

            if (LoopBoundaryLine != null && LoopBoundaryHandle != null &&
                LoopStartLine != null && LoopStartHandle != null && LoopRangeBand != null)
            {
                (double from, double to) = ClampPreviewLoop(
                    _model.ActiveLoopStart,
                    _model.ActiveLoopDuration > 0 ? _model.ActiveLoopDuration : totalDur,
                    totalDur);
                double startPosX = from / totalDur * availableWidth;
                double endPosX = to / totalDur * availableWidth;

                LoopStartLine.X1 = startPosX;
                LoopStartLine.X2 = startPosX;
                Canvas.SetLeft(LoopStartHandle, startPosX - 7);

                LoopBoundaryLine.X1 = endPosX;
                LoopBoundaryLine.X2 = endPosX;
                Canvas.SetLeft(LoopBoundaryHandle, endPosX - 7);

                Canvas.SetLeft(LoopRangeBand, startPosX);
                LoopRangeBand.Width = Math.Max(0d, endPosX - startPosX);
            }
        }

        internal static bool ShouldRestartPreview(bool enabled, double currentTime, double boundary)
            => enabled && boundary > 0 && currentTime >= boundary;

        internal static (double From, double To) ClampPreviewLoop(double from, double to, double span)
        {
            double safeSpan = double.IsFinite(span) ? Math.Max(PreviewLoopMinimumSpan, span) : PreviewLoopMinimumSpan;
            double safeTo = Math.Clamp(double.IsFinite(to) ? to : safeSpan, PreviewLoopMinimumSpan, safeSpan);
            double safeFrom = Math.Clamp(
                double.IsFinite(from) ? from : 0d,
                0d,
                Math.Max(0d, safeTo - PreviewLoopMinimumSpan));
            return (safeFrom, safeTo);
        }

        internal static double ResolvePreviewLoopRestart(double from, double to, double span)
            => ClampPreviewLoop(from, to, span).From;

        internal static double ResolveTimelineDuration(double playbackDuration)
            => double.IsFinite(playbackDuration) && playbackDuration > 0
                ? Math.Max(0.05, playbackDuration)
                : 10.0;

        private bool _isDraggingLoopStart;
        private bool _isDraggingLoopBoundary;

        private void LoopStartHandle_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _isDraggingLoopStart = true;
            ((UIElement)sender).CaptureMouse();
            UpdateLoopStartFromMouse(e.GetPosition(TracksCanvasContainer).X);
            e.Handled = true;
        }

        private void LoopStartHandle_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_isDraggingLoopStart && e.LeftButton == MouseButtonState.Pressed)
            {
                UpdateLoopStartFromMouse(e.GetPosition(TracksCanvasContainer).X);
                e.Handled = true;
            }
        }

        private void LoopStartHandle_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDraggingLoopStart) return;
            _isDraggingLoopStart = false;
            ((UIElement)sender).ReleaseMouseCapture();
            e.Handled = true;
        }

        private void LoopBoundaryHandle_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _isDraggingLoopBoundary = true;
            ((UIElement)sender).CaptureMouse();
            UpdateLoopBoundaryFromMouse(e.GetPosition(TracksCanvasContainer).X);
            e.Handled = true;
        }

        private void LoopBoundaryHandle_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_isDraggingLoopBoundary && e.LeftButton == MouseButtonState.Pressed)
            {
                UpdateLoopBoundaryFromMouse(e.GetPosition(TracksCanvasContainer).X);
                e.Handled = true;
            }
        }

        private void LoopBoundaryHandle_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDraggingLoopBoundary)
            {
                _isDraggingLoopBoundary = false;
                ((UIElement)sender).ReleaseMouseCapture();
                e.Handled = true;
            }
        }

        private void UpdateLoopStartFromMouse(double mouseX)
        {
            if (_model == null || TracksCanvasContainer == null) return;
            double availableWidth = GetTimelineTrackWidth();
            if (availableWidth <= 0) return;

            double totalDur = _model.TotalDuration > 0 ? _model.TotalDuration : 3.0;
            double requested = Math.Clamp(mouseX / availableWidth, 0d, 1d) * totalDur;
            (double from, double to) = ClampPreviewLoop(
                requested,
                _model.ActiveLoopDuration > 0 ? _model.ActiveLoopDuration : totalDur,
                totalDur);

            _model.ActiveLoopStart = Math.Round(from, 3);
            _model.ActiveLoopDuration = Math.Round(to, 3);
            _model.IsPreviewLoopEnabled = true;
            UpdatePlayheadPosition();
        }

        private void UpdateLoopBoundaryFromMouse(double mouseX)
        {
            if (_model == null || TracksCanvasContainer == null) return;
            double availableWidth = GetTimelineTrackWidth();
            if (availableWidth <= 0) return;

            double totalDur = _model.TotalDuration > 0 ? _model.TotalDuration : 3.0;
            double requested = Math.Clamp(mouseX / availableWidth, 0d, 1d) * totalDur;
            (double from, double to) = ClampPreviewLoop(_model.ActiveLoopStart, requested, totalDur);

            _model.ActiveLoopStart = Math.Round(from, 3);
            _model.ActiveLoopDuration = Math.Round(to, 3);
            _model.IsPreviewLoopEnabled = true;
            UpdatePlayheadPosition();
        }

        private bool _isTimelineDragging;

        private void TimelineGrid_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_isDraggingLoopStart || _isDraggingLoopBoundary) return;
            if (e.OriginalSource is FrameworkElement fe &&
                (fe == LoopStartHandle || fe == LoopBoundaryHandle || fe == LoopBoundaryCanvas ||
                 fe == LoopStartLine || fe == LoopBoundaryLine || fe == LoopRangeBand)) return;
            _isTimelineDragging = true;
            UpdateSeekFromTimeline(e.GetPosition(TracksCanvasContainer).X);
        }

        private void TimelineGrid_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_isTimelineDragging && e.LeftButton == MouseButtonState.Pressed)
            {
                UpdateSeekFromTimeline(e.GetPosition(TracksCanvasContainer).X);
            }
        }

        private void TimelineGrid_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            _isTimelineDragging = false;
        }

        private void UpdateSeekFromTimeline(double mouseX)
        {
            double availableWidth = GetTimelineTrackWidth();
            if (availableWidth <= 0 || _model == null) return;

            double ratio = Math.Clamp(mouseX / availableWidth, 0.0, 1.0);
            double seekTime = ratio * _model.TotalDuration;

            SeekTimeline(seekTime);
        }

        private void StepBack_Click(object sender, RoutedEventArgs e)
            => StepPlayback(-1);

        private void StepForward_Click(object sender, RoutedEventArgs e)
            => StepPlayback(1);

        private void StepPlayback(int frames)
        {
            if (_model == null || frames == 0) return;

            _vfxRenderer?.Pause();
            _model.IsPlaying = false;

            double newTime = PlaybackStepTarget(
                _model.CurrentTime,
                frames,
                _model.TotalDuration);
            SeekTimeline(newTime);
            UpdatePlayheadPosition();
        }

        /// <summary>
        /// Moves the timeline of the focused preview and its MAP Character clip. Background scene
        /// actors keep their own transport.
        /// </summary>
        private void SeekTimeline(double time)
        {
            _model.CurrentTime = time;
            SyncMapCharacterClipTime(time);
            _vfxRenderer?.Seek(time);
        }

        internal static double PlaybackStepTarget(double currentTime, int frames, double span)
        {
            const double frame = 1d / 60d;
            double target = Math.Max(0d, currentTime + frames * frame);
            return span > 0d && double.IsFinite(span)
                ? Math.Min(target, span)
                : target;
        }

        private void RunKeys_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // WASD over the viewport moves the camera (polled each frame); swallow the key so a
            // focused browser tree does not jump to items by letter.
            if (_cameraController?.IsNavigationKey(e.Key) == true)
            {
                e.Handled = true;
                return;
            }

            if (ShouldIgnoreRunHotkey(Keyboard.FocusedElement as DependencyObject)) return;

            ModifierKeys modifiers = Keyboard.Modifiers;
            if ((modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0) return;
            bool shift = (modifiers & ModifierKeys.Shift) != 0;

            bool handled = true;
            switch (e.Key)
            {
                case Key.Space:
                    PlayPauseToggle_Click(this, new RoutedEventArgs());
                    break;
                case Key.Left:
                    StepPlayback(shift ? -6 : -1);
                    break;
                case Key.Right:
                    StepPlayback(shift ? 6 : 1);
                    break;
                case Key.Home:
                    RestartPlayback();
                    break;
                case Key.F:
                    ResetCamera();
                    break;
                case Key.S when _model.IsRawSystemsMode && _model.SelectedEmitter != null:
                    ToggleEmitterSolo(_model.SelectedEmitter);
                    break;
                case Key.M when _model.IsRawSystemsMode && _model.SelectedEmitter != null:
                    ToggleEmitterMuted(_model.SelectedEmitter);
                    break;
                case Key.OemOpenBrackets:
                    SetPlaybackSpeed(PlaybackSpeedDetent(_model.Speed, -1));
                    break;
                case Key.OemCloseBrackets:
                    SetPlaybackSpeed(PlaybackSpeedDetent(_model.Speed, 1));
                    break;
                default:
                    handled = false;
                    break;
            }

            if (handled) e.Handled = true;
        }

        private static bool ShouldIgnoreRunHotkey(DependencyObject focused)
            => focused is TextBoxBase or PasswordBox or ComboBox or Slider or ButtonBase or
               Selector or TreeView or MenuItem;

        private void RestartPlayback()
        {
            if (_model == null) return;
            bool wasPlaying = _model.IsPlaying;
            SeekTimeline(0d);
            if (wasPlaying) _vfxRenderer?.Play();
            UpdatePlayheadPosition();
        }

        private void PlayPauseToggle_Click(object sender, RoutedEventArgs e)
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

        private void StopToBindPose_Click(object sender, RoutedEventArgs e)
        {
            if (_model == null) return;

            AnimationClipCatalogItem bindPose = _model.DetectedAnimations.FirstOrDefault(item => item.IsBindPose);
            if (bindPose != null)
            {
                _model.SelectedAnimation = bindPose;
            }
            else
            {
                _animationClipCancellation?.Cancel();
                _activeAnimationClip = null;
                ResetChampionToBindPose();
                _model.StatusText = "Character in Bind pose (T-Pose).";
                _model.TotalDuration = 0d;
                _model.CurrentTime = 0d;
                _model.IsPlaying = false;
                _vfxRenderer?.SetSystem(null);
                OpenTkControl?.InvalidateVisual();
            }
        }

        private void Ruler_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (TracksCanvasContainer == null || _model == null) return;

            double mouseX = e.GetPosition(TracksCanvasContainer).X;
            _rulerPressX = mouseX;
            _isRulerDragging = true;
            _isRulerCreatingLoop = false;
            ((UIElement)sender).CaptureMouse();
            e.Handled = true;
        }

        private void Ruler_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isRulerDragging || e.LeftButton != MouseButtonState.Pressed || TracksCanvasContainer == null)
                return;

            double mouseX = e.GetPosition(TracksCanvasContainer).X;
            if (!_isRulerCreatingLoop &&
                _model.IsPreviewLoopEnabled &&
                Math.Abs(mouseX - _rulerPressX) >= RulerLoopDragThreshold)
            {
                _isRulerCreatingLoop = true;
            }

            if (_isRulerCreatingLoop)
                UpdateLoopRangeFromRuler(_rulerPressX, mouseX);

            e.Handled = true;
        }

        private void Ruler_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isRulerDragging || TracksCanvasContainer == null) return;

            double mouseX = e.GetPosition(TracksCanvasContainer).X;
            if (_isRulerCreatingLoop)
                UpdateLoopRangeFromRuler(_rulerPressX, mouseX);
            else
                UpdateSeekFromTimeline(mouseX);

            _isRulerDragging = false;
            _isRulerCreatingLoop = false;
            ((UIElement)sender).ReleaseMouseCapture();
            e.Handled = true;
        }

        private double TimelineTimeFromX(double mouseX)
        {
            double availableWidth = GetTimelineTrackWidth();
            if (availableWidth <= 0 || _model == null) return 0d;

            double ratio = Math.Clamp(mouseX / availableWidth, 0d, 1d);
            return ratio * _model.TotalDuration;
        }

        private void UpdateLoopRangeFromRuler(double anchorX, double currentX)
        {
            if (_model == null) return;

            double totalDuration = Math.Max(PreviewLoopMinimumSpan, _model.TotalDuration);
            double from = Math.Min(TimelineTimeFromX(anchorX), TimelineTimeFromX(currentX));
            double to = Math.Max(TimelineTimeFromX(anchorX), TimelineTimeFromX(currentX));
            if (to - from < PreviewLoopMinimumSpan)
            {
                to = Math.Min(totalDuration, from + PreviewLoopMinimumSpan);
                if (to - from < PreviewLoopMinimumSpan)
                    from = Math.Max(0d, to - PreviewLoopMinimumSpan);
            }

            _model.ActiveLoopStart = Math.Round(from, 3);
            _model.ActiveLoopDuration = Math.Round(to, 3);
            _model.IsPreviewLoopEnabled = true;
            UpdatePlayheadPosition();
        }


        private static string GetBlendModeName(int blendMode) => VfxBlendModes.Describe(blendMode);
    }
}
