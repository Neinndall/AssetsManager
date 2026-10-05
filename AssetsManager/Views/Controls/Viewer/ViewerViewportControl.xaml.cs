using AssetsManager.Services.Viewer.Resources;
using System;
using System.IO;
using System.Linq;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using LeagueToolkit.Core.Animation;
using LeagueToolkit.Core.Mesh;
using System.Collections.Generic;
using AssetsManager.Services;
using AssetsManager.Services.Core;
using AssetsManager.Services.Viewer.Animation;
using AssetsManager.Services.Viewer.Interaction;
using AssetsManager.Services.Viewer.Runtime;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Services.Viewer.Vfx.Resources;
using AssetsManager.Utils;
using AssetsManager.Utils.Viewport;
using AssetsManager.Views.Models.Viewer;
using AssetsManager.Views.Helpers;
using System.Windows;
using System.Windows.Threading;
using OpenTK.Wpf;
using System.Numerics;

namespace AssetsManager.Views.Controls.Viewer
{
    public partial class ViewerViewportControl : UserControl
    {
        private Silk.NET.OpenGL.GL _gl;
        private GlMeshRenderer _meshRenderer;
        private PreviewSurfaceRenderer _previewSurfaceRenderer;
        private SkyRenderer _skyRenderer;
        private CubeMapData _genericSkyCube;
        private bool _skyCubeDirty;
        private FxaaPostEffectsRenderer _fxaaRenderer;
        private SmaaPostEffectsRenderer _smaaRenderer;

        private readonly ViewerViewportModel _viewModel;
        public ViewerViewportModel ViewModel => _viewModel;

        private readonly OpenGlSnapshotService _snapshotService = new();
        private readonly Viewport3D _dummyViewport = new Viewport3D
        {
            Camera = new PerspectiveCamera(new Point3D(0, 1118, 250), new Vector3D(0, -38, -250), new Vector3D(0, 1, 0), 45)
        };
        public Viewport3D Viewport3D => _dummyViewport;
        public Viewport3D Viewport => Viewport3D;

        public LogService LogService { get; set; }
        public AppSettings AppSettings { get; set; }
        public ViewerPanelControl Panel { get; set; }
        public IAnimationAsset CurrentlyPlayingAnimation => _activeSceneModel?.CurrentAnimation;
        public double CurrentAnimationTime => _activeSceneModel?.AnimationTime ?? 0;

