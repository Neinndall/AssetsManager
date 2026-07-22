using System;
using System.IO;
using System.Linq;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Diagnostics;
using LeagueToolkit.Core.Animation;
using LeagueToolkit.Core.Mesh;
using System.Collections.Generic;
using AssetsManager.Services;
using AssetsManager.Services.Core;
using AssetsManager.Services.Viewer;
using AssetsManager.Services.Viewer.Vfx;
using AssetsManager.Utils;
using AssetsManager.Utils.Rendering;
using AssetsManager.Views.Models.Viewer;
using AssetsManager.Views.Helpers;
using Microsoft.WindowsAPICodePack.Dialogs;
using System.Windows;
using OpenTK.Wpf;
using System.Numerics;
using System.Threading.Tasks;

namespace AssetsManager.Views.Controls.Viewer
{
    public partial class ViewerViewportControl : UserControl, IDisposable
    {
        private Silk.NET.OpenGL.GL _gl;
        private GlMeshRenderer _meshRenderer;
        private GridRenderer _gridRenderer;
        private readonly ViewerViewportModel _viewModel;
        public ViewerViewportModel ViewModel => _viewModel;

        private readonly Viewport3D _dummyViewport = new Viewport3D
        {
            Camera = new PerspectiveCamera(new Point3D(0, 1130, 280), new Vector3D(0, -0.14, -0.99), new Vector3D(0, 0.99, -0.14), 45)
        };
        public Viewport3D Viewport3D => _dummyViewport;
        public Viewport3D Viewport => Viewport3D;

        private VfxOpenGlRenderer _vfxRenderer;
        private readonly List<VfxPlaybackGraphRuntime> _vfxSims = new();
        private readonly Dictionary<SceneModel, Dictionary<string, VfxAnimationClip>> _modelVfxClips = new();
        private readonly Dictionary<SceneModel, Dictionary<uint, VfxSystemDefinition>> _modelVfxDefs = new();
        private readonly Dictionary<SceneModel, IReadOnlyDictionary<uint, uint>> _modelVfxResourceMap = new();
        private readonly Dictionary<SceneModel, Task> _modelVfxLoadTasks = new();
        private readonly Dictionary<BitmapSource, uint> _vfxTextureCache = new();
        private readonly VfxLoadingService _vfxLoadingService = new();
        private IAnimationAsset _lastActiveAnimation;
        private double _lastActiveAnimationTime = 0;
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

                _vfxRenderer = new VfxOpenGlRenderer();
                _vfxRenderer.Initialize(_gl);

