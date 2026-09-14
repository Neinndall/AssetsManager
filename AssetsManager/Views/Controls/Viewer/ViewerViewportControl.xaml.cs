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
using LeagueToolkit.Hashing;
using System.Collections.Generic;
using AssetsManager.Services;
using AssetsManager.Services.Core;
using AssetsManager.Services.Viewer.Animation;
using AssetsManager.Services.Viewer.Interaction;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Services.Viewer.Vfx.Session;
using AssetsManager.Utils;
using AssetsManager.Utils.Rendering;
using AssetsManager.Views.Models.Viewer;
using AssetsManager.Views.Helpers;
using Microsoft.Win32;
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
        private VfxRenderSession _vfxRenderer;
        private GridRenderer _gridRenderer;
        private VfxSystemModel _selectedVfxSystem;
        private bool _isMapGeometry;

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
        public VfxLoadingService VfxLoadingService { get; set; }
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
                EnsureSceneRenderers();
                EnsureVfxRenderer();
                _vfxRenderer?.SetVfxSystem(_selectedVfxSystem);
            }
            catch (Exception ex)
            {
                LogService.LogError(ex, "Failed to initialize Silk.NET OpenGL context.");
            }
        }

        private void OpenTkControl_Render(TimeSpan delta)
        {
            if (_gl == null) return;
            int framebufferWidth = OpenTkControl.FrameBufferWidth;
            int framebufferHeight = OpenTkControl.FrameBufferHeight;
            if (framebufferWidth <= 0 || framebufferHeight <= 0) return;

            TimeSpan renderTime = _renderStopwatch.Elapsed;
            TimeSpan frameDelta = _lastRenderedAt == TimeSpan.Zero
                ? delta
                : renderTime - _lastRenderedAt;

            _lastRenderedAt = renderTime;
            UpdateScene(frameDelta);
            RenderScene(framebufferWidth, framebufferHeight, frameDelta, updateVfx: true);
            RecordRenderedFrame();
            ProcessPendingSnapshot();
        }

        private void RenderScene(int framebufferWidth, int framebufferHeight, TimeSpan frameDelta, bool updateVfx)
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
                CalculateProjectionNearPlane(lookDir, _isMapGeometry),
                CalculateProjectionFarPlane(lookDir));
            var viewProj = view * proj;
            _modelInteractionController?.Update(viewProj);

            // 3. Setup lighting from view model settings
            float phi = (float)(_viewModel.LightRotation * (Math.PI / 180.0));
            float theta = (float)(_viewModel.LightHeight * (Math.PI / 180.0));

            // Key light direction
            float x = MathF.Cos(theta) * MathF.Sin(phi);
            float y = MathF.Sin(theta);
            float z = MathF.Cos(theta) * MathF.Cos(phi);
            var lightDir1 = new Vector3(-x, -y, -z);

            // Fill light direction
            var lightDir2 = new Vector3(x, y, -z);

            float ambientVal = (float)(_viewModel.AmbientIntensity / 100.0);
            var ambientColor = new Vector3(ambientVal, ambientVal, ambientVal);

            float keyIntensity = 0.0f;
            float fillIntensity = 0.0f;
            if (_viewModel.AmbientIntensity < 95)
            {
                keyIntensity = (float)((100.0 - _viewModel.AmbientIntensity) / 100.0);
                fillIntensity = keyIntensity * 0.5f;
            }

            var lightColor1 = new Vector3(keyIntensity, keyIntensity, keyIntensity);
            var lightColor2 = new Vector3(fillIntensity, fillIntensity, fillIntensity);

            // Render ground before the editor grid so the grid remains a world-space guide.
            if (_groundModel != null && _viewModel.IsGroundVisible && !_viewModel.IsTransparentBg)
            {
                _meshRenderer.Render(_groundModel, viewProj, eye, lightDir1, lightColor1, lightDir2, lightColor2, ambientColor);
            }

            // Render the grid before transparent model passes. Transparent parts do not write
            // depth, so drawing the grid afterward makes it appear over the model.
            if (_gridRenderer != null && _viewModel.IsGridVisible)
            {
                _gridRenderer.Render(viewProj);
            }

            // Render loaded models after the ground and grid.
            foreach (var model in _loadedModels)
            {
                _meshRenderer.Render(model, viewProj, eye, lightDir1, lightColor1, lightDir2, lightColor2, ambientColor);
            }

            // Render skybox if visible
            if (_skyModel != null && _viewModel.ShowSkybox)
            {
                _meshRenderer.Render(_skyModel, viewProj, eye, lightDir1, lightColor1, lightDir2, lightColor2, ambientColor);
            }

            // Render standalone VFX inspection/playback.
            if (_vfxRenderer != null)
            {
                if (_activeSceneModel != null)
                {
                    _vfxRenderer.SetWorldTransform(
                        ViewerInteractionService.CreateWorldMatrix(_activeSceneModel));
                }
                _vfxRenderer.SetViewportSize(framebufferWidth, framebufferHeight);
                if (updateVfx)
                    _vfxRenderer.Update((float)Math.Clamp(frameDelta.TotalSeconds, 0, 0.25));
                _vfxRenderer.Render(viewProj, view);
            }

            // Animation-clip VFX belong to the model that owns the GraphClip. They use the
            // animation clock rather than frameDelta, so snapshots and pauses draw the exact
            // same deterministic state without advancing the simulation.
            foreach ((SceneModel model, VfxRenderSession session) in _clipVfxSessions)
            {
                if (model.CurrentAnimation == null ||
                    !_activeAnimationData.TryGetValue(model, out AnimationData clipData) ||
                    !clipData.IsAuthoredClip)
                {
                    continue;
                }

                session.SetWorldTransform(ViewerInteractionService.CreateWorldMatrix(model));
                session.SetViewportSize(framebufferWidth, framebufferHeight);
                session.Render(viewProj, view);
            }
        }

        private void EnsureSceneRenderers(bool required = false)
        {
            if (_gl == null || (!required && _loadedModels.Count == 0)) return;

            if (_meshRenderer == null)
            {
                _meshRenderer = new GlMeshRenderer();
                _meshRenderer.Initialize(_gl);
            }

            if (_gridRenderer == null && _viewModel.IsGridVisible)
            {
                _gridRenderer = new GridRenderer();
                _gridRenderer.Initialize(_gl, GlShaderCompiler.UsesEmbeddedProfile(_gl), 1000f);
            }
        }

        private void EnsureVfxRenderer()
        {
            if (_vfxRenderer != null || _gl == null || _selectedVfxSystem == null) return;
            EnsureSceneRenderers(required: true);
            var renderer = new VfxRenderSession(LogService, VfxLoadingService);
            renderer.Initialize(_gl);
            _vfxRenderer = renderer;
        }

        private VfxRenderSession EnsureClipVfxSession(SceneModel model)
        {
            if (model == null || _gl == null || VfxLoadingService == null) return null;
            if (_clipVfxSessions.TryGetValue(model, out VfxRenderSession existing)) return existing;

            EnsureSceneRenderers(required: true);
            var session = new VfxRenderSession(LogService, VfxLoadingService);
            session.Initialize(_gl);
            _clipVfxSessions[model] = session;
            return session;
        }

        private CustomCameraController _cameraController;
        private readonly Dictionary<SceneModel, AnimationService> _animationServices = new();
        private readonly Dictionary<SceneModel, VfxRenderSession> _clipVfxSessions = new();
        private readonly Dictionary<SceneModel, AnimationData> _activeAnimationData = new();
        private readonly Dictionary<SceneModel, ClipVisualState> _clipVisualStates = new();
        private readonly System.Diagnostics.Stopwatch _renderStopwatch = new();
        private readonly System.Diagnostics.Stopwatch _fpsStopwatch = new();
        private bool _isCompositionTargetHooked;
        private int _framesSinceFpsUpdate;
        private TimeSpan _lastRenderedAt;
        private TimeSpan _lastInvalidatedAt;
        private TimeSpan _nextLimitedFrame;
        private sealed record SnapshotRequest(string FilePath, int Width, int Height);
        private SnapshotRequest _pendingSnapshot;

        private SceneModel _activeSceneModel;
        private AnimationModel _activeAnimationModel;
        private readonly List<SceneModel> _loadedModels = new();
        private ViewportModelInteractionController _modelInteractionController;
        private bool _isCleanedUp;

        private struct ModelUpdateKey
        {
            public IAnimationAsset Animation;
            public double AnimationTime;
            public int VisiblePartsHash;
            public bool IsVisible;
        }

        private sealed record ClipVisibilityChange(
            double AtSeconds,
            IReadOnlyList<uint> ShowHashes,
            IReadOnlyList<uint> HideHashes);

        private sealed class ClipVisualState
        {
            public AnimationData Animation { get; init; }
            public Dictionary<ModelPart, bool> BaseVisibility { get; init; }
            public IReadOnlyList<ClipVisibilityChange> VisibilityChanges { get; init; }
        }

        private readonly Dictionary<SceneModel, ModelUpdateKey> _lastModelUpdates = new();
        // Environment references
        private ModelVisual3D _skyVisual;
        private ModelVisual3D _groundVisual;
        private SceneModel _groundModel;
        private SceneModel _skyModel;
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

            UpdateToolbarVisibility();
        }

        private void InitializeModelInteraction()
        {
            if (_modelInteractionController != null) return;
            _modelInteractionController = new ViewportModelInteractionController(
                CameraInputSurface,
                TransformGizmoCanvas,
                GizmoXAxis,
                GizmoYAxis,
                GizmoZAxis,
                GizmoOrigin,
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
                    SetGroundVisibility(!_viewModel.IsTransparentBg && _viewModel.IsGroundVisible);
                    break;
                case nameof(ViewerViewportModel.IsGridVisible):
                    if (_viewModel.IsGridVisible)
                        EnsureSceneRenderers();
                    break;
                case nameof(ViewerViewportModel.ShowSkybox):
                    SetSkyboxVisibility(_viewModel.ShowSkybox);
                    break;
            }
        }

        private void ApplyFpsLimitMode()
        {
            if (_isCleanedUp || OpenTkControl == null) return;

            ResetRenderTiming();

            if (_viewModel.LimitFps)
            {
                OpenTkControl.RenderContinuously = false;
                if (!_isCompositionTargetHooked)
                {
                    CompositionTarget.Rendering += OnCompositionTargetRendering;
                    _isCompositionTargetHooked = true;
                }
            }
            else
            {
                if (_isCompositionTargetHooked)
                {
                    CompositionTarget.Rendering -= OnCompositionTargetRendering;
                    _isCompositionTargetHooked = false;
                }
                OpenTkControl.RenderContinuously = true;
            }
        }

        private void OnCompositionTargetRendering(object sender, EventArgs e)
        {
            if (_isCleanedUp || OpenTkControl == null || !OpenTkControl.IsLoaded || !OpenTkControl.IsVisible)
                return;

            TimeSpan now = _renderStopwatch.Elapsed;
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

            if (_isCleanedUp) return;

            var settings = new GLWpfControlSettings
            {
                MajorVersion = 3,
                MinorVersion = 3,
                Profile = OpenTK.Windowing.Common.ContextProfile.Core
            };
            OpenTkControl.Start(settings);

            ApplyFpsLimitMode();
            _fpsStopwatch.Restart();
        }

        private void OnAppSettingsPropertyChanged(object sender, PropertyChangedEventArgs e) =>
            RequestGroundPlaneRefresh();

        private void OnAppSettingsSaved(object sender, EventArgs e) => RequestGroundPlaneRefresh();

        private void RequestGroundPlaneRefresh()
        {
            if (_isCleanedUp || _isMapGeometry ||
                _groundRefreshOperation?.Status == DispatcherOperationStatus.Pending)
            {
                return;
            }

            _groundRefreshOperation = Dispatcher.InvokeAsync(RefreshGroundPlane, DispatcherPriority.Render);
        }

        private void RefreshGroundPlane()
        {
            if (_isCleanedUp || _isMapGeometry) return;

            if (_groundVisual != null && Viewport.Children.Contains(_groundVisual))
                Viewport.Children.Remove(_groundVisual);

            _meshRenderer?.QueueRelease(_groundModel);
            _groundModel?.Dispose();
            _groundVisual = null;
            _groundModel = null;
            SetupScene(false);
        }

        private void OnViewportUnloaded(object sender, RoutedEventArgs e)
        {
            Cleanup();
        }

        private SceneModel BuildSceneModelFromVisual(ModelVisual3D visual, string name)
        {
            if (visual == null) return null;
            var sceneModel = new SceneModel { Name = name, IsVisible = true };

            void ExtractGeometryModels(Model3D model, Transform3D parentTransform)
            {
                Transform3D combined = Transform3D.Identity;
                if (parentTransform != null && parentTransform != Transform3D.Identity)
                {
                    if (model.Transform != null && model.Transform != Transform3D.Identity)
                    {
                        var group = new Transform3DGroup();
                        group.Children.Add(model.Transform);
                        group.Children.Add(parentTransform);
                        combined = group;
                    }
                    else
                    {
                        combined = parentTransform;
                    }
                }
                else if (model.Transform != null && model.Transform != Transform3D.Identity)
                {
                    combined = model.Transform;
                }

                if (model is GeometryModel3D geomModel)
                {
                    if (geomModel.Geometry is MeshGeometry3D mesh)
                    {
                        var transformedMesh = new MeshGeometry3D();
                        transformedMesh.TriangleIndices = mesh.TriangleIndices;
                        transformedMesh.TextureCoordinates = mesh.TextureCoordinates;
                        transformedMesh.Normals = mesh.Normals;

                        foreach (var pos in mesh.Positions)
                        {
                            transformedMesh.Positions.Add(combined.Transform(pos));
                        }

                        var part = new ModelPart(
                            name + "_" + sceneModel.Parts.Count,
                            new GeometryModel3D(transformedMesh, geomModel.Material));

                        if (geomModel.Material is DiffuseMaterial diffuse && diffuse.Brush is ImageBrush imgBrush && imgBrush.ImageSource is BitmapSource bitmap)
                        {
                            string texName = "tex_" + part.Name;
                            part.AllTextures[texName] = bitmap;
                            part.SelectedTextureName = texName;
                            float opacity = (float)Math.Clamp(imgBrush.Opacity, 0.0, 1.0);
                            part.ColorTint = new Vector4(1f, 1f, 1f, opacity);
                            if (opacity < 1f) part.AlphaCutoff = 0f;
                        }

                        sceneModel.AddPart(part);
                    }
                }
                else if (model is Model3DGroup group)
                {
                    foreach (var child in group.Children)
                    {
                        ExtractGeometryModels(child, combined);
                    }
                }
            }

            if (visual.Content != null)
            {
                ExtractGeometryModels(visual.Content, visual.Transform ?? Transform3D.Identity);
            }

            return sceneModel;
        }

        public void SetupScene(bool isMapGeometry)
        {
            _isMapGeometry = isMapGeometry;
            if (_cameraController != null)
                _cameraController.IsMapGroundCollisionEnabled = isMapGeometry;

            if (isMapGeometry)
            {
                _viewModel.IsGridVisible = false;
                if (_skyVisual != null && Viewport.Children.Contains(_skyVisual))
                    Viewport.Children.Remove(_skyVisual);
                if (_groundVisual != null && Viewport.Children.Contains(_groundVisual))
                    Viewport.Children.Remove(_groundVisual);
                _skyVisual = null;
                _groundVisual = null;
                _groundModel = null;
                _skyModel = null;
                return;
            }

            if (_groundVisual == null)
            {
                _groundVisual = SceneElements.CreateGroundPlane(
                    LogService,
                    AppSettings?.CustomGroundLogoPath,
                    AppSettings?.GroundLogoScale ?? 1.0,
                    AppSettings?.GroundLogoOpacity ?? 1.0);
                Viewport.Children.Add(_groundVisual);
            }
            _groundModel = BuildSceneModelFromVisual(_groundVisual, "Ground");

            if (_skyVisual == null)
            {
                _skyVisual = SceneElements.CreateSidePlanes(LogService);
                Viewport.Children.Add(_skyVisual);
            }
            _skyModel = BuildSceneModelFromVisual(_skyVisual, "Skybox");

            // Ensure initial state is applied
            SetGroundVisibility(!_viewModel.IsTransparentBg && _viewModel.IsGroundVisible);
            SetSkyboxVisibility(_viewModel.ShowSkybox);
        }

        public void ApplyStudioParameters()
        {
            StudioParametersSettings studioParameters = AppSettings?.StudioParameters;
            if (studioParameters == null) return;

            _viewModel.IsGroundVisible = studioParameters.GroundVisible;
            _viewModel.IsGridVisible = studioParameters.GridVisible;
            _viewModel.IsTransparentBg = studioParameters.TransparentBackground;
            _viewModel.ShowSkybox = studioParameters.SkyboxVisible && !studioParameters.TransparentBackground;
        }


        public void Cleanup()
        {
            if (_isCleanedUp) return;
            _isCleanedUp = true;
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

                _skyVisual = null;
                _groundVisual = null;

                // Liberar los recursos de renderizado OpenGL de forma aislada
                var meshRenderer = _meshRenderer;
                _meshRenderer = null;
                RunReleaseStep(nameof(GlMeshRenderer), () => meshRenderer?.Dispose(), gpuBound: true);

                var gridRenderer = _gridRenderer;
                _gridRenderer = null;
                RunReleaseStep(nameof(GridRenderer), () => gridRenderer?.Dispose(), gpuBound: true);

                var vfxRenderer = _vfxRenderer;
                _vfxRenderer = null;
                _selectedVfxSystem = null;
                RunReleaseStep(nameof(VfxRenderSession), () => vfxRenderer?.Dispose(), gpuBound: true);

                var gl = _gl;
                _gl = null;
                RunReleaseStep("OpenGL API", () => gl?.Dispose());

                RunReleaseStep(nameof(OpenTkControl), OpenTkControl.Dispose, gpuBound: true);
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
                    AnimationData data = FindMatchingAnimation(model, animationModel.AnimationData);
                    if (data != null)
                        ActivateAnimation(model, data);
                    else if (_activeAnimationData.ContainsKey(model))
                        DeactivateAnimation(model);
                }
            }
            else
            {
                ActivateAnimation(_activeSceneModel, animationModel.AnimationData);
            }

            Panel?.SetAnimationPlayingState(animationModel, true);
        }

        private static AnimationData FindMatchingAnimation(SceneModel model, AnimationData source)
        {
            if (model?.Animations == null || source == null) return null;
            if (source.IsAuthoredClip)
            {
                uint clipHash = source.AuthoredClip.Clip.OwnerPathHash;
                return model.Animations.FirstOrDefault(candidate =>
                    candidate.IsAuthoredClip &&
                    candidate.AuthoredClip.Clip.OwnerPathHash == clipHash);
            }

            return model.Animations.FirstOrDefault(candidate =>
                !candidate.IsAuthoredClip &&
                string.Equals(candidate.Name, source.Name, StringComparison.OrdinalIgnoreCase));
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
                        AnimationData data = FindMatchingAnimation(model, animationModel.AnimationData);
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
                Panel?.SetAnimationPlayingState(animationModel, false);
            }

            SeekAnimation(time);
        }

        private void ActivateAnimation(SceneModel model, AnimationData data)
        {
            if (model == null || data?.AnimationAsset == null) return;

            RestoreClipVisibility(model, removeState: true);
            _activeAnimationData[model] = data;
            model.CurrentAnimation = data.AnimationAsset;
            model.AnimationTime = 0d;
            model.IsAnimationPaused = false;
            _lastModelUpdates.Remove(model);

            AnimationService animationService = GetAnimationServiceForModel(model);
            IReadOnlyList<AnimationJointSnapCue> snaps = data.AuthoredClip?.TimedCues
                ?.OfType<AnimationJointSnapCue>()
                .ToArray() ?? Array.Empty<AnimationJointSnapCue>();
            animationService.SetJointSnapCues(snaps);

            if (data.IsAuthoredClip)
            {
                PrepareClipVisualState(model, data);
                animationService.Update(
                    0f,
                    data.AnimationAsset,
                    model.Skeleton,
                    model.SkinnedMesh,
                    model.Parts,
                    model.Name);
                model.GpuSkinningData = animationService.SkinningData;
                model.SkinningMatrices = animationService.FinalBoneTransforms;
                ConfigureClipVfx(model, data, animationService);
                ApplyClipVisibility(model, 0d);
            }
            else if (_clipVfxSessions.TryGetValue(model, out VfxRenderSession staleSession))
            {
                staleSession.Stop();
            }
        }

        private void ConfigureClipVfx(SceneModel model, AnimationData data, AnimationService animationService)
        {
            AnimationClipVfxContext context = data?.ClipVfxContext;
            AnimationClipCatalogItem clip = data?.AuthoredClip;
            if (context == null || clip == null) return;

            VfxRenderSession session = EnsureClipVfxSession(model);
            if (session == null) return;

            session.SetBoneTransformSampler((time, name, hash) =>
                animationService.TrySampleBoneTransform((float)time, name, hash, out Matrix4x4 sampled)
                    ? sampled
                    : null);

            int seed = unchecked((int)(clip.Clip.OwnerPathHash ^ 0x9e3779b9u));
            session.SetAnimationSession(
                clip.Composition,
                context.IdleEffects,
                context.Systems,
                context.ResourceMap,
                context.SearchDirectory,
                seed,
                clip.Duration,
                context.OwnerSceneContext);
            session.SetWorldTransform(ViewerInteractionService.CreateWorldMatrix(model));
            session.SynchronizeTo(0d);
            UpdateClipBoneAttachments(session, animationService);
        }

        private static void UpdateClipBoneAttachments(VfxRenderSession session, AnimationService animationService)
        {
            session?.UpdateBoneTransforms((name, hash) =>
            {
                if (!string.IsNullOrWhiteSpace(name) &&
                    animationService.TryGetBoneTransform(name, out Matrix4x4 named))
                {
                    return named;
                }
                return animationService.TryGetBoneTransform(hash, out Matrix4x4 hashed)
                    ? hashed
                    : null;
            });
        }

        private void PrepareClipVisualState(SceneModel model, AnimationData data)
        {
            var changes = new List<ClipVisibilityChange>();
            foreach (AnimationSubmeshVisibilityCue cue in data.AuthoredClip.TimedCues.OfType<AnimationSubmeshVisibilityCue>())
            {
                changes.Add(new ClipVisibilityChange(
                    cue.AtSeconds,
                    cue.ShowSubmeshHashes,
                    cue.HideSubmeshHashes));
                if (cue.UntilSeconds.HasValue)
                {
                    changes.Add(new ClipVisibilityChange(
                        cue.UntilSeconds.Value,
                        cue.HideSubmeshHashes,
                        cue.ShowSubmeshHashes));
                }
            }

            _clipVisualStates[model] = new ClipVisualState
            {
                Animation = data,
                BaseVisibility = model.Parts.ToDictionary(part => part, part => part.IsVisible),
                VisibilityChanges = changes.OrderBy(change => change.AtSeconds).ToArray()
            };
        }

        private void ApplyClipVisibility(SceneModel model, double clipTime)
        {
            if (!_clipVisualStates.TryGetValue(model, out ClipVisualState state) ||
                !ReferenceEquals(state.Animation, _activeAnimationData.GetValueOrDefault(model)))
            {
                return;
            }

            var desired = new Dictionary<ModelPart, bool>(state.BaseVisibility);
            var byHash = model.Parts
                .GroupBy(part => Fnv1a.HashLower(part.Name ?? string.Empty))
                .ToDictionary(group => group.Key, group => group.ToArray());

            foreach (ClipVisibilityChange change in state.VisibilityChanges)
            {
                if (change.AtSeconds > clipTime + 1e-9) break;
                foreach (uint hash in change.ShowHashes ?? Array.Empty<uint>())
                {
                    if (byHash.TryGetValue(hash, out ModelPart[] parts))
                        foreach (ModelPart part in parts) desired[part] = true;
                }
                foreach (uint hash in change.HideHashes ?? Array.Empty<uint>())
                {
                    if (byHash.TryGetValue(hash, out ModelPart[] parts))
                        foreach (ModelPart part in parts) desired[part] = false;
                }
            }

            model.ApplyAnimationVisibility(desired);
        }

        private void RestoreClipVisibility(SceneModel model, bool removeState)
        {
            if (model != null && _clipVisualStates.TryGetValue(model, out ClipVisualState state))
            {
                model.ApplyAnimationVisibility(state.BaseVisibility);
                if (removeState) _clipVisualStates.Remove(model);
            }
        }

        public void TogglePauseResume(AnimationModel animationToToggle)
        {
            if (_activeAnimationModel != animationToToggle || _activeSceneModel == null) return;

            bool newPausedState = !_activeSceneModel.IsAnimationPaused;
            if (Panel?.ViewModel.IsAnimationPlaybackSyncEnabled == true)
            {
                foreach (SceneModel model in _loadedModels)
                {
                    if (model.CurrentAnimation != null)
                        model.IsAnimationPaused = newPausedState;
                }
            }
            else
            {
                _activeSceneModel.IsAnimationPaused = newPausedState;
            }

            Panel?.SetAnimationPlayingState(_activeAnimationModel, !newPausedState);
        }

        public void SeekAnimation(TimeSpan time)
        {
            if (_activeSceneModel == null) return;

            void SeekModel(SceneModel model)
            {
                if (model?.CurrentAnimation == null) return;
                double duration = Math.Max(0d, model.CurrentAnimation.Duration);
                model.AnimationTime = duration > 0d
                    ? Math.Clamp(time.TotalSeconds, 0d, duration)
                    : Math.Max(0d, time.TotalSeconds);
                SynchronizeClipAtCurrentTime(model);
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
                Panel?.SetAnimationPlayingState(_activeAnimationModel, false);

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
            RestoreClipVisibility(model, removeState: true);
            if (_animationServices.TryGetValue(model, out AnimationService animationService))
                animationService.SetJointSnapCues(Array.Empty<AnimationJointSnapCue>());
            if (_clipVfxSessions.TryGetValue(model, out VfxRenderSession session))
                session.Stop();
            _activeAnimationData.Remove(model);
            _lastModelUpdates.Remove(model);
            model.CurrentAnimation = null;
            model.AnimationTime = 0d;
            model.IsAnimationPaused = true;
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

            foreach (VfxRenderSession session in _clipVfxSessions.Values)
                RunReleaseStep(nameof(VfxRenderSession), session.Dispose, gpuBound: true);
            _clipVfxSessions.Clear();
            _activeAnimationData.Clear();
            _clipVisualStates.Clear();

            foreach (var model in _loadedModels)
            {
                _meshRenderer?.QueueRelease(model);
                if (Viewport.Children.Contains(model.RootVisual))
                    Viewport.Children.Remove(model.RootVisual);
                model.PropertyChanged -= Model_PropertyChanged;
                model.Dispose();
            }
            _loadedModels.Clear();
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

        public void AddModel(SceneModel model)
        {
            _loadedModels.Add(model);
            EnsureSceneRenderers();
            if (model.IsVisible)
            {
                if (!Viewport.Children.Contains(model.RootVisual))
                    Viewport.Children.Add(model.RootVisual);
            }

            model.PropertyChanged += Model_PropertyChanged;
            SetActiveModel(model);
            _viewModel.UpdateSceneDisplay(_loadedModels.Count, _loadedModels.Count > 0 ? _loadedModels[0].Name : null);
        }

        public void ClearModels()
        {
            var modelsToClear = _loadedModels.ToList();
            foreach (var model in modelsToClear)
            {
                RemoveModel(model);
            }
        }

        public void RemoveModel(SceneModel model)
        {
            bool removingActiveModel = model == _activeSceneModel;
            if (removingActiveModel)
            {
                _activeSceneModel = null;
            }

            model.PropertyChanged -= Model_PropertyChanged;
            RestoreClipVisibility(model, removeState: true);
            _activeAnimationData.Remove(model);
            if (_clipVfxSessions.Remove(model, out VfxRenderSession clipSession))
                RunReleaseStep(nameof(VfxRenderSession), clipSession.Dispose, gpuBound: true);
            _loadedModels.Remove(model);
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
            _viewModel.UpdateSceneDisplay(_loadedModels.Count, _loadedModels.Count > 0 ? _loadedModels[0].Name : null);
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

        public void SelectVfxSystem(VfxSystemModel vfxSystem)
        {
            _selectedVfxSystem = vfxSystem;
            EnsureVfxRenderer();
            _vfxRenderer?.SetVfxSystem(vfxSystem);
        }

        public void PlayVfx() => _vfxRenderer?.Play();

        public void PauseVfx() => _vfxRenderer?.Pause();

        public void StopVfx() => _vfxRenderer?.Stop();

        public void SeekVfx(TimeSpan time) => _vfxRenderer?.Seek(time.TotalSeconds);

        private void SynchronizeClipAtCurrentTime(SceneModel model)
        {
            if (model == null ||
                !_activeAnimationData.TryGetValue(model, out AnimationData data) ||
                !data.IsAuthoredClip)
            {
                return;
            }

            double playbackTime = FoldClipTime(model.AnimationTime, data.AnimationAsset?.Duration ?? 0f);
            ApplyClipVisibility(model, playbackTime);
            if (!_clipVfxSessions.TryGetValue(model, out VfxRenderSession session)) return;

            AnimationService animationService = GetAnimationServiceForModel(model);
            session.SetWorldTransform(ViewerInteractionService.CreateWorldMatrix(model));
            session.SynchronizeTo(playbackTime);
            UpdateClipBoneAttachments(session, animationService);
        }

        private static double FoldClipTime(double time, double duration)
        {
            if (!(duration > 0d) || !double.IsFinite(time)) return 0d;
            double folded = time % duration;
            return folded < 0d ? folded + duration : folded;
        }

        private void UpdateScene(TimeSpan frameDelta)
        {
            double deltaTime = Math.Clamp(frameDelta.TotalSeconds, 0, 0.25);

            if (_viewModel.IsAutoRotateActive && _activeSceneModel != null)
                _activeSceneModel.RotationY = (_activeSceneModel.RotationY + 30.0 * deltaTime) % 360;

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

                AnimationData data = _activeAnimationData.GetValueOrDefault(model);
                double playbackTime = data?.IsAuthoredClip == true
                    ? FoldClipTime(model.AnimationTime, model.CurrentAnimation.Duration)
                    : model.AnimationTime;

                if (data?.IsAuthoredClip == true)
                    ApplyClipVisibility(model, playbackTime);

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
                if (needsUpdate)
                {
                    _lastModelUpdates[model] = currentKey;
                    animationService.Update(
                        (float)playbackTime,
                        model.CurrentAnimation,
                        model.Skeleton,
                        model.SkinnedMesh,
                        model.Parts,
                        model.Name);
                    model.GpuSkinningData = animationService.SkinningData;
                    model.SkinningMatrices = animationService.FinalBoneTransforms;
                }

                if (data?.IsAuthoredClip == true &&
                    _clipVfxSessions.TryGetValue(model, out VfxRenderSession session))
                {
                    session.SetWorldTransform(ViewerInteractionService.CreateWorldMatrix(model));
                    session.SynchronizeTo(playbackTime);
                    UpdateClipBoneAttachments(session, animationService);
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
                ? FoldClipTime(next, duration)
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
            bool isMap = _isMapGeometry;

            Point3D position;
            Vector3D lookDirection;
            Vector3D upDirection = new Vector3D(0.00, 1.00, 0.00);

            if (TryGetModelBounds(isMap, out var center, out var maxDim, out var horizontalDim))
            {
                double distance = isMap ? horizontalDim * 0.18 : maxDim * 1.25;
                if (distance < 50) distance = 250;

                double heightFactor = isMap ? 1.30 : 0.15;
                double horizontalAngle = isMap ? Math.PI * 0.75 : Math.PI / 2;

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
            position = isMap ? new Point3D(0.00, 1386.00, 670.00) : new Point3D(0.00, 1118.00, 250.00);
            lookDirection = isMap ? new Vector3D(0.00, -250.00, -650.00) : new Vector3D(0.00, -38.00, -250.00);

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

        internal static float CalculateProjectionNearPlane(
            Vector3 lookDirection,
            bool isMapGeometry = false)
        {
            float cameraDistance = lookDirection.Length();
            if (!float.IsFinite(cameraDistance) || cameraDistance <= 0f)
                return isMapGeometry ? 0.01f : 1f;

            return isMapGeometry
                ? Math.Clamp(cameraDistance * 0.001f, 0.01f, 2.5f)
                : Math.Clamp(cameraDistance * 0.01f, 0.1f, 500f);
        }

        internal static float CalculateProjectionFarPlane(Vector3 lookDirection)
        {
            float cameraDistance = lookDirection.Length();
            if (!float.IsFinite(cameraDistance) || cameraDistance <= 0f)
            {
                return 100000f;
            }

            return Math.Max(100000f, cameraDistance * 4f);
        }

        private void SetCameraView_Click(object sender, RoutedEventArgs e)
        {
            if (_cameraController == null || sender is not Button btn || btn.Tag is not string viewType) return;

            bool isMap = _isMapGeometry;

            // Compute dynamic target center and distance if model is available
            double baselineY = 1000;
            Point3D targetPoint = new Point3D(0, 90.00 + baselineY, 0);
            double distance = 300.00;

            if (TryGetModelBounds(isMap, out var center, out var maxDim, out var horizontalDim))
            {
                targetPoint = center;
                double framingDim = isMap ? horizontalDim : maxDim;
                distance = (isMap ? 1.5 : 1.25) * framingDim;
                if (distance < 50) distance = 250;
            }

            var pose = CalculateCameraView(viewType, targetPoint, distance);
            if (pose == null) return;

            _cameraController.SnapTo(
                pose.Value.Position,
                pose.Value.LookDirection,
                pose.Value.UpDirection);
        }

        internal static (
            Point3D Position,
            Vector3D LookDirection,
            Vector3D UpDirection)? CalculateCameraView(
                string viewType,
                Point3D target,
                double distance)
        {
            if (!double.IsFinite(distance) || distance <= 0)
            {
                return null;
            }

            Vector3D worldUp = new Vector3D(0, 1, 0);
            return viewType switch
            {
                "Front" => (
                    target + new Vector3D(0, 0, distance),
                    new Vector3D(0, 0, -distance),
                    worldUp),
                "Back" => (
                    target + new Vector3D(0, 0, -distance),
                    new Vector3D(0, 0, distance),
                    worldUp),
                "Left" => (
                    target + new Vector3D(-distance, 0, 0),
                    new Vector3D(distance, 0, 0),
                    worldUp),
                "Right" => (
                    target + new Vector3D(distance, 0, 0),
                    new Vector3D(-distance, 0, 0),
                    worldUp),
                "Top" => (
                    target + new Vector3D(0, distance, 0),
                    new Vector3D(0, -distance, 0),
                    new Vector3D(0, 0, -1)),
                "Bottom" => (
                    target + new Vector3D(0, -distance, 0),
                    new Vector3D(0, distance, 0),
                    new Vector3D(0, 0, 1)),
                _ => null
            };
        }

        private bool TryGetModelBounds(
            bool isMapGeometry,
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
                var playableBounds = Rect3D.Empty;
                foreach (var part in _activeSceneModel.Parts)
                {
                    if (part.Geometry?.Geometry is MeshGeometry3D mesh)
                    {
                        bounds.Union(mesh.Bounds);
                        if (isMapGeometry && part.Name?.StartsWith("LM_", StringComparison.OrdinalIgnoreCase) == true)
                            playableBounds.Union(mesh.Bounds);
                    }
                }

                if (!bounds.IsEmpty)
                {
                    Rect3D focusBounds = isMapGeometry && !playableBounds.IsEmpty ? playableBounds : bounds;
                    double centerX = focusBounds.X + focusBounds.SizeX / 2 + _activeSceneModel.PositionX;
                    double focusHeight = isMapGeometry ? 0.85 : 0.5;
                    double centerY = focusBounds.Y + focusBounds.SizeY * focusHeight + _activeSceneModel.PositionY;
                    double centerZ = focusBounds.Z + focusBounds.SizeZ / 2 + _activeSceneModel.PositionZ;
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

        public void SetSkyboxVisibility(bool isVisible)
        {
            if (_skyVisual == null) return;

            if (isVisible && !Viewport.Children.Contains(_skyVisual))
            {
                Viewport.Children.Add(_skyVisual);
            }
            else if (!isVisible && Viewport.Children.Contains(_skyVisual))
            {
                Viewport.Children.Remove(_skyVisual);
            }
        }

        public void SetGroundVisibility(bool isVisible)
        {
            if (_groundVisual == null) return;

            if (isVisible && !Viewport.Children.Contains(_groundVisual))
            {
                Viewport.Children.Add(_groundVisual);
            }
            else if (!isVisible && Viewport.Children.Contains(_groundVisual))
            {
                Viewport.Children.Remove(_groundVisual);
            }
        }

        private void ProcessPendingSnapshot()
        {
            SnapshotRequest request = _pendingSnapshot;
            if (request == null)
                return;

            _pendingSnapshot = null;

            try
            {
                BitmapSource snapshot = _snapshotService.Capture(
                    _gl,
                    request.Width,
                    request.Height,
                    OpenTkControl.FrameBufferWidth,
                    OpenTkControl.FrameBufferHeight,
                    () => RenderScene(request.Width, request.Height, TimeSpan.Zero, updateVfx: false));
                _ = SaveSnapshotAsync(snapshot, request.FilePath);
            }
            catch (Exception ex)
            {
                LogService.LogError(ex, $"Failed to render high-definition snapshot to {request.FilePath}");
            }
        }

        private async Task SaveSnapshotAsync(BitmapSource snapshot, string filePath)
        {
            try
            {
                await _snapshotService.SaveAsync(snapshot, filePath);
                LogService.LogInteractiveSuccess(
                    $"Snapshot saved ({snapshot.PixelWidth}x{snapshot.PixelHeight})",
                    filePath,
                    Path.GetFileName(filePath));
            }
            catch (Exception ex)
            {
                LogService.LogError(ex, $"Failed to save high-definition snapshot to {filePath}");
            }
        }

        private bool TryGetSnapshotSize(out int width, out int height)
        {
            width = OpenTkControl.FrameBufferWidth;
            height = OpenTkControl.FrameBufferHeight;
            return OpenTkControl.IsVisible && width > 0 && height > 0;
        }

        public void InitiateHighDefinitionSnapshot()
        {
            if (!TryGetSnapshotSize(out _, out _))
            {
                LogService.LogWarning("The OpenGL viewport is not ready for high-definition capture.");
                return;
            }

            if (_activeSceneModel == null || string.IsNullOrEmpty(_activeSceneModel.Name))
            {
                LogService.LogWarning("No model loaded to name the screenshot automatically. Using default name.");
            }

            string modelName = _activeSceneModel?.Name ?? "Model";
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string defaultFileName = $"{modelName}_{timestamp}.png";
            var saveFileDialog = new SaveFileDialog
            {
                FileName = defaultFileName,
                Filter = "PNG Image (*.png)|*.png|All Files (*.*)|*.*",
                Title = "Save Viewport Snapshot",
                DefaultExt = "png"
            };

            if (saveFileDialog.ShowDialog() == true)
            {
                string filePath = saveFileDialog.FileName;
                (int width, int height) = OpenGlSnapshotService.CalculateUhdSize(
                    OpenTkControl.FrameBufferWidth,
                    OpenTkControl.FrameBufferHeight);
                ImageExportUtils.ValidateDimensions(width, height);
                _pendingSnapshot = new SnapshotRequest(filePath, width, height);
            }
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
