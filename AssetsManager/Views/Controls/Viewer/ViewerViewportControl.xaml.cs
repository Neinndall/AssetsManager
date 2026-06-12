using System;
using System.IO;
using System.Linq;
using System.ComponentModel;
using HelixToolkit.Wpf;
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
using AssetsManager.Utils;
using AssetsManager.Views.Models.Viewer;
using AssetsManager.Views.Helpers;
using Microsoft.WindowsAPICodePack.Dialogs;
using System.Windows;
using System.Windows.Threading;

namespace AssetsManager.Views.Controls.Viewer
{
    public partial class ViewerViewportControl : UserControl, IDisposable
    {
        private readonly ViewerViewportModel _viewModel;
        public ViewerViewportModel ViewModel => _viewModel;

        public HelixViewport3D Viewport => Viewport3D;
        public LogService LogService { get; set; }
        public AppSettings AppSettings { get; set; }
        public ViewerPanelControl Panel { get; set; }
        public IAnimationAsset CurrentlyPlayingAnimation => _activeSceneModel?.CurrentAnimation;
        public double CurrentAnimationTime => _activeSceneModel?.AnimationTime ?? 0;

        private CustomCameraController _cameraController;
        private readonly LinesVisual3D _skeletonVisual = new LinesVisual3D { Color = Colors.Red, Thickness = 2 };
        private readonly PointsVisual3D _jointsVisual = new PointsVisual3D { Color = Colors.Blue, Size = 5 };
        private AnimationPlayer _animationPlayer;
        private DateTime _lastFrameTime;

