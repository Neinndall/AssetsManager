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
using System.Collections.Generic;
using AssetsManager.Services;
using AssetsManager.Services.Core;
using AssetsManager.Services.Viewer.Interaction;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Services.Viewer.Vfx.Resources;
using AssetsManager.Utils;
using AssetsManager.Utils.Viewport;
using AssetsManager.Views.Models.Viewer;
using AssetsManager.Views.Models.Dialogs.Controls;
using AssetsManager.Views.Helpers;
using System.Windows;
using System.Windows.Threading;
using OpenTK.Wpf;
using System.Numerics;

namespace AssetsManager.Views.Dialogs.Controls
{
    public partial class SknDiffViewportControl : UserControl
    {
        private Silk.NET.OpenGL.GL _gl;
        private GlMeshRenderer _meshRenderer;
        private PreviewSurfaceRenderer _previewSurfaceRenderer;
        private SkyRenderer _skyRenderer;
        private CubeMapData _genericSkyCube;
        private bool _skyCubeDirty;
        private FxaaPostEffectsRenderer _fxaaRenderer;
        private SmaaPostEffectsRenderer _smaaRenderer;

        private readonly SknDiffViewportModel _viewModel;
        public SknDiffViewportModel ViewModel => _viewModel;

        private readonly Viewport3D _dummyViewport = new Viewport3D
        {
            Camera = new PerspectiveCamera(new Point3D(0, 1118, 250), new Vector3D(0, -38, -250), new Vector3D(0, 1, 0), 45)
        };
        public Viewport3D Viewport3D => _dummyViewport;
        public Viewport3D Viewport => Viewport3D;

        public LogService LogService { get; set; }
        public AppSettings AppSettings { get; set; }

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
            double deltaTime = Math.Clamp(frameDelta.TotalSeconds, 0, 0.25);
            if (_viewModel.IsAutoRotateActive)
                foreach (SceneModel model in _loadedModels.Concat(_auxiliaryModels))
                    model.RotationY = ViewportToolUtils.AdvanceAutoRotation(model.RotationY, deltaTime);
            RenderScene(framebufferWidth, framebufferHeight);
            OpenTkControl.Opacity = 1d;
            RecordRenderedFrame();
            _firstRenderedFrame.TrySetResult(true);
        }

        private void RenderScene(int framebufferWidth, int framebufferHeight)
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
                Color background = ((SolidColorBrush)FindResource("ViewportBackground")).Color;
                _gl.ClearColor(background.R / 255f, background.G / 255f,
                    background.B / 255f, background.A / 255f);
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

            bool hasModels = _loadedModels.Count > 0 || _auxiliaryModels.Count > 0;
            if (!hasModels)
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
            bool hasModels = required || _loadedModels.Count > 0 || _auxiliaryModels.Count > 0;
            if (_gl == null || !hasModels) return;

            if (hasModels && _meshRenderer == null)
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

            if (hasModels && _skyRenderer == null)
            {
                _skyRenderer = new SkyRenderer();
                _skyRenderer.Initialize(_gl);
                _genericSkyCube ??= SceneElements.LoadGenericSkyCube(AppSettings, LogService);
                _skyCubeDirty = true;
            }