        [System.Runtime.InteropServices.DllImport("opengl32.dll", EntryPoint = "wglGetProcAddress", CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
        private static extern IntPtr wglGetProcAddress(string procName);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
        private static extern IntPtr LoadLibrary(string lpszLib);

        private static readonly IntPtr OpenGLModule = LoadLibrary("opengl32.dll");

        private static IntPtr GetOpenGLProcAddress(string procName)
        {
            var addr = wglGetProcAddress(procName);
            if (addr == IntPtr.Zero)
            {
                addr = GetProcAddress(OpenGLModule, procName);
            }
            return addr;
        }

        private void OpenTkControl_Ready()
        {
            try
            {
                _gl = Silk.NET.OpenGL.GL.GetApi(GetOpenGLProcAddress);
            }
            catch (Exception ex)
            {
                LogService?.LogError(ex, "Failed to initialize Silk.NET OpenGL context.");
            }
        }

        private void OpenTkControl_Render(TimeSpan delta)
        {
            if (_gl == null) return;

            // GLWpfControl makes this viewport's context current immediately before
            // invoking Render. Create GPU resources only here so multiple viewports
            // cannot initialize shaders or buffers against another control's context.
            EnsureSceneRenderers();

            int framebufferWidth = OpenTkControl.FrameBufferWidth;
            int framebufferHeight = OpenTkControl.FrameBufferHeight;
            if (framebufferWidth <= 0 || framebufferHeight <= 0) return;

            TimeSpan renderTime = _renderStopwatch.Elapsed;
            TimeSpan frameDelta = _lastRenderedAt == TimeSpan.Zero
                ? TimeSpan.Zero
                : renderTime - _lastRenderedAt;

            _lastRenderedAt = renderTime;
            UpdateScene(frameDelta);
            RenderScene(framebufferWidth, framebufferHeight, frameDelta);
            RecordRenderedFrame();
            ProcessPendingSnapshot();
            _firstRenderedFrame.TrySetResult(true);
        }

        private void RenderScene(int framebufferWidth, int framebufferHeight, TimeSpan mapFrameDelta)
        {
            _meshRenderer?.ProcessPendingReleases();
            _gl.Viewport(0, 0, (uint)framebufferWidth, (uint)framebufferHeight);

            // Clear color based on transparent background setting
            if (_viewModel.IsTransparentBg)
            {
                _gl.ClearColor(0.0f, 0.0f, 0.0f, 0.0f);
            }
            else
            {
                // Clear to standard dark theme color (#18181b)
                _gl.ClearColor(0.094f, 0.094f, 0.106f, 1.0f);
            }

            _gl.Clear((uint)(Silk.NET.OpenGL.ClearBufferMask.ColorBufferBit | Silk.NET.OpenGL.ClearBufferMask.DepthBufferBit));

            // 1. Get perspective camera from viewport to build View/Projection matrices
            _cameraController?.ApplyPendingRotation();
            var camera = Viewport3D.Camera as PerspectiveCamera;
            if (camera == null) return;

            // 2. Build camera matrices
            var eye = new Vector3((float)camera.Position.X, (float)camera.Position.Y, (float)camera.Position.Z);
            var lookDir = new Vector3((float)camera.LookDirection.X, (float)camera.LookDirection.Y, (float)camera.LookDirection.Z);
            var target = eye + lookDir;
            var up = new Vector3((float)camera.UpDirection.X, (float)camera.UpDirection.Y, (float)camera.UpDirection.Z);
            var view = Matrix4x4.CreateLookAt(eye, target, up);

            float fovRadians = (float)(camera.FieldOfView * (Math.PI / 180.0));
            float aspect = (float)framebufferWidth / framebufferHeight;
            var proj = Matrix4x4.CreatePerspectiveFieldOfView(
                fovRadians,
                aspect,
                CameraPresets.CalculateProjectionNearPlane(lookDir),
                CameraPresets.CalculateProjectionFarPlane(lookDir));
            var viewProj = view * proj;
            _modelInteractionController?.Update(viewProj);

            bool hasClassicScene = _loadedModels.Count > 0 || _auxiliaryModels.Count > 0;
            if (!hasClassicScene)
            {
                return;
            }

            // Render sky before ground, grid, and scene models
            if (_skyRenderer != null)
            {
                if (_genericSkyCube == null)
                {
                    _genericSkyCube = SceneElements.LoadGenericSkyCube(AppSettings, LogService);
                    _skyCubeDirty = true;
                }

                if (_skyCubeDirty)
                {
                    _skyRenderer.SetCube(_genericSkyCube);
                    _skyCubeDirty = false;
                }

                if (_viewModel.IsSkyVisible && !_viewModel.IsTransparentBg)
                {
                    _skyRenderer.Render(view, proj);
                }
            }

            // PBR game shaders light models from the environment the sky shows.
            if (_meshRenderer != null)
                _meshRenderer.ImageLight = _genericSkyCube;

            // 3. Setup lighting from view model settings. The default values reproduce the
            // character preview sun/ambient split while still allowing explicit studio overrides.
            var lighting = GlMeshRenderer.StudioCharacterLighting(
                _viewModel.AmbientIntensity,
                _viewModel.LightRotation,
                _viewModel.LightHeight);
            Vector3 lightDir1 = lighting.LightDirection;
            Vector3 lightColor1 = lighting.LightColor;
            Vector3 lightDir2 = lighting.FillDirection;
            Vector3 lightColor2 = lighting.FillColor;
            Vector3 ambientColor = lighting.AmbientColor;

            // Draw preview surfaces before transparent models, which do not write depth.
            _previewSurfaceRenderer?.Render(viewProj, _viewModel.IsGridVisible,
                _viewModel.IsGroundVisible && !_viewModel.IsTransparentBg, showStage: false);

            // Render primary models, then auxiliary diff geometry.
            foreach (var model in _loadedModels)
            {
                _meshRenderer.Render(
                    model,
                    viewProj,
                    view,
                    proj,
                    eye,
                    lightDir1,
                    lightColor1,
                    lightDir2,
                    lightColor2,
                    ambientColor);
            }
            foreach (var model in _auxiliaryModels)
            {
                _meshRenderer.Render(
                    model,
                    viewProj,
                    view,
                    proj,
                    eye,
                    lightDir1,
                    lightColor1,
                    lightDir2,
                    lightColor2,
                    ambientColor);
            }

            // Glow the game shaders wrote, once every model is drawn and before anti-aliasing.
            _meshRenderer?.ComposeBloom();

            if (_fxaaRenderer != null && _viewModel.IsFxaaEnabled)
            {
                if (AppSettings?.StudioParameters?.AntiAliasingMode == "Smaa")
                {
                    if (_smaaRenderer == null)
                    {
                        _smaaRenderer = new SmaaPostEffectsRenderer();
                        _smaaRenderer.Initialize(_gl);
                    }
                    _smaaRenderer.Render(framebufferWidth, framebufferHeight);
                }
                else _fxaaRenderer.Render(framebufferWidth, framebufferHeight);
            }
        }

        private void EnsureSceneRenderers(bool required = false)
        {
            bool hasClassicScene = required || _loadedModels.Count > 0 || _auxiliaryModels.Count > 0;
            if (_gl == null || !hasClassicScene) return;

            if (hasClassicScene && _meshRenderer == null)
            {
                _meshRenderer = new GlMeshRenderer(AppSettings);
                _meshRenderer.Initialize(_gl);
            }

            if (_previewSurfaceRenderer == null)
            {
                var surfaces = new PreviewSurfaceRenderer();
                surfaces.Initialize(_gl, SceneElements.LoadGroundAppearance(AppSettings, LogService),
                    groundSize: SceneElements.GroundSize, groundHeight: (float)SceneElements.GroundLevel,
                    gridHeight: (float)SceneElements.GroundLevel);
                _previewSurfaceRenderer = surfaces;
                _groundTextureDirty = false;
            }
            else if (_groundTextureDirty)
            {
                _groundTextureDirty = false;
                try
                {
                    _previewSurfaceRenderer.SetGroundAppearance(SceneElements.LoadGroundAppearance(AppSettings, LogService));
                }
                catch (Exception ex)
                {
                    LogService?.LogError(ex, "Failed to refresh preview ground texture.");
                }
            }

            if (hasClassicScene && _skyRenderer == null)
            {
                _skyRenderer = new SkyRenderer();
                _skyRenderer.Initialize(_gl);
                _genericSkyCube ??= SceneElements.LoadGenericSkyCube(AppSettings, LogService);
                _skyCubeDirty = true;
            }

            if (hasClassicScene && _fxaaRenderer == null)
            {
                _fxaaRenderer = new FxaaPostEffectsRenderer();
                _fxaaRenderer.Initialize(_gl);
            }
        }

        private CustomCameraController _cameraController;
        private readonly Dictionary<SceneModel, AnimationService> _animationServices = new();
        private readonly System.Diagnostics.Stopwatch _renderStopwatch = new();
        private readonly System.Diagnostics.Stopwatch _fpsStopwatch = new();
        private bool _isCompositionTargetHooked;
        private int _framesSinceFpsUpdate;
        private TimeSpan _lastRenderedAt;
        private TimeSpan _lastInvalidatedAt;
        private TimeSpan _nextLimitedFrame;
        private OpenGlSnapshotService.SnapshotRequest _pendingSnapshot;

        private SceneModel _activeSceneModel;
        private AnimationModel _activeAnimationModel;
        private readonly List<SceneModel> _loadedModels = new();
        private readonly List<SceneModel> _auxiliaryModels = new();
        private ViewportModelInteractionController _modelInteractionController;
        private bool _isCleanedUp;
        private bool _isOpenTkStarted;
        private TaskCompletionSource<bool> _firstRenderedFrame = CreateFrameCompletionSource();

        private struct ModelUpdateKey
        {
            public IAnimationAsset Animation;
            public double AnimationTime;
            public int VisiblePartsHash;
            public bool IsVisible;
        }

        private readonly Dictionary<SceneModel, ModelUpdateKey> _lastModelUpdates = new();
        // Environment references
        private bool _groundTextureDirty = true;
        private DispatcherOperation _groundRefreshOperation;

        public ViewerViewportControl()
        {
            InitializeComponent();

            _viewModel = new ViewerViewportModel();
            DataContext = _viewModel;
            InitializeModelInteraction();

            _viewModel.PropertyChanged += OnViewportViewModelPropertyChanged;

            Loaded += OnViewportLoaded;
            Unloaded += OnViewportUnloaded;
            IsVisibleChanged += OnViewportVisibilityChanged;
            // WASD over the viewport moves the camera (polled each frame); keep the key from focused controls.
            PreviewKeyDown += (_, e) => e.Handled |= _cameraController?.IsNavigationKey(e.Key) == true;

            UpdateToolbarVisibility();
        }

        private void InitializeModelInteraction()
        {
            if (_modelInteractionController != null) return;
            _modelInteractionController = new ViewportModelInteractionController(
                CameraInputSurface,
                TransformGizmoCanvas,
                () => Viewport3D.Camera as PerspectiveCamera,
                _loadedModels);
            _modelInteractionController.SelectionRequested += OnModelSelectionRequested;
        }

        private void OnViewportViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(ViewerViewportModel.LimitFps):
                    ApplyFpsLimitMode();
                    break;
                case nameof(ViewerViewportModel.IsFpsVisible):
                    _fpsStopwatch.Restart();
                    _framesSinceFpsUpdate = 0;
                    if (!_viewModel.IsFpsVisible)
                        _viewModel.DisplayFps = "0";
                    break;
                case nameof(ViewerViewportModel.FieldOfView):
                    UpdateFieldOfView();
                    break;
                case nameof(ViewerViewportModel.IsTransparentBg):
                case nameof(ViewerViewportModel.IsGroundVisible):
                case nameof(ViewerViewportModel.IsGridVisible):
                case nameof(ViewerViewportModel.IsSkyVisible):
                case nameof(ViewerViewportModel.IsFxaaEnabled):
                    OpenTkControl.InvalidateVisual();
                    break;
            }
        }

