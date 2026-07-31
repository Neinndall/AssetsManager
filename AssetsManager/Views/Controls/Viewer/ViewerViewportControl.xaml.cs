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
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Services.Viewer.Vfx;
using AssetsManager.Utils;
using AssetsManager.Utils.Rendering;
using AssetsManager.Views.Models.Viewer;
using AssetsManager.Views.Helpers;
using Microsoft.WindowsAPICodePack.Dialogs;
using System.Windows;
using OpenTK.Wpf;
using System.Numerics;

namespace AssetsManager.Views.Controls.Viewer
{
    public partial class ViewerViewportControl : UserControl, IDisposable
    {
        private Silk.NET.OpenGL.GL _gl;
        private GlMeshRenderer _meshRenderer;
        private GlVfxRenderer _vfxRenderer;
        private GridRenderer _gridRenderer;

        private readonly ViewerViewportModel _viewModel;
        public ViewerViewportModel ViewModel => _viewModel;

        private readonly OpenGlSnapshotService _snapshotService = new();
        private readonly Viewport3D _dummyViewport = new Viewport3D
        {
            Camera = new PerspectiveCamera(new Point3D(0, 1118, 250), new Vector3D(0, -38, -250), new Vector3D(0, 1, 0), 45)
        };
        public Viewport3D Viewport3D => _dummyViewport;
        public Viewport3D Viewport => Viewport3D;

        private readonly AmbientLight GlobalAmbientLight = new AmbientLight();
        private readonly DirectionalLight StudioLight = new DirectionalLight();
        private readonly DirectionalLight FillLight = new DirectionalLight();
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
                _meshRenderer = new GlMeshRenderer();
                _meshRenderer.Initialize(_gl);

                _vfxRenderer = new GlVfxRenderer(LogService);
                _vfxRenderer.Initialize(_gl);