            if (hasModels && _fxaaRenderer == null)
            {
                _fxaaRenderer = new FxaaPostEffectsRenderer();
                _fxaaRenderer.Initialize(_gl);
            }
        }

        private CustomCameraController _cameraController;
        private readonly System.Diagnostics.Stopwatch _renderStopwatch = new();
        private readonly System.Diagnostics.Stopwatch _fpsStopwatch = new();
        private bool _isCompositionTargetHooked;
        private int _framesSinceFpsUpdate;
        private TimeSpan _lastRenderedAt;
        private TimeSpan _lastInvalidatedAt;
        private TimeSpan _nextLimitedFrame;

        private SceneModel _activeSceneModel;
        private readonly List<SceneModel> _loadedModels = new();
        private readonly List<SceneModel> _auxiliaryModels = new();
        private bool _isCleanedUp;
        private bool _isOpenTkStarted;
        private TaskCompletionSource<bool> _firstRenderedFrame = CreateFrameCompletionSource();

        // Environment references
        private bool _groundTextureDirty = true;
        private DispatcherOperation _groundRefreshOperation;

        public SknDiffViewportControl()
        {
            InitializeComponent();

            _viewModel = new SknDiffViewportModel();
            DataContext = _viewModel;

            _viewModel.PropertyChanged += OnViewportViewModelPropertyChanged;

            Loaded += OnViewportLoaded;
            Unloaded += OnViewportUnloaded;
            IsVisibleChanged += OnViewportVisibilityChanged;
            // WASD over the viewport moves the camera (polled each frame); keep the key from focused controls.
            PreviewKeyDown += (_, e) => e.Handled |= _cameraController?.IsNavigationKey(e.Key) == true;

        }

        private void OnViewportViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(SknDiffViewportModel.LimitFps):
                    ApplyFpsLimitMode();
                    break;
                case nameof(SknDiffViewportModel.IsFpsVisible):
                    _fpsStopwatch.Restart();
                    _framesSinceFpsUpdate = 0;
                    if (!_viewModel.IsFpsVisible)
                        _viewModel.DisplayFps = "0";
                    break;
                case nameof(SknDiffViewportModel.FieldOfView):
                    UpdateFieldOfView();
                    break;
                case nameof(SknDiffViewportModel.IsTransparentBg):
                case nameof(SknDiffViewportModel.IsGroundVisible):
                case nameof(SknDiffViewportModel.IsGridVisible):
                case nameof(SknDiffViewportModel.IsSkyVisible):
                case nameof(SknDiffViewportModel.IsFxaaEnabled):
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
            if (_isCleanedUp) return;
            if (AppSettings != null)
            {
                AppSettings.PropertyChanged -= OnAppSettingsPropertyChanged;
                AppSettings.PropertyChanged += OnAppSettingsPropertyChanged;
                AppSettings.ConfigurationSaved -= OnAppSettingsSaved;
                AppSettings.ConfigurationSaved += OnAppSettingsSaved;
            }
            _cameraController ??= new CustomCameraController(Viewport3D, CameraInputSurface);

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
                OpenTkControl.Opacity = 0d;
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
            Loaded -= OnViewportLoaded;
            Unloaded -= OnViewportUnloaded;
            IsVisibleChanged -= OnViewportVisibilityChanged;
            if (_isCompositionTargetHooked) CompositionTarget.Rendering -= OnCompositionTargetRendering;
            _isCompositionTargetHooked = false;
            _viewModel.PropertyChanged -= OnViewportViewModelPropertyChanged;
            if (AppSettings != null)
            {
                AppSettings.PropertyChanged -= OnAppSettingsPropertyChanged;
                AppSettings.ConfigurationSaved -= OnAppSettingsSaved;
            }
            _groundRefreshOperation?.Abort();
            _groundRefreshOperation = null;
            ClearModels();
            RunReleaseStep(nameof(CustomCameraController), () => _cameraController?.Dispose());
            _cameraController = null;
            RunReleaseStep(nameof(GlMeshRenderer), () => _meshRenderer?.Dispose(), gpuBound: true);
            _meshRenderer = null;
            RunReleaseStep(nameof(PreviewSurfaceRenderer), () => _previewSurfaceRenderer?.Dispose(), gpuBound: true);
            _previewSurfaceRenderer = null;
            RunReleaseStep(nameof(SkyRenderer), () => _skyRenderer?.Dispose(), gpuBound: true);
            _skyRenderer = null;
            _genericSkyCube = null;
            RunReleaseStep(nameof(FxaaPostEffectsRenderer), () => _fxaaRenderer?.Dispose(), gpuBound: true);
            _fxaaRenderer = null;
            RunReleaseStep(nameof(SmaaPostEffectsRenderer), () => _smaaRenderer?.Dispose(), gpuBound: true);
            _smaaRenderer = null;
            RunReleaseStep("OpenGL API", () => _gl?.Dispose());
            _gl = null;
            RunReleaseStep(nameof(OpenTkControl), OpenTkControl.Dispose, gpuBound: true);
            _isOpenTkStarted = false;
            _firstRenderedFrame.TrySetCanceled();
        }

        public void AddModel(SceneModel model)
        {
            _loadedModels.Add(model);
            _activeSceneModel = model;
            _viewModel.UpdateSceneDisplay(_loadedModels.Count, _loadedModels[0].Name);
        }

        public void AddAuxiliaryModel(SceneModel model) => _auxiliaryModels.Add(model);

        public void ClearModels()
        {
            foreach (SceneModel model in _loadedModels.Concat(_auxiliaryModels).ToArray()) RemoveModel(model);
        }

        public void RemoveModel(SceneModel model)
        {
            bool primary = _loadedModels.Remove(model);
            bool auxiliary = _auxiliaryModels.Remove(model);
            if (!primary && !auxiliary) return;
            if (ReferenceEquals(_activeSceneModel, model)) _activeSceneModel = _loadedModels.LastOrDefault();
            _meshRenderer?.QueueRelease(model);
            model.Dispose();
            _viewModel.UpdateSceneDisplay(_loadedModels.Count, _loadedModels.FirstOrDefault()?.Name);
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
                LogService?.LogError(ex, $"Failed to release SKN comparison {componentName}.");
            }
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

        public void ResetCamera(bool smooth = true)
        {
            Point3D position;
            Vector3D lookDirection;
            Vector3D upDirection = new Vector3D(0.00, 1.00, 0.00);

            if (TryGetModelBounds(out var center, out _, out var radius))
            {
                double aspect = OpenTkControl.ActualHeight > 0d
                    ? Math.Max(1d, OpenTkControl.ActualWidth) / OpenTkControl.ActualHeight
                    : 1d;
                double distance = CameraPresets.CalculatePerspectiveFrameDistance(
                    radius, CameraPresets.OrbitFieldOfView, aspect);

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
            out double radius)
        {
            center = new Point3D();
            maxDim = 0;
            radius = 0;

            if (_activeSceneModel?.Parts?.Count > 0)
            {
                Rect3D bounds = ViewerInteractionService.GetWorldBounds(_activeSceneModel);

                if (!bounds.IsEmpty)
                {
                    double centerX = bounds.X + bounds.SizeX / 2;
                    double centerY = bounds.Y + bounds.SizeY * 0.5;
                    double centerZ = bounds.Z + bounds.SizeZ / 2;
                    center = new Point3D(centerX, centerY, centerZ);

                    maxDim = Math.Max(bounds.SizeX, Math.Max(bounds.SizeY, bounds.SizeZ));
                    if (maxDim <= 0) maxDim = 150;
                    radius = Math.Max(1d, new Vector3D(bounds.SizeX, bounds.SizeY, bounds.SizeZ).Length / 2d);

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

        private void ResetCameraButton_Click(object sender, RoutedEventArgs e) =>
            ResetCamerasClicked?.Invoke(this, EventArgs.Empty);
    }
}