        private void ApplyFpsLimitMode()
        {
            if (_isCleanedUp || !_isOpenTkStarted || OpenTkControl == null || !IsLoaded || !IsVisible) return;

            ResetRenderTiming();

            // Drive rendering from one AssetsManager-owned scheduler instead of relying on
            // GLWpfControl.IsVisibleChanged, which can be missed when Start happens late.
            OpenTkControl.RenderContinuously = false;
            if (!_isCompositionTargetHooked)
            {
                CompositionTarget.Rendering += OnCompositionTargetRendering;
                _isCompositionTargetHooked = true;
            }
        }

        private void OnCompositionTargetRendering(object sender, EventArgs e)
        {
            if (_isCleanedUp || !_isOpenTkStarted || OpenTkControl == null ||
                !OpenTkControl.IsLoaded || !OpenTkControl.IsVisible)
            {
                return;
            }

            TimeSpan now = _renderStopwatch.Elapsed;
            if (!_viewModel.LimitFps)
            {
                _lastInvalidatedAt = now;
                OpenTkControl.InvalidateVisual();
                return;
            }

            TimeSpan targetFrameTime = TimeSpan.FromSeconds(1.0 / 60.0);
            if (_lastInvalidatedAt == TimeSpan.Zero || now >= _nextLimitedFrame)
            {
                _nextLimitedFrame = _nextLimitedFrame == TimeSpan.Zero || now - _nextLimitedFrame > targetFrameTime * 4
                    ? now + targetFrameTime
                    : _nextLimitedFrame + targetFrameTime;

                _lastInvalidatedAt = now;
                OpenTkControl.InvalidateVisual();
            }
        }

        private static TaskCompletionSource<bool> CreateFrameCompletionSource() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal OpenTK.Windowing.Common.IGraphicsContext OpenTkContext => OpenTkControl?.Context;

        internal Task WaitForFirstRenderedFrameAsync() => _firstRenderedFrame.Task;

        internal void RequestRender()
        {
            if (_isOpenTkStarted && OpenTkControl != null)
                OpenTkControl.InvalidateVisual();
        }

        internal void EnsureOpenTkStarted(OpenTK.Windowing.Common.IGraphicsContext contextToUse = null)
        {
            if (_isCleanedUp || _isOpenTkStarted || OpenTkControl == null) return;

            var settings = new GLWpfControlSettings
            {
                MajorVersion = 3,
                MinorVersion = 3,
                Profile = OpenTK.Windowing.Common.ContextProfile.Core,
                RenderContinuously = false,
                ContextToUse = contextToUse
            };

            OpenTkControl.Start(settings);
            _isOpenTkStarted = true;
        }

