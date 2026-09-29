using System;
using System.Linq;
using System.Windows.Media.Media3D;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Views.Controls.Viewer
{
    public partial class VfxInspectorControl
    {
        private void RestoreWorkspaceCamera(VfxWorkspaceTab tab)
        {
            if (tab?.CameraState is not { } state || _cameraController == null)
                return;

            _suppressCameraPresetFit = true;
            try { _model.PreviewCameraPreset = state.Preset; }
            finally { _suppressCameraPresetFit = false; }
            _cameraController.MapNavigationGroundHeight = null;
            ApplyCameraDistanceLimits(VfxPreviewCamera.Stand(state.Preset), tab.Kind == VfxWorkspaceTabKind.Map);
            ProjectionCamera camera;
            if (state.Orthographic)
            {
                _previewOrthographicCamera.Width = state.ProjectionSpan;
                camera = _previewOrthographicCamera;
            }
            else
            {
                _previewPerspectiveCamera.FieldOfView = state.ProjectionSpan;
                camera = _previewPerspectiveCamera;
            }
            _cameraController.SetCamera(camera);
            _cameraController.SnapTo(state.Position, state.LookDirection, state.UpDirection);
        }

        private void OpenCharacterBackdropScene(VfxCharacterBackdropOption option)
        {
            VfxWorkspaceTab source = _model.SelectedWorkspaceTab;
            if (source?.Kind != VfxWorkspaceTabKind.Skin || source.FocusedActor == null || option?.Source == null)
                return;

            // Selection belongs to the destination scene; the source keeps only its open map chooser.
            _isApplyingCharacterViewportState = true;
            try
            {
                _model.SelectedCharacterBackdrop = _model.CharacterBackdrops.FirstOrDefault(candidate =>
                    source.CharacterBackdropKey != null && string.Equals(
                        VfxInstallationMapCatalog.BackdropKey(candidate.Source), source.CharacterBackdropKey,
                        StringComparison.OrdinalIgnoreCase));
            }
            finally { _isApplyingCharacterViewportState = false; }
            CaptureWorkspaceSelection(source);

            string mapKey = VfxInstallationMapCatalog.BackdropKey(option.Source);
            string key = $"{source.OriginSceneKey}|backdrop:{mapKey}";
            VfxWorkspaceTab destination = _model.WorkspaceTabs.FirstOrDefault(tab =>
                string.Equals(tab.Key, key, StringComparison.OrdinalIgnoreCase));
            if (destination == null)
            {
                destination = source.CopyForBackdrop(key, mapKey, MapWorkspaceTitle(option.Source));
                foreach (VfxSceneActor actor in destination.Actors)
                    actor.PropertyChanged += SceneActor_PropertyChanged;
                _model.WorkspaceTabs.Add(destination);
                _model.NotifyWorkspaceTabsChanged();
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    WorkspaceTabsScrollViewer?.ScrollToRightEnd();
                    UpdateWorkspaceTabScrollButtons();
                }));
            }
            _startNextPreviewPaused = destination.FocusedActor.IsPlaybackPaused;
            ActivateWorkspaceTab(destination);
        }
    }
}