                _gridRenderer = new GridRenderer();
                _gridRenderer.Initialize(_gl, GlShaderCompiler.UsesEmbeddedProfile(_gl), 1000f);
            }
            catch (Exception ex)
            {
                LogService.LogError(ex, "Failed to initialize Silk.NET OpenGL context.");
            }
        }

        private void OpenTkControl_Render(TimeSpan delta)
        {
            if (_gl == null || _meshRenderer == null) return;

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
            float aspect = (float)(OpenTkControl.ActualWidth / OpenTkControl.ActualHeight);
            if (float.IsNaN(aspect) || aspect <= 0) aspect = 1.0f;
            var proj = Matrix4x4.CreatePerspectiveFieldOfView(fovRadians, aspect, 10f, 10000f);
            var viewProj = view * proj;

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

            // 5. Render active particle systems
            if (_vfxRenderer != null && _vfxSims.Count > 0)
            {
                for (int i = 0; i < _vfxSims.Count; i++)
                {
                    var graph = _vfxSims[i];
                    for (int runtimeIndex = 0; runtimeIndex < graph.Runtimes.Count; runtimeIndex++)
                    {
                        VfxPlaybackRuntime runtime = graph.Runtimes[runtimeIndex];
                        for (int j = 0; j < runtime.Emitters.Count; j++)
                        {
                        var es = runtime.Emitters[j];
                        if (es.PendingTexture is BitmapSource bmp)
                        {
                            if (!_vfxTextureCache.TryGetValue(bmp, out var tex))
                            {
                                tex = UploadBitmapToGl(bmp);
                                _vfxTextureCache[bmp] = tex;
                            }
                            es.Texture = tex;
                            es.TextureWidth = bmp.PixelWidth;
                            es.TextureHeight = bmp.PixelHeight;
                            es.PendingTexture = null;
                        }
                        if (es.PendingTextureMult is BitmapSource bmpMult)
                        {
                            if (!_vfxTextureCache.TryGetValue(bmpMult, out var tex))
                            {
                                tex = UploadBitmapToGl(bmpMult);
                                _vfxTextureCache[bmpMult] = tex;
                            }
                            es.TextureMult = tex;
                            es.PendingTextureMult = null;
                        }
                        if (es.PendingDistortionTexture is BitmapSource bmpDist)
                        {
                            if (!_vfxTextureCache.TryGetValue(bmpDist, out var tex))
                            {
                                tex = UploadBitmapToGl(bmpDist);
                                _vfxTextureCache[bmpDist] = tex;
                            }
                            es.DistortionTexture = tex;
                            es.PendingDistortionTexture = null;
                        }
                        if (es.PendingErosionTexture is BitmapSource bmpErosion)
                        {
                            if (!_vfxTextureCache.TryGetValue(bmpErosion, out var tex))
                            {
                                tex = UploadBitmapToGl(bmpErosion);
                                _vfxTextureCache[bmpErosion] = tex;
                            }
                            es.ErosionTexture = tex;
                            es.PendingErosionTexture = null;
                        }
                        if (es.PendingMesh != null)
                        {
                            var meshData = es.PendingMesh.Value;
                            _vfxRenderer.UploadEmitterMesh(es, meshData.Positions, meshData.Uvs, meshData.Indices);
                            es.PendingMesh = null;
                        }
                        }
                    }
                }

                _vfxRenderer.CaptureScene((uint)OpenTkControl.ActualWidth, (uint)OpenTkControl.ActualHeight);
                for (int i = 0; i < _vfxSims.Count; i++)
                {
                    foreach (VfxPlaybackRuntime runtime in _vfxSims[i].Runtimes)
                        _vfxRenderer.Render(runtime, viewProj, view);
                }
            }
        }

        private CustomCameraController _cameraController;
        private readonly Dictionary<SceneModel, AnimationPlayer> _modelPlayers = new();
        private readonly System.Diagnostics.Stopwatch _frameStopwatch = new();
        private readonly System.Diagnostics.Stopwatch _fpsStopwatch = new();
        private int _framesSinceFpsUpdate;
        private bool _renderPulse;
        private TimeSpan _nextLimitedFrame;
        private DateTime _lastFrameTime;

        private readonly RotateTransform3D _autoRotation = new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), 0));

        private SceneModel _activeSceneModel;
        private AnimationModel _activeAnimationModel;
        private readonly List<SceneModel> _loadedModels = new();
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

            _viewModel.PropertyChanged += OnViewportViewModelPropertyChanged;

            Loaded += OnViewportLoaded;
            Unloaded += OnViewportUnloaded;

            UpdateToolbarVisibility();
        }

        private void OnViewportViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(ViewerViewportModel.LimitFps):
                    _nextLimitedFrame = TimeSpan.Zero;
                    _frameStopwatch.Restart();
                    _fpsStopwatch.Restart();
                    _framesSinceFpsUpdate = 0;
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
            _cameraController = new CustomCameraController(Viewport3D, CameraInputSurface);

            var settings = new GLWpfControlSettings
            {
                MajorVersion = 3,
                MinorVersion = 3,
                Profile = OpenTK.Windowing.Common.ContextProfile.Core
            };
            OpenTkControl.Start(settings);

            // Self-healing subscription to the rendering loop
            CompositionTarget.Rendering -= CompositionTarget_Rendering;
            CompositionTarget.Rendering += CompositionTarget_Rendering;
            _lastFrameTime = DateTime.Now;
            _frameStopwatch.Restart();
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
                        
                        var part = new ModelPart
                        {
                            Name = name + "_" + sceneModel.Parts.Count,
                            Geometry = new GeometryModel3D(transformedMesh, geomModel.Material),
                            IsVisible = true
                        };
                        
                        if (geomModel.Material is DiffuseMaterial diffuse && diffuse.Brush is ImageBrush imgBrush && imgBrush.ImageSource is BitmapSource bitmap)
                        {
                            string texName = "tex_" + part.Name;
                            part.AllTextures[texName] = bitmap;
                            part.SelectedTextureName = texName;
                        }
                        
                        sceneModel.Parts.Add(part);
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
                // 1. Desuscribir eventos
                CompositionTarget.Rendering -= CompositionTarget_Rendering;
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

                _vfxSims.Clear();
                _modelVfxClips.Clear();
                _modelVfxDefs.Clear();
                _modelVfxResourceMap.Clear();
                _modelVfxLoadTasks.Clear();
                _vfxTextureCache.Clear();
                _vfxLoadingService.ClearCaches();
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

            _lastFrameTime = DateTime.Now;
            _frameStopwatch.Restart();

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
            
            _ = EnsureVfxLoadedAsync(model);

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
            _modelVfxDefs.Remove(model);
            _modelVfxClips.Remove(model);
            _modelVfxResourceMap.Remove(model);
            _modelVfxLoadTasks.Remove(model);
            if (removingActiveModel)
            {
                _vfxSims.Clear();
                Panel?.SetVfxSystems(new List<string>());
            }
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

        private void CompositionTarget_Rendering(object sender, System.EventArgs e)
        {
            var renderingTime = e is RenderingEventArgs renderingArgs
                ? renderingArgs.RenderingTime
                : _fpsStopwatch.Elapsed;

            if (_viewModel.LimitFps)
            {
                var targetFrameTime = TimeSpan.FromSeconds(1.0 / 60.0);
                if (_nextLimitedFrame != TimeSpan.Zero && renderingTime < _nextLimitedFrame)
                    return;

                _nextLimitedFrame = _nextLimitedFrame == TimeSpan.Zero ||
                                    renderingTime - _nextLimitedFrame > targetFrameTime * 4
                    ? renderingTime + targetFrameTime
                    : _nextLimitedFrame + targetFrameTime;
            }
            else
            {
                _nextLimitedFrame = TimeSpan.Zero;
            }

            var elapsed = _frameStopwatch.Elapsed.TotalSeconds;
            _frameStopwatch.Restart();

            _framesSinceFpsUpdate++;
            if (_fpsStopwatch.Elapsed.TotalSeconds >= 1.0)
            {
                var fps = _framesSinceFpsUpdate / _fpsStopwatch.Elapsed.TotalSeconds;
                _viewModel.DisplayFps = Math.Round(fps).ToString("0");
                _framesSinceFpsUpdate = 0;
                _fpsStopwatch.Restart();
            }

            var now = DateTime.Now;
            _lastFrameTime = now;
            var deltaTime = elapsed;

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
                    double rotationSpeed = 30.0 * elapsed;
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

                    // Synchronize active model VFX (runs even if static/paused/no animation)
                    if (model == _activeSceneModel)
                    {
                        bool isSeekOrLoop = _lastActiveAnimation != model.CurrentAnimation || 
                                            model.AnimationTime < _lastActiveAnimationTime || 
                                            Math.Abs(model.AnimationTime - _lastActiveAnimationTime) > 0.2;
                        
                        _lastActiveAnimationTime = model.AnimationTime;

                        if (isSeekOrLoop)
                        {
                            _lastActiveAnimation = model.CurrentAnimation;
                            SetActiveAnimationVfx(model, model.CurrentAnimation);
                        }

                        // Update VFX attachment positions
                        for (int idxSim = 0; idxSim < _vfxSims.Count; idxSim++)
                        {
                            var sim = _vfxSims[idxSim];
                            Matrix4x4 attachMatrix = Matrix4x4.Identity;
                            if (sim.UserTag is VfxAnimationEvent ev && model.Skeleton != null)
                            {
                                var player = GetPlayerForModel(model);
                                attachMatrix = player.GetBoneTransform(ev.BoneName, ev.BoneHash, model.Skeleton);
                            }
                            else if (!(sim.UserTag is VfxAnimationEvent) && model.Skeleton != null)
                            {
                                var player = GetPlayerForModel(model);
                                string boneName = model.Skeleton.Joints.Any(j => string.Equals(j.Name, "C_BUFFVERT", StringComparison.OrdinalIgnoreCase)) ? "C_BUFFVERT" : "Root";
                                attachMatrix = player.GetBoneTransform(boneName, 0, model.Skeleton);
                            }

                            // Apply model transform
                            var rotX = Matrix4x4.CreateRotationX((float)(model.RotationX * Math.PI / 180f));
                            var rotY = Matrix4x4.CreateRotationY((float)(model.RotationY * Math.PI / 180f));
                            var rotZ = Matrix4x4.CreateRotationZ((float)(model.RotationZ * Math.PI / 180f));
                            var scale = Matrix4x4.CreateScale((float)model.Scale);
                            var trans = Matrix4x4.CreateTranslation((float)model.PositionX, (float)model.PositionY, (float)model.PositionZ);
                            var modelWorld = scale * rotX * rotY * rotZ * trans;

                            sim.SetTransform(attachMatrix * modelWorld);
                            
                            if (!(sim.UserTag is VfxAnimationEvent))
                            {
                                // Manual VFX previews update in real-time, ignoring the animation pause/speed
                                sim.Update((float)deltaTime);
                            }
                            else if (!model.IsAnimationPaused)
                            {
                                // Animation-tied VFX events only update when animation is playing
                                sim.Update((float)(deltaTime * speed));
                            }
                        }
                    }
                }

                if (_activeSceneModel != null && _activeSceneModel.CurrentAnimation != null)
                {
                    Panel?.UpdateAnimationProgress(_activeSceneModel.AnimationTime);
                }
            }

            // WPF renders static 3D scenes on demand. While FPS measurement is
            // visible, request continuous real frames without a visible camera change.
            if (_viewModel.IsFpsVisible && Viewport3D.IsVisible &&
                Viewport3D.Camera is PerspectiveCamera camera)
            {
                _renderPulse = !_renderPulse;
                camera.NearPlaneDistance = _renderPulse ? 0.1 : 0.100001;
            }

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
            double baselineY = 1000;
            
            Point3D position;
            Vector3D lookDirection;
            Vector3D upDirection = new Vector3D(0.00, 1.00, 0.00);

            if (TryGetModelBounds(out var center, out var maxDim))
            {
                double distance = isMap ? maxDim * 0.55 : maxDim * 1.8;
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
            position = isMap ? new Point3D(0.00, 1386.00, 670.00) : new Point3D(0.00, 130.00 + baselineY, 280.00);
            lookDirection = isMap ? new Vector3D(0.00, -250.00, -650.00) : new Vector3D(0.00, -40.00, -280.00);

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

            if (TryGetModelBounds(out var center, out var maxDim))
            {
                targetPoint = center;
                distance = (Panel?.ViewModel?.IsMapMode == true ? 1.5 : 1.8) * maxDim;
                if (distance < 50) distance = 250;
            }

            Vector3D lookDirection;
            Point3D cameraPosition;
            Vector3D upDirection = new Vector3D(0, 1, 0);

            switch (viewType)
            {
                case "Front":
                    cameraPosition = new Point3D(targetPoint.X, targetPoint.Y, targetPoint.Z + distance);
                    lookDirection = new Vector3D(0, 0, -distance);
                    break;
                case "Back":
                    cameraPosition = new Point3D(targetPoint.X, targetPoint.Y, targetPoint.Z - distance);
                    lookDirection = new Vector3D(0, 0, distance);
                    break;
                case "Left":
                    cameraPosition = new Point3D(targetPoint.X - distance, targetPoint.Y, targetPoint.Z);
                    lookDirection = new Vector3D(distance, 0, 0);
                    break;
                case "Right":
                    cameraPosition = new Point3D(targetPoint.X + distance, targetPoint.Y, targetPoint.Z);
                    lookDirection = new Vector3D(-distance, 0, 0);
                    break;
                case "Top":
                    cameraPosition = new Point3D(targetPoint.X, targetPoint.Y + distance, targetPoint.Z);
                    lookDirection = new Vector3D(0, -distance, 0);
                    upDirection = new Vector3D(0, 0, -1);
                    break;
                case "Bottom":
                    cameraPosition = new Point3D(targetPoint.X, targetPoint.Y - distance, targetPoint.Z);
                    lookDirection = new Vector3D(0, distance, 0);
                    upDirection = new Vector3D(0, 0, 1);
                    break;
                default:
                    return;
            }

            _cameraController.FlyTo(cameraPosition, lookDirection, upDirection);
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

        public void TakeScreenshot(string filePath, double scaleFactor = 1.0)
        {
            string finalFilePath = filePath;
            if (Path.GetExtension(finalFilePath).ToLower() != ".png")
            {
                finalFilePath = Path.ChangeExtension(finalFilePath, ".png");
            }

            try
            {
                int baseWidth = (int)Viewport3D.ActualWidth;
                int baseHeight = (int)Viewport3D.ActualHeight;
                int supersamplingFactor = (int)Math.Max(1, scaleFactor);

                if (baseWidth <= 0 || baseHeight <= 0)
                {
                    LogService.LogWarning("Cannot take a screenshot of a zero-sized viewport.");
                    return;
                }

                int outputWidth = baseWidth * supersamplingFactor;
                int outputHeight = baseHeight * supersamplingFactor;
                var rtb = new RenderTargetBitmap(outputWidth, outputHeight, 96, 96, PixelFormats.Pbgra32);
                var drawing = new DrawingVisual();
                using (var context = drawing.RenderOpen())
                {
                    context.PushTransform(new ScaleTransform(supersamplingFactor, supersamplingFactor));
                    context.DrawRectangle(new VisualBrush(Viewport3D), null, new Rect(0, 0, baseWidth, baseHeight));
                    context.Pop();
                }
                rtb.Render(drawing);

                var pngEncoder = new PngBitmapEncoder();
                pngEncoder.Interlace = PngInterlaceOption.Off;
                pngEncoder.Frames.Add(BitmapFrame.Create(rtb));

                using (var stream = File.Create(finalFilePath))
                {
                    pngEncoder.Save(stream);
                }

                LogService.LogInteractiveSuccess($"Snapshot saved ({baseWidth * supersamplingFactor}x{baseHeight * supersamplingFactor})", finalFilePath, Path.GetFileName(finalFilePath));
            }
            catch (Exception ex)
            {
                LogService.LogError(ex, $"Failed to save screenshot to {finalFilePath}");
            }
        }

        public void InitiateSnapshot(double scaleFactor = 1.0)
        {
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
                Title = scaleFactor > 1.5 ? "Save 4K Snapshot" : "Save Screenshot",
                DefaultExtension = ".png",
                DefaultFileName = defaultFileName
            };

            if (saveFileDialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                TakeScreenshot(saveFileDialog.FileName, scaleFactor);
            }
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

        private void ScreenshotButton_Click(object sender, RoutedEventArgs e)
        {
            InitiateSnapshot(1.0);
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

        private Task EnsureVfxLoadedAsync(SceneModel model)
        {
            if (model == null || _modelVfxDefs.ContainsKey(model)) return Task.CompletedTask;
            if (_modelVfxLoadTasks.TryGetValue(model, out Task existingTask)) return existingTask;

            Task loadTask = LoadVfxForModelAsync(model);
            _modelVfxLoadTasks[model] = loadTask;
            return loadTask;
        }

        private async Task LoadVfxForModelAsync(SceneModel model)
        {
            if (model == null || string.IsNullOrEmpty(model.SkinBinPath) || !File.Exists(model.SkinBinPath)) return;

            try
            {
                var bundle = await _vfxLoadingService.LoadAsync(model.SkinBinPath, LogService);
                if (_isCleanedUp || !_loadedModels.Contains(model)) return;

                var systems = bundle.Systems;
                var clips = bundle.Clips;
                var combinedResMap = bundle.ResourceMap;

                _modelVfxDefs[model] = systems;
                _modelVfxClips[model] = clips;
                _modelVfxResourceMap[model] = combinedResMap;

                LogService.Log($"Loaded {systems.Count} VFX systems and {clips.Count} animation clips for '{model.Name}'.");

                Panel?.SetVfxSystems(systems.Values.Select(s => s.Name).Distinct().OrderBy(n => n).ToList());
            }
            catch (Exception ex)
            {
                LogService.LogError(ex, $"Failed to load VFX files for model {model.Name}");
            }
        }

        private void SetActiveAnimationVfx(SceneModel model, IAnimationAsset animation)
        {
            _vfxSims.Clear();

            if (animation == null || !_modelVfxClips.TryGetValue(model, out var clips) || !_modelVfxDefs.TryGetValue(model, out var defs))
            {
                return;
            }

            var animData = model.Animations.FirstOrDefault(a => a.AnimationAsset == animation);
            if (animData == null) return;

            string animFileName = Path.GetFileName(animData.Name.Replace('\\', '/'));
            string animNameWithoutExt = Path.GetFileNameWithoutExtension(animFileName);

            VfxAnimationClip clip = null;
            if (!clips.TryGetValue(animFileName, out clip) && !clips.TryGetValue(animNameWithoutExt, out clip))
            {
                // Also match against the referenced .anm path inside each clip (modern mClipDataMap entries).
                string animNameLower = animNameWithoutExt.ToLowerInvariant();
                var matchingKey = clips.Keys.FirstOrDefault(k =>
                    k.Equals(animFileName, StringComparison.OrdinalIgnoreCase) ||
                    k.Equals(animNameWithoutExt, StringComparison.OrdinalIgnoreCase) ||
                    animFileName.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                    k.Contains(animNameWithoutExt, StringComparison.OrdinalIgnoreCase));

                if (matchingKey == null)
                {
                    matchingKey = clips.Keys.FirstOrDefault(k =>
                    {
                        var c = clips[k];
                        string refName = Path.GetFileNameWithoutExtension(c.AnimationName.Replace('\\', '/'));
                        return !string.IsNullOrEmpty(refName) &&
                               (refName.Equals(animNameWithoutExt, StringComparison.OrdinalIgnoreCase) ||
                                refName.Equals(animFileName, StringComparison.OrdinalIgnoreCase) ||
                                animNameLower.Contains(refName.ToLowerInvariant()) ||
                                refName.ToLowerInvariant().Contains(animNameLower));
                    });
                }

                if (matchingKey != null)
                {
                    clip = clips[matchingKey];
                }
            }

            if (clip == null)
            {
                LogService.Log($"No VFX clip matched animation '{animNameWithoutExt}'.");
                return;
            }

            var resMap = _modelVfxResourceMap.TryGetValue(model, out var rm) ? rm : null;
            string charFolder = Path.GetDirectoryName(Path.GetDirectoryName(model.SkinBinPath));

            foreach (var ev in clip.ParticleEvents)
            {
                uint keyHash = ev.EffectHash != 0 ? ev.EffectHash : VfxResourceResolver.Fnv1a(ev.EffectName);
                VfxSystemDefinition def = null;
                if (resMap != null && resMap.TryGetValue(keyHash, out var objHash))
                {
                    defs.TryGetValue(objHash, out def);
                }
                if (def == null)
                {
                    def = defs.Values.FirstOrDefault(d =>
                        (!string.IsNullOrEmpty(ev.EffectName) && string.Equals(d.Name, ev.EffectName, StringComparison.OrdinalIgnoreCase)) ||
                        (ev.EffectHash != 0 && (d.PathHash == ev.EffectHash || VfxResourceResolver.Fnv1a(d.Name) == ev.EffectHash)));
                }

                if (def == null) continue;

                int seed = HashCode.Combine(def.PathHash, ev.StartFrame);
                var sim = _vfxLoadingService.PreparePlaybackGraph(
                    def,
                    defs,
                    resMap ?? new Dictionary<uint, uint>(),
                    charFolder,
                    Matrix4x4.Identity,
                    seed,
                    LogService);

                float fps = animation.Fps > 1f && animation.Fps < 240f ? animation.Fps : 30f;
                float startDelay = MathF.Max(0f, ev.StartFrame) / fps;
                sim.SetStartDelay(startDelay);

                sim.UserTag = ev;
                _vfxSims.Add(sim);
            }
        }

        private uint UploadBitmapToGl(BitmapSource bitmap)
        {
            if (bitmap.Format != System.Windows.Media.PixelFormats.Bgra32)
            {
                var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap();
                converted.BeginInit();
                converted.Source = bitmap;
                converted.DestinationFormat = System.Windows.Media.PixelFormats.Bgra32;
                converted.EndInit();
                bitmap = converted;
            }
            int width = bitmap.PixelWidth;
            int height = bitmap.PixelHeight;
            int stride = width * 4;
            byte[] pixelData = new byte[height * stride];
            bitmap.CopyPixels(new Int32Rect(0, 0, width, height), pixelData, stride, 0);
            return _vfxRenderer.UploadTexture(pixelData, width, height);
        }

        public void PlayVfxSystem(string systemName)
        {
            if (_activeSceneModel == null) return;

            if (!_modelVfxDefs.TryGetValue(_activeSceneModel, out var defs)) return;
            var def = defs.Values.FirstOrDefault(d => string.Equals(d.Name, systemName, StringComparison.OrdinalIgnoreCase));
            if (def == null) return;

            _vfxSims.Clear();

            string charFolder = Path.GetDirectoryName(Path.GetDirectoryName(_activeSceneModel.SkinBinPath));
            var transform = Matrix4x4.CreateTranslation(new Vector3(
                (float)_activeSceneModel.PositionX,
                (float)_activeSceneModel.PositionY,
                (float)_activeSceneModel.PositionZ));
            var resourceMap = _modelVfxResourceMap.TryGetValue(_activeSceneModel, out var map)
                ? map
                : new Dictionary<uint, uint>();
            var sim = _vfxLoadingService.PreparePlaybackGraph(
                def,
                defs,
                resourceMap,
                charFolder,
                transform,
                HashCode.Combine(def.PathHash, systemName),
                LogService);

            _vfxSims.Add(sim);
            LogService.Log($"Playing VFX system manually: {systemName}");
        }

    }
}