        private void OnViewportLoaded(object sender, RoutedEventArgs e)
        {
            _isCleanedUp = false;
            if (AppSettings != null)
            {
                AppSettings.PropertyChanged -= OnAppSettingsPropertyChanged;
                AppSettings.PropertyChanged += OnAppSettingsPropertyChanged;
                AppSettings.ConfigurationSaved -= OnAppSettingsSaved;
                AppSettings.ConfigurationSaved += OnAppSettingsSaved;
            }
            _animationServices.Clear();
            InitializeModelInteraction();
            _cameraController = new CustomCameraController(Viewport3D, CameraInputSurface);

            ApplyStudioParameters();
            UpdateViewportRendering();
        }

        private void OnViewportVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) =>
            UpdateViewportRendering();

        private void UpdateViewportRendering()
        {
            if (_isCleanedUp) return;

            if (!IsLoaded || !IsVisible)
            {
                if (_isCompositionTargetHooked)
                {
                    CompositionTarget.Rendering -= OnCompositionTargetRendering;
                    _isCompositionTargetHooked = false;
                }
                return;
            }

            EnsureOpenTkStarted();
            ApplyFpsLimitMode();
            OpenTkControl.InvalidateVisual();
            _fpsStopwatch.Restart();
            _framesSinceFpsUpdate = 0;
        }