                _gridRenderer = new GridRenderer();
                _gridRenderer.Initialize(_gl, GlShaderCompiler.UsesEmbeddedProfile(_gl), 1000f);
            }
            catch (Exception ex)
            {
                LogService?.LogError(ex, "Failed to initialize Silk.NET OpenGL context.");
            }
        }

        private void OpenTkControl_Render(TimeSpan delta)
        {
            if (_gl == null || _meshRenderer == null) return;
            int framebufferWidth = OpenTkControl.FrameBufferWidth;
            int framebufferHeight = OpenTkControl.FrameBufferHeight;
            if (framebufferWidth <= 0 || framebufferHeight <= 0) return;

            TimeSpan renderTime = _renderStopwatch.Elapsed;
            TimeSpan frameDelta = _lastRenderedAt == TimeSpan.Zero
                ? delta
                : renderTime - _lastRenderedAt;

            if (_viewModel.LimitFps)
            {
                TimeSpan targetFrameTime = TimeSpan.FromSeconds(1.0 / 60.0);
                if (_nextLimitedFrame != TimeSpan.Zero && renderTime < _nextLimitedFrame)
                    return;

                _nextLimitedFrame = _nextLimitedFrame == TimeSpan.Zero ||
                                    renderTime - _nextLimitedFrame > targetFrameTime * 4
                    ? renderTime + targetFrameTime
                    : _nextLimitedFrame + targetFrameTime;
            }
            else
            {
                _nextLimitedFrame = TimeSpan.Zero;
            }

            _lastRenderedAt = renderTime;
            UpdateScene(frameDelta);
            RenderScene(framebufferWidth, framebufferHeight, frameDelta, updateVfx: true);
            RecordRenderedFrame();
            ProcessPendingSnapshot();
        }

        private void RenderScene(int framebufferWidth, int framebufferHeight, TimeSpan frameDelta, bool updateVfx)
        {
            _meshRenderer.ProcessPendingReleases();
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
                10f,
                CalculateProjectionFarPlane(lookDir));
            var viewProj = view * proj;
            _modelInteractionController?.Update(viewProj);

            // 3. Setup lighting from view model settings
            float phi = (float)(_viewModel.LightRotation * (Math.PI / 180.0));
            float theta = (float)(_viewModel.LightHeight * (Math.PI / 180.0));

            // Key Light (StudioLight)
            float x = MathF.Cos(theta) * MathF.Sin(phi);
            float y = MathF.Sin(theta);
            float z = MathF.Cos(theta) * MathF.Cos(phi);
            var lightDir1 = new Vector3(-x, -y, -z);

            // Fill Light (FillLight - opposite)
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

            // 4. Render loaded models
            foreach (var model in _loadedModels)
            {
                _meshRenderer.Render(model, viewProj, lightDir1, lightColor1, lightDir2, lightColor2, ambientColor);
            }

            // Render ground if visible
            if (_groundModel != null && _viewModel.IsGroundVisible && !_viewModel.IsTransparentBg)
            {
                _meshRenderer.Render(_groundModel, viewProj, lightDir1, lightColor1, lightDir2, lightColor2, ambientColor);
            }

            // Render 3D Ground Grid if visible
            if (_gridRenderer != null && _viewModel.IsGridVisible)
            {
                _gridRenderer.Render(viewProj);
            }

            // Render skybox if visible
            if (_skyModel != null && _viewModel.ShowSkybox)
            {
                _meshRenderer.Render(_skyModel, viewProj, lightDir1, lightColor1, lightDir2, lightColor2, ambientColor);
            }

            // Render active VFX particles
            if (_vfxRenderer != null)
            {
                if (_activeSceneModel != null)
                {
                    _vfxRenderer.SetWorldTransform(
                        GlVfxRenderer.CreateSceneWorldTransform(_activeSceneModel));
                }
                _vfxRenderer.SetAttachedMeshSource(_activeSceneModel);
                _vfxRenderer.SetViewportSize(framebufferWidth, framebufferHeight);
                if (updateVfx)
                    _vfxRenderer.Update((float)Math.Clamp(frameDelta.TotalSeconds, 0, 0.25));
                _vfxRenderer.Render(viewProj, view);
            }
        }

        private CustomCameraController _cameraController;
        private readonly Dictionary<SceneModel, AnimationPlayer> _modelPlayers = new();
        private readonly System.Diagnostics.Stopwatch _renderStopwatch = new();
        private readonly System.Diagnostics.Stopwatch _fpsStopwatch = new();
        private int _framesSinceFpsUpdate;
        private TimeSpan _lastRenderedAt;
        private TimeSpan _nextLimitedFrame;
        private sealed record SnapshotRequest(string FilePath, int Width, int Height);
        private SnapshotRequest _pendingSnapshot;

        private readonly RotateTransform3D _autoRotation = new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), 0));

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

        private readonly Dictionary<SceneModel, ModelUpdateKey> _lastModelUpdates = new();
        // Environment references
        private ModelVisual3D _skyVisual;
        private ModelVisual3D _groundVisual;
        private SceneModel _groundModel;
        private SceneModel _skyModel;

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
                    ResetRenderTiming();
                    break;
                case nameof(ViewerViewportModel.IsFpsVisible):
                    _fpsStopwatch.Restart();
                    _framesSinceFpsUpdate = 0;
                    if (!_viewModel.IsFpsVisible)
                        _viewModel.DisplayFps = "0";
                    break;
                case nameof(ViewerViewportModel.IsAutoRotateActive):
                    HandleAutoRotateChanged(_viewModel.IsAutoRotateActive);
                    break;
                case nameof(ViewerViewportModel.AmbientIntensity):
                case nameof(ViewerViewportModel.LightRotation):
                case nameof(ViewerViewportModel.LightHeight):
                    UpdateStudioLighting();
                    break;
                case nameof(ViewerViewportModel.FieldOfView):
                    UpdateFieldOfView();
                    break;
                case nameof(ViewerViewportModel.IsTransparentBg):
                case nameof(ViewerViewportModel.IsGroundVisible):
                    SetGroundVisibility(!_viewModel.IsTransparentBg && _viewModel.IsGroundVisible);
                    break;
                case nameof(ViewerViewportModel.ShowSkybox):
                    SetSkyboxVisibility(_viewModel.ShowSkybox);
                    break;
            }
        }

        private void OnViewportLoaded(object sender, RoutedEventArgs e)
        {
            _isCleanedUp = false;
            _modelPlayers.Clear();
            InitializeModelInteraction();
            _cameraController = new CustomCameraController(Viewport3D, CameraInputSurface);

            var settings = new GLWpfControlSettings
            {
                MajorVersion = 3,
                MinorVersion = 3,
                Profile = OpenTK.Windowing.Common.ContextProfile.Core
            };
            OpenTkControl.Start(settings);

            ResetRenderTiming();
            _fpsStopwatch.Restart();
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
                            new GeometryModel3D(transformedMesh, geomModel.Material))
                        {
                            IsVisible = true
                        };

                        if (geomModel.Material is DiffuseMaterial diffuse && diffuse.Brush is ImageBrush imgBrush && imgBrush.ImageSource is BitmapSource bitmap)
                        {
                            string texName = "tex_" + part.Name;
                            part.AllTextures[texName] = bitmap;
                            part.SelectedTextureName = texName;
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
                _viewModel.PropertyChanged -= OnViewportViewModelPropertyChanged;

                // 2. Limpiar escena y animaciones
                ResetScene();

                // Limpiar todo el viewport
                Viewport.Children.Clear();

                // 6. Liberar los AnimationPlayers y todos sus buffers cacheados
                foreach (var player in _modelPlayers.Values)
                {
                    player.Dispose();
                }
                _modelPlayers.Clear();

                // 7. Liberar el CameraController (dueño único)
                _cameraController?.Dispose();
                _cameraController = null;

                _skyVisual = null;
                _groundVisual = null;
                // Liberar el renderizador de OpenGL
                _meshRenderer?.Dispose();
                _meshRenderer = null;

                _gridRenderer?.Dispose();
                _gridRenderer = null;

                _vfxRenderer?.Dispose();
                _vfxRenderer = null;

            }
            catch (Exception ex)
            {
                LogService.LogError(ex, "Error during ViewerViewportControl.Cleanup");
            }
        }

        public void Dispose()
        {
            Cleanup();
        }

        public void SetAnimation(AnimationModel animationModel)
        {
            if (_activeSceneModel == null) return;

            _activeAnimationModel = animationModel;

            if (Panel?.ViewModel.IsAnimationPlaybackSyncEnabled == true)
            {
                foreach (var model in _loadedModels)
                {
                    var animData = model.Animations.FirstOrDefault(a => a.Name == animationModel.Name);
                    if (animData != null)
                    {
                        model.CurrentAnimation = animData.AnimationAsset;
                        model.AnimationTime = 0;
                        model.IsAnimationPaused = false;
                    }
                }
            }
            else
            {
                _activeSceneModel.CurrentAnimation = animationModel.AnimationData.AnimationAsset;
                _activeSceneModel.AnimationTime = 0;
                _activeSceneModel.IsAnimationPaused = false;
            }

            Panel?.SetAnimationPlayingState(animationModel, true);
        }

        public void TogglePauseResume(AnimationModel animationToToggle)
        {
            if (_activeAnimationModel != animationToToggle) return;

            bool newPausedState = !_activeSceneModel.IsAnimationPaused;

            if (Panel?.ViewModel.IsAnimationPlaybackSyncEnabled == true)
            {
                foreach (var model in _loadedModels)
                {
                    if (model.CurrentAnimation != null)
                    {
                        model.IsAnimationPaused = newPausedState;
                    }
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

            if (Panel?.ViewModel.IsAnimationPlaybackSyncEnabled == true)
            {
                foreach (var model in _loadedModels)
                {
                    if (model.CurrentAnimation != null)
                    {
                        model.AnimationTime = time.TotalSeconds;
                    }
                }
            }
            else
            {
                _activeSceneModel.AnimationTime = time.TotalSeconds;
            }
        }

        public void StopAnimation()
        {
            if (_activeSceneModel == null || _activeAnimationModel == null) return;

            if (_activeSceneModel.CurrentAnimation != null)
            {
                Panel?.SetAnimationPlayingState(_activeAnimationModel, false);
            }

            if (Panel?.ViewModel.IsAnimationPlaybackSyncEnabled == true)
            {
                foreach (var model in _loadedModels)
                {
                    model.CurrentAnimation = null;
                    model.AnimationTime = 0;
                    model.IsAnimationPaused = true;
                }
            }
            else
            {
                _activeSceneModel.CurrentAnimation = null;
                _activeSceneModel.AnimationTime = 0;
                _activeSceneModel.IsAnimationPaused = true;
            }

            _activeAnimationModel = null;
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

            // RESET LIGHTING TO 'NORMAL' MODE (Como antes)
            if (GlobalAmbientLight != null) GlobalAmbientLight.Color = Colors.White;
            if (StudioLight != null) StudioLight.Color = Colors.Black;
            if (FillLight != null) FillLight.Color = Colors.Black;

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
            ((AxisAngleRotation3D)_autoRotation.Rotation).Angle = 0;

            // CRITICAL: Free cached vertex/skin buffers from the previous model so
            // the next load does not retain RAM of a model that is no longer in use.
            foreach (var player in _modelPlayers.Values)
            {
                player.Dispose();
            }
            _modelPlayers.Clear();
            _lastModelUpdates.Clear();

            _viewModel.UpdateSceneDisplay(_loadedModels.Count, _loadedModels.Count > 0 ? _loadedModels[0].Name : null);
        }

        public void AddModel(SceneModel model)
        {
            _loadedModels.Add(model);
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
                if (_viewModel.IsAutoRotateActive)
                {
                    var transformGroup = model.RootVisual.Transform as Transform3DGroup;
                    if (transformGroup != null && transformGroup.Children.Contains(_autoRotation))
                    {
                        transformGroup.Children.Remove(_autoRotation);
                    }
                }
                _activeSceneModel = null;
            }

            model.PropertyChanged -= Model_PropertyChanged;
            _loadedModels.Remove(model);
            _lastModelUpdates.Remove(model);
            _meshRenderer?.QueueRelease(model);
            if (_modelPlayers.TryGetValue(model, out var player))
            {
                player.Dispose();
                _modelPlayers.Remove(model);
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
            if (_viewModel.IsAutoRotateActive && _activeSceneModel != null)
            {
                var transformGroup = _activeSceneModel.RootVisual.Transform as Transform3DGroup;
                if (transformGroup != null && transformGroup.Children.Contains(_autoRotation))
                {
                    double accumulatedAngle = ((AxisAngleRotation3D)_autoRotation.Rotation).Angle;
                    _activeSceneModel.RotationY = (_activeSceneModel.RotationY + accumulatedAngle) % 360;
                    transformGroup.Children.Remove(_autoRotation);
                    ((AxisAngleRotation3D)_autoRotation.Rotation).Angle = 0;
                }
            }

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
            if (_vfxRenderer == null) return;
            _vfxRenderer.SetVfxSystem(vfxSystem);
            LogService?.LogDebug($"[VFX] Selected VFX system '{vfxSystem?.Name ?? "none"}'.");
        }

        public void PlayVfx() => _vfxRenderer?.Play();

        public void PauseVfx() => _vfxRenderer?.Pause();

        public void StopVfx() => _vfxRenderer?.Stop();

        public void SeekVfx(TimeSpan time) => _vfxRenderer?.Seek(time.TotalSeconds);

        private void UpdateScene(TimeSpan frameDelta)
        {
            double deltaTime = Math.Clamp(frameDelta.TotalSeconds, 0, 0.25);

            if (_viewModel.IsAutoRotateActive && _activeSceneModel != null)
            {
                var transform = _activeSceneModel.RootVisual.Transform;
                Transform3DGroup transformGroup;

                if (transform == null || transform == Transform3D.Identity)
                {
                    transformGroup = new Transform3DGroup();
                    _activeSceneModel.RootVisual.Transform = transformGroup;
                }
                else
                {
                    transformGroup = transform as Transform3DGroup;
                }

                if (transformGroup != null)
                {
                    if (!transformGroup.Children.Contains(_autoRotation))
                    {
                        transformGroup.Children.Add(_autoRotation);
                    }
                    double rotationSpeed = 30.0 * deltaTime;
                    ((AxisAngleRotation3D)_autoRotation.Rotation).Angle = (((AxisAngleRotation3D)_autoRotation.Rotation).Angle + rotationSpeed) % 360;
                }
            }

            if (_loadedModels.Count > 0)
            {
                // Synchronize playback timing across all models if enabled (v3.2.3.2)
                // IMPORTANT: Only sync if the master model actually has an animation to sync from.
                bool isPlaybackSync = Panel?.ViewModel.IsAnimationPlaybackSyncEnabled == true &&
                                     _activeSceneModel != null &&
                                     _activeSceneModel.CurrentAnimation != null;

                double masterTime = _activeSceneModel?.AnimationTime ?? 0;
                double speed = _activeAnimationModel?.Speed ?? 1.0;

                for (int i = 0; i < _loadedModels.Count; i++)
                {
                    var model = _loadedModels[i];
                    if (model.CurrentAnimation != null && model.Skeleton != null && model.SkinnedMesh != null)
                    {
                        if (isPlaybackSync && model != _activeSceneModel)
                        {
                            model.AnimationTime = masterTime;
                            model.IsAnimationPaused = _activeSceneModel.IsAnimationPaused;
                        }
                        else if (!model.IsAnimationPaused)
                        {
                            double oldTime = model.AnimationTime;
                            model.AnimationTime += deltaTime * speed;

                            var duration = model.CurrentAnimation.Duration;
                            if (duration > 0 && model.AnimationTime >= duration)
                            {
                                model.AnimationTime = 0;
                            }
                        }

                        bool isActive = model == _activeSceneModel;

                        // Optimize: skip Parallel Skinning (CPU/Memory intensive) if the model is static/paused
                        // and has already been rendered at this exact frame state.
                        int visiblePartsHash = model.Parts?.Sum(p => p.IsVisible ? 1 : 0) ?? 0;
                        var currentKey = new ModelUpdateKey
                        {
                            Animation = model.CurrentAnimation,
                            AnimationTime = model.AnimationTime,
                            VisiblePartsHash = visiblePartsHash,
                            IsVisible = model.IsVisible
                        };

                        bool needsUpdate = true;
                        if (_lastModelUpdates.TryGetValue(model, out var lastKey))
                        {
                            if (lastKey.Animation == currentKey.Animation &&
                                Math.Abs(lastKey.AnimationTime - currentKey.AnimationTime) < 0.0001 &&
                                lastKey.VisiblePartsHash == currentKey.VisiblePartsHash &&
                                lastKey.IsVisible == currentKey.IsVisible)
                            {
                                needsUpdate = false;
                            }
                        }

                        if (needsUpdate)
                        {
                            _lastModelUpdates[model] = currentKey;
                            var player = GetPlayerForModel(model);
                            player.Update(
                                (float)model.AnimationTime,
                                model.CurrentAnimation,
                                model.Skeleton,
                                model.SkinnedMesh,
                                model.Parts,
                                model.Name
                            );
                        }
                    }

                }

                if (_activeSceneModel != null && _activeSceneModel.CurrentAnimation != null)
                {
                    Panel?.UpdateAnimationProgress(_activeSceneModel.AnimationTime);
                }
            }

        }

        private void ResetRenderTiming()
        {
            _lastRenderedAt = TimeSpan.Zero;
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

        private AnimationPlayer GetPlayerForModel(SceneModel model)
        {
            if (!_modelPlayers.TryGetValue(model, out var player))
            {
                player = new AnimationPlayer(LogService);
                _modelPlayers[model] = player;
            }
            return player;
        }

        public void ResetCamera(bool smooth = true)
        {
            bool isMap = Panel?.ViewModel?.IsMapMode == true;

            Point3D position;
            Vector3D lookDirection;
            Vector3D upDirection = new Vector3D(0.00, 1.00, 0.00);

            if (TryGetModelBounds(out var center, out var maxDim))
            {
                double distance = isMap ? maxDim * 0.55 : maxDim * 1.25;
                if (distance < 50) distance = 250;

                double heightFactor = isMap ? 1.2 : 0.15;
                double depthFactor = isMap ? 1.0 : 1.0;

                position = new Point3D(center.X, center.Y + distance * heightFactor, center.Z + distance * depthFactor);
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

        internal static float CalculateProjectionFarPlane(Vector3 lookDirection)
        {
            float cameraDistance = lookDirection.Length();
            if (!float.IsFinite(cameraDistance) || cameraDistance <= 0f)
            {
                return 10000f;
            }

            return Math.Max(10000f, cameraDistance * 4f);
        }

        private void SetCameraView_Click(object sender, RoutedEventArgs e)
        {
            if (_cameraController == null || sender is not Button btn || btn.Tag is not string viewType) return;

            // Compute dynamic target center and distance if model is available
            double baselineY = 1000;
            Point3D targetPoint = new Point3D(0, 90.00 + baselineY, 0);
            double distance = 300.00;

            if (TryGetModelBounds(out var center, out var maxDim))
            {
                targetPoint = center;
                distance = (Panel?.ViewModel?.IsMapMode == true ? 1.5 : 1.25) * maxDim;
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

        private bool TryGetModelBounds(out Point3D center, out double maxDim)
        {
            center = new Point3D();
            maxDim = 0;

            if (_activeSceneModel?.Parts?.Count > 0)
            {
                var bounds = Rect3D.Empty;
                foreach (var part in _activeSceneModel.Parts)
                {
                    if (part.Geometry?.Geometry is MeshGeometry3D mesh)
                    {
                        bounds.Union(mesh.Bounds);
                    }
                }

                if (!bounds.IsEmpty)
                {
                    double centerX = bounds.X + bounds.SizeX / 2 + _activeSceneModel.PositionX;
                    double centerY = bounds.Y + bounds.SizeY / 2 + _activeSceneModel.PositionY;
                    double centerZ = bounds.Z + bounds.SizeZ / 2 + _activeSceneModel.PositionZ;
                    center = new Point3D(centerX, centerY, centerZ);

                    maxDim = Math.Max(bounds.SizeX, Math.Max(bounds.SizeY, bounds.SizeZ));
                    if (maxDim <= 0) maxDim = 150;
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

        private void UpdateStudioLighting()
        {
            if (GlobalAmbientLight == null || StudioLight == null || FillLight == null) return;

            // 1. Set Ambient Color
            byte ambVal = (byte)(255 * (_viewModel.AmbientIntensity / 100.0));
            GlobalAmbientLight.Color = Color.FromRgb(ambVal, ambVal, ambVal);

            // 2. Set Studio Lights Intensity (Inverse of Ambient)
            double studioFactor = 1.0 - (_viewModel.AmbientIntensity / 100.0);

            if (studioFactor <= 0)
            {
                StudioLight.Color = Colors.Black;
                FillLight.Color = Colors.Black;
            }
            else
            {
                byte keyVal = (byte)(255 * studioFactor);
                byte fillVal = (byte)(64 * studioFactor);
                StudioLight.Color = Color.FromRgb(keyVal, keyVal, keyVal);
                FillLight.Color = Color.FromRgb(fillVal, fillVal, fillVal);
            }

            // 3. Update Studio Light Direction
            double phiRad = _viewModel.LightRotation * Math.PI / 180.0;
            double thetaRad = _viewModel.LightHeight * Math.PI / 180.0;
            double x = Math.Cos(thetaRad) * Math.Sin(phiRad);
            double y = Math.Sin(thetaRad);
            double z = Math.Cos(thetaRad) * Math.Cos(phiRad);
            StudioLight.Direction = new Vector3D(-x, -y, -z);
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

            var saveFileDialog = new CommonSaveFileDialog
            {
                Filters = { new CommonFileDialogFilter("PNG Image", "*.png") },
                Title = "Save 4K Snapshot",
                DefaultExtension = ".png",
                DefaultFileName = defaultFileName
            };

            if (saveFileDialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                string filePath = Path.GetExtension(saveFileDialog.FileName).Equals(".png", StringComparison.OrdinalIgnoreCase)
                    ? saveFileDialog.FileName
                    : Path.ChangeExtension(saveFileDialog.FileName, ".png");
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

        private void HandleAutoRotateChanged(bool isAutoRotating)
        {
            if (!isAutoRotating && _activeSceneModel != null)
            {
                var transformGroup = _activeSceneModel.RootVisual.Transform as Transform3DGroup;
                if (transformGroup != null && transformGroup.Children.Contains(_autoRotation))
                {
                    double accumulatedAngle = ((AxisAngleRotation3D)_autoRotation.Rotation).Angle;
                    _activeSceneModel.RotationY = (_activeSceneModel.RotationY + accumulatedAngle) % 360;
                    transformGroup.Children.Remove(_autoRotation);
                    ((AxisAngleRotation3D)_autoRotation.Rotation).Angle = 0;
                }
            }

        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : Visual
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild)
                {
                    return typedChild;
                }
                var result = FindVisualChild<T>(child);
                if (result != null)
                {
                    return result;
                }
            }
            return null;
        }

    }
}
