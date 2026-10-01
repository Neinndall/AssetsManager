using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using AssetsManager.Services.Viewer.Animation;
using AssetsManager.Services.Viewer.Interaction;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Services.Viewer.Runtime;
using AssetsManager.Services.Viewer.Vfx.Composition;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using AssetsManager.Services.Viewer.Vfx.Session;
using AssetsManager.Utils;
using AssetsManager.Views.Helpers;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Views.Controls.Viewer
{
    public partial class VfxInspectorControl
    {
        private void OnControlLoaded(object sender, RoutedEventArgs e)
        {
            LoadPreviewDisplayPreferences();
            UpdateViewportClip();
            UpdateInspectorColumnVisibility();
            if (_isActive)
            {
                EnsureOpenGlStarted();
            }
        }

        private void LoadPreviewDisplayPreferences()
        {
            StudioParametersSettings viewerSettings = AppSettings?.StudioParameters;
            VfxStudioSettings vfxSettings = AppSettings?.VfxStudio;
            if (viewerSettings == null || vfxSettings == null) return;

            _isLoadingPreviewPreferences = true;
            try
            {
                _model.ShowPreviewSky = viewerSettings.SkyVisible;
                _model.ShowPreviewGrid = viewerSettings.GridVisible;
                _model.ShowPreviewGround = viewerSettings.GroundVisible;
                _model.ShowPreviewStage = vfxSettings.StageVisible;

                if (Enum.TryParse(vfxSettings.CameraPreset, ignoreCase: true, out VfxPreviewCameraPreset cameraPreset))
                    _model.PreviewCameraPreset = cameraPreset;
                if (Enum.TryParse(vfxSettings.ViewMode, ignoreCase: true, out VfxPreviewViewMode viewMode))
                    _model.PreviewViewMode = viewMode;
                _model.PreviewWireOverlay = vfxSettings.WireOverlay;
            }
            finally
            {
                _isLoadingPreviewPreferences = false;
            }
        }

        private void SavePreviewDisplayPreferences()
        {
            if (_isLoadingPreviewPreferences || AppSettings == null) return;

            AppSettings.StudioParameters ??= new StudioParametersSettings();
            AppSettings.VfxStudio ??= new VfxStudioSettings();

            AppSettings.StudioParameters.SkyVisible = _model.ShowPreviewSky;
            AppSettings.StudioParameters.GridVisible = _model.ShowPreviewGrid;
            AppSettings.StudioParameters.GroundVisible = _model.ShowPreviewGround;
            AppSettings.VfxStudio.ViewMode = _model.PreviewViewMode.ToString();
            AppSettings.VfxStudio.WireOverlay = _model.PreviewWireOverlay;
            AppSettings.VfxStudio.StageVisible = _model.ShowPreviewStage;
            AppSettings.VfxStudio.CameraPreset = _model.PreviewCameraPreset.ToString();
            _ = SavePreviewDisplayPreferencesAsync();
        }

        private async Task SavePreviewDisplayPreferencesAsync()
        {
            try
            {
                await AppSettings.SaveAsync();
            }
            catch (Exception ex)
            {
                LogService?.LogError(ex, "Failed to save VFX preview display preferences.");
            }
        }

        private void ViewportClipGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateViewportClip();
        }

        private void UpdateViewportClip()
        {
            if (ViewportClipGrid == null) return;
            double w = ViewportClipGrid.ActualWidth;
            double h = ViewportClipGrid.ActualHeight;
            if (w > 0 && h > 0)
            {
                ViewportClipGrid.Clip = new RectangleGeometry(new Rect(0, 0, w, h), 10, 10);
            }
        }

        /// <summary>
        /// Activates the VFX viewport when its host view becomes visible.
        /// </summary>
        public void Activate()
        {
            if (_isCleanedUp) return;

            _isActive = true;
            // Reload shared display state on every activation so changes made in Load Project or
            // Chroma Library are reflected immediately when VFX Studio becomes visible.
            LoadPreviewDisplayPreferences();
            // The reference preview starts a fresh RAF clock when content becomes visible, so the
            // first resumed frame advances by zero rather than consuming hidden-tab wall time.
            _discardNextSimulationDelta = true;
            if (!HasSelectedSystemReady())
            {
                RequestSystemInspection(_model.SelectedSystem);
            }

            if (IsLoaded)
            {
                EnsureOpenGlStarted();
                SetRenderLoopRunning(true);
            }
        }

        /// <summary>
        /// Pauses VFX work while the host view is hidden without destroying reusable GPU state.
        /// </summary>
        public void Deactivate()
        {
            CloseAllToolbarPopups();
            _isActive = false;
            _pendingSystem = null;
            // LTK stops the viewport frameloop while hidden. Keep the GL resources alive but stop
            // scheduling empty render callbacks until the Studio becomes visible again.
            SetRenderLoopRunning(false);
        }

        private void SetRenderLoopRunning(bool running)
        {
            if (!_isGlStarted || OpenTkControl == null) return;
            OpenTkControl.RenderContinuously = running;
            if (running)
                OpenTkControl.InvalidateVisual();
        }

        private void EnsureOpenGlStarted()
        {
            if (!_isActive || _isGlStarted || _isCleanedUp || !IsLoaded) return;

            try
            {
                var settings = new OpenTK.Wpf.GLWpfControlSettings
                {
                    MajorVersion = 3,
                    MinorVersion = 3,
                    RenderContinuously = true
                };
                OpenTkControl.Start(settings);
                _isGlStarted = true;
            }
            catch (Exception ex)
            {
                LogService?.LogError(ex, "Failed to initialize the VFX Studio OpenGL viewport.");
                _model.LogMessages.Add($"[ERROR] Failed to initialize the OpenGL viewport: {ex.Message}");
            }
        }

        private void OnControlUnloaded(object sender, RoutedEventArgs e)
        {
            Deactivate();
        }

        /// <summary>
        /// Releases all resources owned by this control. The host calls this once when the Viewer
        /// is torn down; repeated calls are safe.
        /// </summary>
        public void Cleanup()
        {
            if (_isCleanedUp) return;

            _isExitPending = false;
            Deactivate();
            _isCleanedUp = true;
            _model.PropertyChanged -= OnModelPropertyChanged;
            _model.MapVisibilityRequested -= OnMapVisibilityRequested;
            _championLoadGeneration++;

            RunReleaseStep("VFX folder scan cancellation", () => _scanCancellation?.Cancel());
            RunReleaseStep("VFX BIN load cancellation", () => _binCancellation?.Cancel());
            RunReleaseStep("MAP scene load cancellation", () => _mapCancellation?.Cancel());
            RunReleaseStep("MAP clip load cancellation", () => _mapClipCancellation?.Cancel());
            RunReleaseStep("Animation clip load cancellation", () => _animationClipCancellation?.Cancel());

            var cameraController = _cameraController;
            _cameraController = null;
            if (cameraController != null)
            {
                cameraController.RotationStarted -= CameraController_RotationStarted;
                cameraController.RotationEnded -= CameraController_RotationEnded;
            }
            RunReleaseStep(nameof(CustomCameraController), () => cameraController?.Dispose());

            DisposeSceneActorResources();

            var vfxRenderer = _vfxRenderer;
            _vfxRenderer = null;
            RunReleaseStep(nameof(VfxRenderSession), () => vfxRenderer?.Dispose(), gpuBound: true);

            var previewSurfaceRenderer = _previewSurfaceRenderer;
            _previewSurfaceRenderer = null;
            RunReleaseStep(nameof(VfxPreviewSurfaceRenderer), () => previewSurfaceRenderer?.Dispose(), gpuBound: true);

            var mapGeometryRenderer = _mapGeometryRenderer;
            _mapGeometryRenderer = null;
            RunReleaseStep(nameof(MapGeometryRenderer), () => mapGeometryRenderer?.Dispose(), gpuBound: true);

            var mapCharacterRenderer = _mapCharacterRenderer;
            _mapCharacterRenderer = null;
            RunReleaseStep(nameof(MapCharacterRenderer), () => mapCharacterRenderer?.Dispose(), gpuBound: true);

            var mapParticleRenderer = _mapParticleRenderer;
            _mapParticleRenderer = null;
            RunReleaseStep(nameof(MapParticleRenderer), () => mapParticleRenderer?.Dispose(), gpuBound: true);

            var terrainDepthCapture = _terrainDepthCapture;
            _terrainDepthCapture = null;
            _frameTerrainDepth = 0;
            RunReleaseStep("TerrainDepthCapture", () => terrainDepthCapture?.Dispose(), gpuBound: true);

            var mapPostEffectsRenderer = _mapPostEffectsRenderer;
            _mapPostEffectsRenderer = null;
            RunReleaseStep(nameof(MapPostEffectsRenderer), () => mapPostEffectsRenderer?.Dispose(), gpuBound: true);

            var checkerboardBackgroundRenderer = _checkerboardBackgroundRenderer;
            _checkerboardBackgroundRenderer = null;
            RunReleaseStep(nameof(CheckerboardBackgroundRenderer), () => checkerboardBackgroundRenderer?.Dispose(), gpuBound: true);

            var fxaaRenderer = _fxaaRenderer;
            _fxaaRenderer = null;
            RunReleaseStep(nameof(FxaaPostEffectsRenderer), () => fxaaRenderer?.Dispose(), gpuBound: true);
            var smaaRenderer = _smaaRenderer;
            _smaaRenderer = null;
            RunReleaseStep(nameof(SmaaPostEffectsRenderer), () => smaaRenderer?.Dispose(), gpuBound: true);

            var skyRenderer = _skyRenderer;
            _skyRenderer = null;
            RunReleaseStep(nameof(SkyRenderer), () => skyRenderer?.Dispose(), gpuBound: true);
            _genericSkyCube = null;
            _mapSkyCube = null;
            _skyCubeDirty = false;

            var mapSceneRuntime = _mapSceneRuntime;
            _mapSceneRuntime = null;
            RunReleaseStep(nameof(MapSceneRuntime), () => mapSceneRuntime?.Dispose());

            var championMeshRenderer = _championMeshRenderer;
            _championMeshRenderer = null;
            RunReleaseStep(nameof(GlMeshRenderer), () => championMeshRenderer?.Dispose(), gpuBound: true);

            var championAnimationService = _championAnimationService;
            _championAnimationService = null;
            RunReleaseStep(nameof(AnimationService), () => championAnimationService?.Dispose());

            var characterInteractionController = _characterInteractionController;
            _characterInteractionController = null;
            if (characterInteractionController != null)
            {
                characterInteractionController.TransformChanged -= CharacterInteraction_TransformChanged;
                characterInteractionController.SelectionRequested -= CharacterInteraction_SelectionRequested;
                RunReleaseStep(nameof(ViewportModelInteractionController), characterInteractionController.Dispose);
            }
            _characterInteractionModels.Clear();

            var championModel = _championModel;
            _championModel = null;
            RunReleaseStep("Champion SceneModel", () => championModel?.Dispose());

            var clipCatalog = _clipCatalog;
            _clipCatalog = null;
            RunReleaseStep(nameof(VfxClipCatalog), () => clipCatalog?.Dispose());

            _activeBundle = null;
            _championBundle = null;
            ClearCharacterFormState();
            _pendingSystem = null;
            _inspectedSystem = null;

            var gl = _gl;
            _gl = null;
            RunReleaseStep("OpenGL API", () => gl?.Dispose());

            RunReleaseStep(nameof(OpenTkControl), OpenTkControl.Dispose, gpuBound: true);
            _isGlStarted = false;
            ExitRequested = null;

            _model.LogMessages.Add("[GL] VFX Studio resources released.");
        }

        /// <summary>
        /// Runs one disposal step in isolation so a broken component cannot abort the remaining
        /// teardown. GPU-bound steps throw a symbol-loading exception when the OpenGL context is
        /// already destroyed; that is expected because the driver releases every GPU object
        /// created on the context along with it. Any other error is logged and does not stop
        /// the remaining releases.
        /// </summary>
        private void RunReleaseStep(string componentName, Action release, bool gpuBound = false)
        {
            if (release == null) return;

            try
            {
                release();
            }
            catch (Silk.NET.Core.Loader.SymbolLoadingException) when (gpuBound)
            {
                // The OpenGL context owns these handles and may have released them already.
            }
            catch (ObjectDisposedException) when (gpuBound)
            {
                // A WPF teardown can dispose the GL context before the control releases its handles.
            }
            catch (Exception ex)
            {
                LogService?.LogError(ex, $"Failed to release VFX Studio {componentName}.");
            }
        }
    }
}