        private readonly RotateTransform3D _autoRotation = new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), 0));

        private SceneModel _activeSceneModel;
        private AnimationModel _activeAnimationModel;
        private readonly List<SceneModel> _loadedModels = new();
        private bool _isCleanedUp;

        // Environment references
        private ModelVisual3D _skyVisual;
        private ModelVisual3D _groundVisual;

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
            // Self-healing: only create the camera controller and animation player
            // here. The window must not instantiate them too — see ViewerWindow notes.
            _animationPlayer = new AnimationPlayer(LogService);
            _cameraController = new CustomCameraController(Viewport3D);

            // Re-add skeleton visual guides to viewport if they were cleared
            if (!Viewport.Children.Contains(_skeletonVisual))
                Viewport.Children.Add(_skeletonVisual);
            if (!Viewport.Children.Contains(_jointsVisual))
                Viewport.Children.Add(_jointsVisual);

            // Self-healing subscription to the rendering loop
            CompositionTarget.Rendering -= CompositionTarget_Rendering;
            CompositionTarget.Rendering += CompositionTarget_Rendering;
            _lastFrameTime = DateTime.Now;
        }

        private void OnViewportUnloaded(object sender, RoutedEventArgs e)
        {
            Cleanup();
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
                return;
            }

            if (_groundVisual == null)
            {
                _groundVisual = SceneElements.CreateGroundPlane(p => SceneElements.LoadSceneTexture(p, LogService), LogService.LogError, AppSettings?.CustomFloorTexturePath);
                Viewport.Children.Add(_groundVisual);
            }

            if (_skyVisual == null)
            {
                _skyVisual = SceneElements.CreateSidePlanes(p => SceneElements.LoadSceneTexture(p, LogService), LogService.LogError);
                Viewport.Children.Add(_skyVisual);
            }

            // Ensure initial state is applied
            SetGroundVisibility(!_viewModel.IsTransparentBg);
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

                // 3. Limpiar visuales de esqueleto
                _skeletonVisual.Points?.Clear();
                _jointsVisual.Points?.Clear();

                // 4. Remover visuales del viewport
                if (Viewport.Children.Contains(_skeletonVisual))
                    Viewport.Children.Remove(_skeletonVisual);
                if (Viewport.Children.Contains(_jointsVisual))
                    Viewport.Children.Remove(_jointsVisual);

                // 5. Limpiar todo el viewport
                Viewport.Children.Clear();

                // 6. Liberar el AnimationPlayer y todos sus buffers cacheados
                _animationPlayer?.Dispose();
                _animationPlayer = null;

                // 7. Liberar el CameraController (dueño único)
                _cameraController?.Dispose();
                _cameraController = null;

                _skyVisual = null;
                _groundVisual = null;
            }
            catch (Exception ex)
            {
                LogService?.LogError(ex, "Error during ViewerViewportControl.Cleanup");
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
            ((AxisAngleRotation3D)_autoRotation.Rotation).Angle = 0;

            _skeletonVisual.Points?.Clear();
            _jointsVisual.Points?.Clear();

            // CRITICAL: Free cached vertex/skin buffers from the previous model so
            // the next load does not retain RAM of a model that is no longer in use.
            _animationPlayer?.ClearCache();

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
            if (model == _activeSceneModel)
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
            var now = DateTime.Now;
            var deltaTime = (now - _lastFrameTime).TotalSeconds;
            _lastFrameTime = now;

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
                    ((AxisAngleRotation3D)_autoRotation.Rotation).Angle += 0.5;
                }
            }

            if (_animationPlayer != null && _loadedModels.Count > 0)
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
                            model.AnimationTime += deltaTime * speed;

                            var duration = model.CurrentAnimation.Duration;
                            if (duration > 0 && model.AnimationTime >= duration)
                            {
                                model.AnimationTime = 0;
                            }
                        }

                        bool isActive = model == _activeSceneModel;

                        // Update skinning for all models that have an animation to ensure they are visible and moving.
                        // ObservableRangeCollection is passable directly to the AnimationPlayer (it iterates internally);
                        // the previous ToList() allocation per-frame has been removed.
                        _animationPlayer.Update(
                            (float)model.AnimationTime,
                            model.CurrentAnimation,
                            model.Skeleton,
                            model.SkinnedMesh,
                            model.Parts,
                            isActive ? _skeletonVisual : null,
                            isActive ? _jointsVisual : null,
                            model.Name
                        );
                    }
                }

                if (_activeSceneModel != null && _activeSceneModel.CurrentAnimation != null)
                {
                    Panel?.UpdateAnimationProgress(_activeSceneModel.AnimationTime);
                }
            }
        }

        public void ResetCamera(bool smooth = true)
        {
            bool isMap = Panel?.ViewModel?.IsMapMode == true;
            double baselineY = isMap ? 0 : (IsDiffMode ? 0 : 2000);
            
            Point3D position;
            Vector3D lookDirection;
            Vector3D upDirection = new Vector3D(0.00, 1.00, 0.00);

            // Dynamically calculate camera view based on loaded model's bounding box
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
                    var targetPoint = new Point3D(centerX, centerY, centerZ);

                    double maxDim = Math.Max(bounds.SizeX, Math.Max(bounds.SizeY, bounds.SizeZ));
                    if (maxDim <= 0) maxDim = 150; // Fallback height

                    double distance = isMap ? maxDim * 1.5 : maxDim * 1.8;
                    if (distance < 50) distance = 250; // Minimum distance fallback

                    position = new Point3D(centerX, centerY + distance * 0.15, centerZ + distance);
                    lookDirection = targetPoint - position;

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
            }

            // Fallback coordinates
            position = isMap ? new Point3D(0.00, 2386.00, 670.00) : new Point3D(0.00, 130.00 + baselineY, 280.00);
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
            double baselineY = Panel?.ViewModel?.IsMapMode == true || IsDiffMode ? 0 : 2000;
            Point3D targetPoint = new Point3D(0, 90.00 + baselineY, 0);
            double distance = 300.00;

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
                    targetPoint = new Point3D(centerX, centerY, centerZ);

                    double maxDim = Math.Max(bounds.SizeX, Math.Max(bounds.SizeY, bounds.SizeZ));
                    if (maxDim <= 0) maxDim = 150;

                    distance = (Panel?.ViewModel?.IsMapMode == true ? 1.5 : 1.8) * maxDim;
                    if (distance < 50) distance = 250;
                }
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

            var originalShowFrameRate = Viewport3D.ShowFrameRate;

            try
            {
                Viewport3D.ShowFrameRate = false;

                // Base size logic
                int baseWidth = (int)Viewport3D.ActualWidth;
                int baseHeight = (int)Viewport3D.ActualHeight;
                
                // If scaleFactor is high (e.g. 4 for 4K-ish), we use that. 
                // Helix RenderBitmap uses a scaling factor (supersampling).
                // If we want exact resolution (e.g. 3840x2160), we might need a different approach, 
                // but scaling the current view is usually what "Snapshot" implies.
                int supersamplingFactor = (int)Math.Max(1, scaleFactor);

                if (baseWidth <= 0 || baseHeight <= 0)
                {
                    LogService.LogWarning("Cannot take a screenshot of a zero-sized viewport.");
                    return;
                }

                // Traverse the visual tree to find the underlying System.Windows.Controls.Viewport3D
                var underlyingViewport = FindVisualChild<System.Windows.Controls.Viewport3D>(Viewport3D);
                if (underlyingViewport == null)
                {
                    LogService.LogError(null, "Could not find the underlying Viewport3D to create a screenshot.");
                    return;
                }

                // Use the built-in helper from HelixToolkit
                var backgroundBrush = Viewport3D.Background ?? Brushes.Transparent;
                var rtb = Viewport3DHelper.RenderBitmap(underlyingViewport, baseWidth, baseHeight, backgroundBrush, supersamplingFactor);

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
            finally
            {
                Viewport3D.ShowFrameRate = originalShowFrameRate;
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
    }
}