        private void OnAppSettingsPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            ApplyStudioParameters();
            if (SceneElements.IsGroundLogoSetting(e.PropertyName)) RequestGroundTextureRefresh();
        }

        private void OnAppSettingsSaved(object sender, EventArgs e)
        {
            _ = Dispatcher.InvokeAsync(() =>
            {
                if (_isCleanedUp) return;
                ApplyStudioParameters();
                RequestGroundTextureRefresh();
            });
        }

        private void RequestGroundTextureRefresh()
        {
            if (_isCleanedUp ||
                _groundRefreshOperation?.Status == DispatcherOperationStatus.Pending)
            {
                return;
            }

            _groundRefreshOperation = Dispatcher.InvokeAsync(RefreshGroundTexture, DispatcherPriority.Render);
        }

        private void RefreshGroundTexture()
        {
            if (_isCleanedUp) return;
            _groundTextureDirty = true;
            OpenTkControl.InvalidateVisual();
        }

        private void OnViewportUnloaded(object sender, RoutedEventArgs e)
        {
            Cleanup();
        }

        public void SetupScene()
        {
            if (_cameraController != null)
                _cameraController.IsMapGroundCollisionEnabled = false;

            RequestGroundTextureRefresh();
        }

        public void ApplyStudioParameters()
        {
            StudioParametersSettings studioParameters = AppSettings?.StudioParameters;
            if (studioParameters == null) return;

            _viewModel.IsGroundVisible = studioParameters.GroundVisible;
            _viewModel.IsGridVisible = studioParameters.GridVisible;
            _viewModel.IsSkyVisible = studioParameters.SkyVisible;
            _viewModel.IsTransparentBg = studioParameters.TransparentBackground;
            _viewModel.IsFxaaEnabled = studioParameters.EnableFxaa;
        }


        public void Cleanup()
        {
            if (_isCleanedUp) return;
            _isCleanedUp = true;
            IsVisibleChanged -= OnViewportVisibilityChanged;
            try
            {
                _pendingSnapshot = null;
                if (_modelInteractionController != null)
                {
                    _modelInteractionController.SelectionRequested -= OnModelSelectionRequested;
                    _modelInteractionController.Dispose();
                    _modelInteractionController = null;
                }

                // 1. Desuscribir eventos
                if (_isCompositionTargetHooked)
                {
                    CompositionTarget.Rendering -= OnCompositionTargetRendering;
                    _isCompositionTargetHooked = false;
                }
                _viewModel.PropertyChanged -= OnViewportViewModelPropertyChanged;
                AppSettings?.PropertyChanged -= OnAppSettingsPropertyChanged;
                AppSettings?.ConfigurationSaved -= OnAppSettingsSaved;
                _groundRefreshOperation?.Abort();
                _groundRefreshOperation = null;

                // 2. Limpiar escena y animaciones
                ResetScene();

                // Limpiar todo el viewport
                Viewport.Children.Clear();

                // 6. Liberar los animation services y todos sus buffers cacheados
                foreach (var animationService in _animationServices.Values)
                {
                    animationService.Dispose();
                }
                _animationServices.Clear();

                // 7. Liberar el CameraController (dueño único)
                _cameraController?.Dispose();
                _cameraController = null;

                // Liberar los recursos de renderizado OpenGL de forma aislada
                var meshRenderer = _meshRenderer;
                _meshRenderer = null;
                RunReleaseStep(nameof(GlMeshRenderer), () => meshRenderer?.Dispose(), gpuBound: true);

                var surfaceRenderer = _previewSurfaceRenderer;
                _previewSurfaceRenderer = null;
                RunReleaseStep(nameof(PreviewSurfaceRenderer), () => surfaceRenderer?.Dispose(), gpuBound: true);

                var skyRenderer = _skyRenderer;
                _skyRenderer = null;
                RunReleaseStep(nameof(SkyRenderer), () => skyRenderer?.Dispose(), gpuBound: true);
                _genericSkyCube = null;
                _skyCubeDirty = false;

                var fxaaRenderer = _fxaaRenderer;
                _fxaaRenderer = null;
                RunReleaseStep(nameof(FxaaPostEffectsRenderer), () => fxaaRenderer?.Dispose(), gpuBound: true);
                var smaaRenderer = _smaaRenderer;
                _smaaRenderer = null;
                RunReleaseStep(nameof(SmaaPostEffectsRenderer), () => smaaRenderer?.Dispose(), gpuBound: true);

                var gl = _gl;
                _gl = null;
                RunReleaseStep("OpenGL API", () => gl?.Dispose());

                RunReleaseStep(nameof(OpenTkControl), OpenTkControl.Dispose, gpuBound: true);
                _isOpenTkStarted = false;
            }
            catch (Exception ex)
            {
                LogService.LogDebug($"Notice during ViewerViewportControl.Cleanup: {ex.Message}");
            }
        }

        private void RunReleaseStep(string componentName, Action release, bool gpuBound = false)
        {
            if (release == null) return;

            try
            {
                release();
            }
            catch (Silk.NET.Core.Loader.SymbolLoadingException) when (gpuBound)
            {
                // El contexto de OpenGL ya se desmontó en WPF; el driver libera los recursos asociados.
            }
            catch (ObjectDisposedException) when (gpuBound)
            {
            }
            catch (Exception ex)
            {
                LogService.LogDebug($"Notice releasing ViewerViewport {componentName}: {ex.Message}");
            }
        }


        public bool IsAnimationActive(AnimationModel animationModel) =>
            animationModel != null &&
            ReferenceEquals(_activeAnimationModel, animationModel) &&
            _activeSceneModel?.CurrentAnimation != null;

        public void SetAnimation(AnimationModel animationModel)
        {
            if (_activeSceneModel == null || animationModel?.AnimationData?.AnimationAsset == null) return;

            _activeAnimationModel = animationModel;
            if (Panel?.ViewModel.IsAnimationPlaybackSyncEnabled == true)
            {
                foreach (SceneModel model in _loadedModels)
                {
                    AnimationData data = SynchronizationService.MatchingAnimation(model, animationModel.AnimationData);
                    if (data != null)
                        ActivateAnimation(model, data);
                    else if (model.CurrentAnimation != null)
                        DeactivateAnimation(model);
                }
            }
            else
            {
                ActivateAnimation(_activeSceneModel, animationModel.AnimationData);
            }

            Panel?.SetAnimationPlayingState(animationModel, true, true);
        }

        public void PreviewAnimationAt(AnimationModel animationModel, TimeSpan time)
        {
            if (_activeSceneModel == null || animationModel?.AnimationData?.AnimationAsset == null) return;
            if (!IsAnimationActive(animationModel))
            {
                _activeAnimationModel = animationModel;
                if (Panel?.ViewModel.IsAnimationPlaybackSyncEnabled == true)
                {
                    foreach (SceneModel model in _loadedModels)
                    {
                        AnimationData data = SynchronizationService.MatchingAnimation(model, animationModel.AnimationData);
                        if (data == null) continue;
                        ActivateAnimation(model, data);
                        model.IsAnimationPaused = true;
                    }
                }
                else
                {
                    ActivateAnimation(_activeSceneModel, animationModel.AnimationData);
                    _activeSceneModel.IsAnimationPaused = true;
                }
                Panel?.SetAnimationPlayingState(animationModel, false, true);
            }

            SeekAnimation(time);
        }

        private void ActivateAnimation(SceneModel model, AnimationData data)
        {
            if (model == null || data?.AnimationAsset == null) return;

            SynchronizationService.StartAnimation(model, data);
            _lastModelUpdates.Remove(model);
        }

        public void TogglePauseResume(AnimationModel animationToToggle)
        {
            if (_activeAnimationModel != animationToToggle || _activeSceneModel == null) return;

            bool newPausedState = !_activeSceneModel.IsAnimationPaused;
            if (Panel?.ViewModel.IsAnimationPlaybackSyncEnabled == true)
            {
                foreach (SceneModel model in _loadedModels)
                {
                    SynchronizationService.PauseAnimation(model, newPausedState);
                }
            }
            else
            {
                SynchronizationService.PauseAnimation(_activeSceneModel, newPausedState);
            }

            Panel?.SetAnimationPlayingState(_activeAnimationModel, !newPausedState, true);
        }

        public void SeekAnimation(TimeSpan time)
        {
            if (_activeSceneModel == null) return;

            void SeekModel(SceneModel model)
            {
                if (model?.CurrentAnimation == null) return;
                SynchronizationService.SeekAnimation(model, time.TotalSeconds);
                _lastModelUpdates.Remove(model);
            }

            if (Panel?.ViewModel.IsAnimationPlaybackSyncEnabled == true)
            {
                foreach (SceneModel model in _loadedModels) SeekModel(model);
            }
            else
            {
                SeekModel(_activeSceneModel);
            }
        }

        public void StopAnimation()
        {
            if (_activeAnimationModel != null)
                Panel?.SetAnimationPlayingState(_activeAnimationModel, false, false);

            if (Panel?.ViewModel.IsAnimationPlaybackSyncEnabled == true)
            {
                foreach (SceneModel model in _loadedModels)
                    DeactivateAnimation(model);
            }
            else if (_activeSceneModel != null)
            {
                DeactivateAnimation(_activeSceneModel);
            }

            _activeAnimationModel = null;
        }

        private void DeactivateAnimation(SceneModel model)
        {
            if (model == null) return;
            _lastModelUpdates.Remove(model);
            SynchronizationService.StopAnimation(model);
        }

        public void RemoveAnimation(AnimationModel animationModel)
        {
            if (animationModel == null) return;

            // 1. Stop if it's currently playing
            if (_activeAnimationModel == animationModel)
            {
                StopAnimation();
            }

            // 2. Remove from all loaded models
            foreach (var model in _loadedModels)
            {
                var animData = model.Animations.FirstOrDefault(a => a.Name == animationModel.Name);
                if (animData != null)
                {
                    model.Animations.Remove(animData);
                }
            }
        }

        public void ResetScene()
        {
            StopAnimation();

            foreach (var model in _loadedModels)
            {
                _meshRenderer?.QueueRelease(model);
                if (Viewport.Children.Contains(model.RootVisual))
                    Viewport.Children.Remove(model.RootVisual);
                model.PropertyChanged -= Model_PropertyChanged;
                model.Dispose();
            }
            foreach (var model in _auxiliaryModels)
            {
                _meshRenderer?.QueueRelease(model);
                if (Viewport.Children.Contains(model.RootVisual))
                    Viewport.Children.Remove(model.RootVisual);
                model.PropertyChanged -= Model_PropertyChanged;
                model.Dispose();
            }
            _loadedModels.Clear();
            _auxiliaryModels.Clear();
            _activeSceneModel = null;

            _viewModel.IsAutoRotateActive = false;
            _viewModel.ResetStudioSettings();

            // CRITICAL: Free cached vertex/skin buffers from the previous model so
            // the next load does not retain RAM of a model that is no longer in use.
            foreach (var animationService in _animationServices.Values)
            {
                animationService.Dispose();
            }
            _animationServices.Clear();
            _lastModelUpdates.Clear();

            _viewModel.UpdateSceneDisplay(_loadedModels.Count, _loadedModels.Count > 0 ? _loadedModels[0].Name : null);
        }

        public void AddModel(SceneModel model) => AddModelCore(model, isAuxiliary: false);

        public void AddAuxiliaryModel(SceneModel model) => AddModelCore(model, isAuxiliary: true);

        private void AddModelCore(SceneModel model, bool isAuxiliary)
        {
            if (isAuxiliary)
                _auxiliaryModels.Add(model);
            else
                _loadedModels.Add(model);

            if (model.IsVisible && !Viewport.Children.Contains(model.RootVisual))
                Viewport.Children.Add(model.RootVisual);

            model.PropertyChanged += Model_PropertyChanged;
            if (!isAuxiliary)
            {
                SetActiveModel(model);
                UpdateSceneDisplayFromPrimaryModels();
            }
        }

        private void UpdateSceneDisplayFromPrimaryModels() =>
            _viewModel.UpdateSceneDisplay(_loadedModels.Count, _loadedModels.Count > 0 ? _loadedModels[0].Name : null);

        public void ClearModels()
        {
            var modelsToClear = _loadedModels.Concat(_auxiliaryModels).ToList();
            foreach (var model in modelsToClear)
            {
                RemoveModel(model);
            }
        }

        public void RemoveModel(SceneModel model)
        {
            bool wasAuxiliary = _auxiliaryModels.Remove(model);
            bool wasPrimary = _loadedModels.Remove(model);
            if (!wasAuxiliary && !wasPrimary) return;

            bool removingActiveModel = model == _activeSceneModel;
            if (removingActiveModel)
            {
                _activeSceneModel = null;
            }

            model.PropertyChanged -= Model_PropertyChanged;
            _lastModelUpdates.Remove(model);
            _meshRenderer?.QueueRelease(model);
            if (_animationServices.TryGetValue(model, out var animationService))
            {
                animationService.Dispose();
                _animationServices.Remove(model);
            }
            if (Viewport.Children.Contains(model.RootVisual))
            {
                Viewport.Children.Remove(model.RootVisual);
            }
            model.Dispose();
            if (wasPrimary)
                UpdateSceneDisplayFromPrimaryModels();
        }

        private void Model_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (sender is SceneModel model && e.PropertyName == nameof(SceneModel.IsVisible))
            {
                if (model.IsVisible)
                {
                    if (!Viewport.Children.Contains(model.RootVisual))
                        Viewport.Children.Add(model.RootVisual);
                }
                else
                {
                    if (Viewport.Children.Contains(model.RootVisual))
                        Viewport.Children.Remove(model.RootVisual);
                }
            }
            if (sender is SceneModel renamedModel && e.PropertyName == nameof(SceneModel.Name) &&
                _loadedModels.Contains(renamedModel))
            {
                UpdateSceneDisplayFromPrimaryModels();
            }
        }

        public void SetActiveModel(SceneModel model)
        {
            _activeSceneModel = model;
        }

        public void SetSelectedModels(IEnumerable<SceneModel> models, SceneModel activeModel)
        {
            var selected = models?.Where(model => model != null).ToList() ?? new List<SceneModel>();
            _modelInteractionController?.SetSelection(selected, activeModel);
            AutoArrangeModelsButton.IsEnabled = selected.Count > 1;
        }

        private void OnModelSelectionRequested(
            SceneModel model,
            System.Windows.Input.ModifierKeys modifiers)
        {
            Panel?.SelectModelFromViewport(model, modifiers);
        }

        private void TransformGizmoToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_modelInteractionController != null)
                _modelInteractionController.IsEnabled = TransformGizmoToggle.IsChecked == true;
        }

        private void AutoArrangeModelsButton_Click(object sender, RoutedEventArgs e)
        {
            Panel?.AutoArrangeSelectedModels();
        }

        private static double FoldAnimationTime(double time, double duration)
        {
            if (!(duration > 0d) || !double.IsFinite(time)) return 0d;
            double folded = time % duration;
            return folded < 0d ? folded + duration : folded;
        }

        private void UpdateScene(TimeSpan frameDelta)
        {
            double deltaTime = Math.Clamp(frameDelta.TotalSeconds, 0, 0.25);

            if (_viewModel.IsAutoRotateActive)
            {
                if (IsDiffMode)
                {
                    foreach (SceneModel model in _loadedModels)
                        model.RotationY = ViewportToolUtils.AdvanceAutoRotation(model.RotationY, deltaTime);
                    foreach (SceneModel model in _auxiliaryModels)
                        model.RotationY = ViewportToolUtils.AdvanceAutoRotation(model.RotationY, deltaTime);
                }
                else if (_activeSceneModel != null)
                {
                    _activeSceneModel.RotationY = ViewportToolUtils.AdvanceAutoRotation(_activeSceneModel.RotationY, deltaTime);
                }
            }

            if (_loadedModels.Count == 0) return;

            bool isPlaybackSync = Panel?.ViewModel.IsAnimationPlaybackSyncEnabled == true &&
                                  _activeSceneModel?.CurrentAnimation != null;
            double speed = _activeAnimationModel?.Speed ?? 1.0;

            // Advance the active model first so synchronized models consume the current
            // frame's master time rather than the previous frame's value.
            if (_activeSceneModel?.CurrentAnimation != null && !_activeSceneModel.IsAnimationPaused)
                AdvanceAnimationClock(_activeSceneModel, deltaTime * speed);

            double masterTime = _activeSceneModel?.AnimationTime ?? 0d;
            bool masterPaused = _activeSceneModel?.IsAnimationPaused ?? true;

            foreach (SceneModel model in _loadedModels)
            {
                if (model.CurrentAnimation == null || model.Skeleton == null || model.SkinnedMesh == null)
                    continue;

                if (model != _activeSceneModel)
                {
                    if (isPlaybackSync)
                    {
                        model.AnimationTime = masterTime;
                        model.IsAnimationPaused = masterPaused;
                    }
                    else if (!model.IsAnimationPaused)
                    {
                        AdvanceAnimationClock(model, deltaTime * speed);
                    }
                }

                double playbackTime = model.AnimationTime;

                int visiblePartsHash = model.Parts?.Sum(part => part.IsVisible ? 1 : 0) ?? 0;
                var currentKey = new ModelUpdateKey
                {
                    Animation = model.CurrentAnimation,
                    AnimationTime = playbackTime,
                    VisiblePartsHash = visiblePartsHash,
                    IsVisible = model.IsVisible
                };

                bool needsUpdate = !_lastModelUpdates.TryGetValue(model, out ModelUpdateKey lastKey) ||
                                   lastKey.Animation != currentKey.Animation ||
                                   Math.Abs(lastKey.AnimationTime - currentKey.AnimationTime) >= 0.0001 ||
                                   lastKey.VisiblePartsHash != currentKey.VisiblePartsHash ||
                                   lastKey.IsVisible != currentKey.IsVisible;

                AnimationService animationService = GetAnimationServiceForModel(model);
                if (needsUpdate || model.PoseDefinition.HasDynamics)
                {
                    _lastModelUpdates[model] = currentKey;
                    animationService.Update(
                        (float)playbackTime,
                        model.CurrentAnimation,
                        model.Skeleton,
                        model.SkinnedMesh,
                        model.Parts,
                        model.Name, model, (float)deltaTime);
                    model.GpuSkinningData = animationService.SkinningData;
                    model.SkinningMatrices = animationService.FinalBoneTransforms;
                }

            }

            if (_activeSceneModel?.CurrentAnimation != null)
                Panel?.UpdateAnimationProgress(_activeSceneModel.AnimationTime);
        }

        private static void AdvanceAnimationClock(SceneModel model, double elapsed)
        {
            double duration = model.CurrentAnimation?.Duration ?? 0d;
            if (!(duration > 0d))
            {
                model.AnimationTime = Math.Max(0d, model.AnimationTime + elapsed);
                return;
            }

            double next = model.AnimationTime + elapsed;
            model.AnimationTime = next >= duration || next < 0d
                ? FoldAnimationTime(next, duration)
                : next;
        }

        private void ResetRenderTiming()
        {
            _lastRenderedAt = TimeSpan.Zero;
            _lastInvalidatedAt = TimeSpan.Zero;
            _nextLimitedFrame = TimeSpan.Zero;
            _renderStopwatch.Restart();
        }

        private void RecordRenderedFrame()
        {
            if (!_viewModel.IsFpsVisible)
                return;

            _framesSinceFpsUpdate++;
            double elapsedSeconds = _fpsStopwatch.Elapsed.TotalSeconds;
            if (elapsedSeconds < 1.0)
                return;

            double fps = _framesSinceFpsUpdate / elapsedSeconds;
            _viewModel.DisplayFps = Math.Round(fps).ToString("0");
            _framesSinceFpsUpdate = 0;
            _fpsStopwatch.Restart();
        }

        private AnimationService GetAnimationServiceForModel(SceneModel model)
        {
            if (!_animationServices.TryGetValue(model, out var animationService))
            {
                animationService = new AnimationService(LogService);
                _animationServices[model] = animationService;
            }
            return animationService;
        }

        public void ResetCamera(bool smooth = true)
        {
            Point3D position;
            Vector3D lookDirection;
            Vector3D upDirection = new Vector3D(0.00, 1.00, 0.00);

            if (TryGetModelBounds(out var center, out var maxDim, out var horizontalDim))
            {
                double distance = maxDim * 1.25;
                if (distance < 50) distance = 250;

                double heightFactor = 0.15;
                double horizontalAngle = Math.PI / 2;

                position = new Point3D(
                    center.X + Math.Cos(horizontalAngle) * distance,
                    center.Y + distance * heightFactor,
                    center.Z + Math.Sin(horizontalAngle) * distance);
                lookDirection = center - position;

                if (smooth)
                {
                    _cameraController?.FlyTo(position, lookDirection, upDirection);
                }
                else
                {
                    _cameraController?.SnapTo(position, lookDirection, upDirection);
                }
                _viewModel.FieldOfView = 45;
                return;
            }

            // Fallback coordinates
            position = new Point3D(0.00, 1118.00, 250.00);
            lookDirection = new Vector3D(0.00, -38.00, -250.00);

            if (smooth)
            {
                _cameraController?.FlyTo(position, lookDirection, upDirection);
            }
            else
            {
                _cameraController?.SnapTo(position, lookDirection, upDirection);
            }

            _viewModel.FieldOfView = 45; // MVVM Update
        }

        public void SnapCamera() => ResetCamera(false);

        private void SetCameraView_Click(object sender, RoutedEventArgs e)
        {
            if (_cameraController == null || sender is not Button btn || btn.Tag is not string viewType) return;

            // Compute dynamic target center and distance if model is available
            double baselineY = 1000;
            Point3D targetPoint = new Point3D(0, 90.00 + baselineY, 0);
            double distance = 300.00;

            if (TryGetModelBounds(out var center, out var maxDim, out _))
            {
                targetPoint = center;
                distance = 1.25 * maxDim;
                if (distance < 50) distance = 250;
            }

            var pose = CameraPresets.CalculateCameraView(viewType, targetPoint, distance);
            if (pose == null) return;

            _cameraController.SnapTo(
                pose.Value.Position,
                pose.Value.LookDirection,
                pose.Value.UpDirection);
        }

        private bool TryGetModelBounds(
            out Point3D center,
            out double maxDim,
            out double horizontalDim)
        {
            center = new Point3D();
            maxDim = 0;
            horizontalDim = 0;

            if (_activeSceneModel?.Parts?.Count > 0)
            {
                var bounds = Rect3D.Empty;
                foreach (var part in _activeSceneModel.Parts)
                {
                    if (part.Geometry?.Geometry is MeshGeometry3D mesh)
                        bounds.Union(mesh.Bounds);
                }

                if (!bounds.IsEmpty)
                {
                    double centerX = bounds.X + bounds.SizeX / 2 + _activeSceneModel.PositionX;
                    double centerY = bounds.Y + bounds.SizeY * 0.5 + _activeSceneModel.PositionY;
                    double centerZ = bounds.Z + bounds.SizeZ / 2 + _activeSceneModel.PositionZ;
                    center = new Point3D(centerX, centerY, centerZ);

                    maxDim = Math.Max(bounds.SizeX, Math.Max(bounds.SizeY, bounds.SizeZ));
                    if (maxDim <= 0) maxDim = 150;
                    horizontalDim = Math.Max(bounds.SizeX, bounds.SizeZ);
                    if (horizontalDim <= 0) horizontalDim = maxDim;

                    return true;
                }
            }

            return false;
        }

        private void UpdateFieldOfView()
        {
            if (Viewport.Camera is PerspectiveCamera camera)
            {
                camera.FieldOfView = _viewModel.FieldOfView;
            }
        }

        private void ProcessPendingSnapshot()
        {
            _snapshotService.ProcessPendingSnapshot(ref _pendingSnapshot, _gl,
                OpenTkControl.FrameBufferWidth, OpenTkControl.FrameBufferHeight,
                (width, height) => RenderScene(width, height, TimeSpan.Zero), LogService);
        }

        public void InitiateHighDefinitionSnapshot()
        {
            _pendingSnapshot = OpenGlSnapshotService.RequestUhdSnapshot(
                _gl != null && OpenTkControl.IsVisible, OpenTkControl.FrameBufferWidth,
                OpenTkControl.FrameBufferHeight, _activeSceneModel?.Name ?? "Model", LogService);
            if (_pendingSnapshot != null) OpenTkControl.InvalidateVisual();
        }

        private void ViewportSnapshotButton_Click(object sender, RoutedEventArgs e)
        {
            InitiateHighDefinitionSnapshot();
        }

        // --- Diff Mode support ---
        public static readonly DependencyProperty IsDiffModeProperty =
            DependencyProperty.Register(
                nameof(IsDiffMode),
                typeof(bool),
                typeof(ViewerViewportControl),
                new PropertyMetadata(false, OnIsDiffModeChanged));

        public bool IsDiffMode
        {
            get => (bool)GetValue(IsDiffModeProperty);
            set => SetValue(IsDiffModeProperty, value);
        }

        private static void OnIsDiffModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ViewerViewportControl control)
            {
                control.UpdateToolbarVisibility();
            }
        }

        private void UpdateToolbarVisibility()
        {
            if (StandardToolbarGroup != null)
                StandardToolbarGroup.Visibility = IsDiffMode ? Visibility.Collapsed : Visibility.Visible;
            if (DiffToolbarGroup != null)
                DiffToolbarGroup.Visibility = IsDiffMode ? Visibility.Visible : Visibility.Collapsed;
        }

        public bool IsCombinedModeChecked
        {
            get => CombinedModeToggle.IsChecked == true;
            set => CombinedModeToggle.IsChecked = value;
        }

        public bool IsAutoRotateChecked
        {
            get => AutoRotateToggle.IsChecked == true;
            set => AutoRotateToggle.IsChecked = value;
        }

        public bool IsMeshPartsChecked
        {
            get => MeshPartsToggle.IsChecked == true;
            set => MeshPartsToggle.IsChecked = value;
        }

        public bool IsGhostModeChecked
        {
            get => GhostModeToggle.IsChecked == true;
            set => GhostModeToggle.IsChecked = value;
        }

        public event EventHandler<bool> CombinedModeToggled;
        public event EventHandler<bool> AutoRotateToggled;
        public event EventHandler<bool> MeshPartsToggled;
        public event EventHandler<bool> GhostModeToggled;
        public event EventHandler ResetCamerasClicked;

        private void CombinedModeToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Primitives.ToggleButton tb)
            {
                CombinedModeToggled?.Invoke(this, tb.IsChecked == true);
            }
        }

        private void AutoRotateToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Primitives.ToggleButton tb)
            {
                AutoRotateToggled?.Invoke(this, tb.IsChecked == true);
            }
        }

        private void MeshPartsToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Primitives.ToggleButton tb)
            {
                MeshPartsToggled?.Invoke(this, tb.IsChecked == true);
            }
        }
        private void GhostModeToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Primitives.ToggleButton tb)
            {
                GhostModeToggled?.Invoke(this, tb.IsChecked == true);
            }
        }

        private void ResetCameras_Click(object sender, RoutedEventArgs e)
        {
            ResetCamerasClicked?.Invoke(this, EventArgs.Empty);
        }

        private void ResetCameraButton_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (IsDiffMode)
            {
                ResetCamerasClicked?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                ResetCamera();
            }
        }
    }
}
