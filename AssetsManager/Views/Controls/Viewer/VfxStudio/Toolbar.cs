using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Services.Viewer.Vfx.Session;
using AssetsManager.Services.Viewer.Vfx.Composition;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Views.Controls.Viewer
{
    public partial class VfxInspectorControl
    {
        private AnimationClipCatalogItem _bindPoseReturnAnimation;

        private void ViewportSnapshotButton_Click(object sender, RoutedEventArgs e)
        {
            _pendingSnapshot = OpenGlSnapshotService.RequestUhdSnapshot(
                _gl != null && _isActive && !_isCleanedUp && OpenTkControl.IsVisible,
                OpenTkControl.FrameBufferWidth, OpenTkControl.FrameBufferHeight,
                _model.SelectedWorkspaceTab?.Title ?? "VFXStudio", LogService);
            if (_pendingSnapshot != null) OpenTkControl.InvalidateVisual();
        }

        private void PreviewBindPoseToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton toggle) return;

            if (toggle.IsChecked == true)
            {
                _bindPoseReturnAnimation = _model.SelectedAnimation;
                _model.IsAnimationMode = true;
                StopToBindPose_Click(sender, e);
            }
            else
            {
                AnimationClipCatalogItem animation = _bindPoseReturnAnimation != null &&
                    _model.DetectedAnimations.Contains(_bindPoseReturnAnimation)
                    ? _bindPoseReturnAnimation : VfxClipCatalog.OpeningClip(_model.DetectedAnimations);
                if (animation is { IsBindPose: false })
                {
                    _model.IsAnimationMode = true;
                    _model.SelectedAnimation = animation;
                    _bindPoseReturnAnimation = null;
                }
            }

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
