using System;
using System.Linq;
using HelixToolkit.Wpf;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using System.Windows.Media.Media3D;
using System.Diagnostics;
using LeagueToolkit.Core.Animation;
using LeagueToolkit.Core.Mesh;
using System.Collections.Generic;
using AssetsManager.Services;
using AssetsManager.Services.Core;
using AssetsManager.Services.Models;
using AssetsManager.Views.Models.Models3D;
using Microsoft.WindowsAPICodePack.Dialogs;
using System.Windows;
using System.Windows.Threading;

namespace AssetsManager.Views.Controls.Models
{
    public partial class ModelViewportControl : UserControl
    {
        public HelixViewport3D Viewport => Viewport3D;
        public LogService LogService { get; set; }
        public IAnimationAsset CurrentlyPlayingAnimation => _activeSceneModel?.CurrentAnimation;
        public double CurrentAnimationTime => _activeSceneModel?.AnimationTime ?? 0;
        public event Action<AnimationModel, bool> PlaybackStateChanged;
        public event EventHandler<double> AnimationProgressChanged;
        public event EventHandler<bool> MaximizeClicked;
        public event EventHandler<double> AutoRotationStopped;

        private readonly LinesVisual3D _skeletonVisual = new LinesVisual3D { Color = Colors.Red, Thickness = 2 };
        private readonly PointsVisual3D _jointsVisual = new PointsVisual3D { Color = Colors.Blue, Size = 5 };
        private AnimationPlayer _animationPlayer;
        private DateTime _lastFrameTime;

        private bool _isAutoRotating = false;
        private readonly RotateTransform3D _autoRotation = new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), 0));

        private SceneModel _activeSceneModel;
        private AnimationModel _activeAnimationModel;
        private readonly List<SceneModel> _loadedModels = new();

        // Environment references
        private ModelVisual3D _skyVisual;
        private ModelVisual3D _groundVisual;

        public ModelViewportControl()
        {
            InitializeComponent();

            Loaded += (s, e) =>
            {
                _animationPlayer = new AnimationPlayer(LogService);
            };

            Unloaded += (s, e) => Cleanup();

            Viewport.Children.Add(_skeletonVisual);
            Viewport.Children.Add(_jointsVisual);

            CompositionTarget.Rendering += CompositionTarget_Rendering;
            _lastFrameTime = DateTime.Now;
        }

        public void RegisterEnvironment(ModelVisual3D sky, ModelVisual3D ground)
        {
            _skyVisual = sky;
            _groundVisual = ground;
        }


        public void Cleanup()
        {
            // 1. Desuscribir eventos
            CompositionTarget.Rendering -= CompositionTarget_Rendering;

            // 2. Limpiar escena y animaciones (ahora con Dispose)
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

            // 6. Limpiar referencias
            _animationPlayer = null;
            _skyVisual = null;
            _groundVisual = null;
        }

        public void SetAnimation(AnimationModel animationModel)
        {
            if (_activeSceneModel == null) return;

            _activeAnimationModel = animationModel;
            _activeSceneModel.CurrentAnimation = animationModel.AnimationData.AnimationAsset;
            _activeSceneModel.AnimationTime = 0;
            _activeSceneModel.IsAnimationPaused = false;
            _lastFrameTime = DateTime.Now;

            PlaybackStateChanged?.Invoke(animationModel, true);
        }

        public void TogglePauseResume(AnimationModel animationToToggle)
        {
            if (_activeAnimationModel != animationToToggle) return;

            _activeSceneModel.IsAnimationPaused = !_activeSceneModel.IsAnimationPaused;

            PlaybackStateChanged?.Invoke(_activeAnimationModel, !_activeSceneModel.IsAnimationPaused);
        }

        public void SeekAnimation(TimeSpan time)
        {
            if (_activeSceneModel != null)
            {
                _activeSceneModel.AnimationTime = time.TotalSeconds;
            }
        }

        public void StopAnimation()
        {
            if (_activeSceneModel == null || _activeAnimationModel == null) return;

            if (_activeSceneModel.CurrentAnimation != null)
            {
                PlaybackStateChanged?.Invoke(_activeAnimationModel, false);
            }

            _activeSceneModel.CurrentAnimation = null;
            _activeAnimationModel = null;
            _activeSceneModel.AnimationTime = 0;
            _activeSceneModel.IsAnimationPaused = true;
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
                model.Dispose();
            }
            _loadedModels.Clear();
            _activeSceneModel = null;
            
            // Clear environment refs as they are wiped from children
            _skyVisual = null;
            _groundVisual = null;

            if (AutoRotateToggleButton != null)
            {
                AutoRotateToggleButton.IsChecked = false;
            }
            _isAutoRotating = false;
            ((AxisAngleRotation3D)_autoRotation.Rotation).Angle = 0;

            _skeletonVisual.Points?.Clear();
            _jointsVisual.Points?.Clear();
        }

        public void AddModel(SceneModel model)
        {
            _loadedModels.Add(model);
            Viewport.Children.Add(model.RootVisual);
            SetActiveModel(model);
        }

        public void RemoveModel(SceneModel model)
        {
            if (model == _activeSceneModel)
            {
                if (_isAutoRotating)
                {
                    var transformGroup = model.RootVisual.Transform as Transform3DGroup;
                    if (transformGroup != null && transformGroup.Children.Contains(_autoRotation))
                    {
                        transformGroup.Children.Remove(_autoRotation);
                    }
                }
                _activeSceneModel = null;
            }
            _loadedModels.Remove(model);
            if (Viewport.Children.Contains(model.RootVisual))
            {
                Viewport.Children.Remove(model.RootVisual);
            }
            model.Dispose();
        }

        public void SetActiveModel(SceneModel model)
        {
            if (_isAutoRotating && _activeSceneModel != null)
            {
                var transformGroup = _activeSceneModel.RootVisual.Transform as Transform3DGroup;
                if (transformGroup != null && transformGroup.Children.Contains(_autoRotation))
                {
                    transformGroup.Children.Remove(_autoRotation);
                }
            }

            _activeSceneModel = model;
        }

        private void CompositionTarget_Rendering(object sender, System.EventArgs e)
        {
            var now = DateTime.Now;
            var deltaTime = (now - _lastFrameTime).TotalSeconds;
            _lastFrameTime = now;

            if (_isAutoRotating && _activeSceneModel != null)
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

            if (_animationPlayer != null && _activeSceneModel?.CurrentAnimation != null && _activeSceneModel.Skeleton != null && _activeSceneModel.SkinnedMesh != null)
            {
                if (!_activeSceneModel.IsAnimationPaused)
                {
                    var speed = _activeAnimationModel?.Speed ?? 1.0;
                    _activeSceneModel.AnimationTime += deltaTime * speed;

                    var duration = _activeSceneModel.CurrentAnimation.Duration;
                    if (duration > 0 && _activeSceneModel.AnimationTime >= duration)
                    {
                        _activeSceneModel.AnimationTime = 0;
                    }

                    AnimationProgressChanged?.Invoke(this, _activeSceneModel.AnimationTime);
                }

                _animationPlayer.Update(
                    (float)_activeSceneModel.AnimationTime,
                    _activeSceneModel.CurrentAnimation,
                    _activeSceneModel.Skeleton,
                    _activeSceneModel.SkinnedMesh,
                    _activeSceneModel.Parts.ToList(),
                    _skeletonVisual,
                    _jointsVisual
                );
            }
        }

        public void ResetCamera()
        {
            if (Viewport.Camera is not PerspectiveCamera camera) return;

            var position = new Point3D(0, 2300, 465);
            var lookDirection = new Vector3D(0, -164, -445);
            var upDirection = new Vector3D(0, 1, 0);

            camera.Position = position;
            camera.LookDirection = lookDirection;
            camera.UpDirection = upDirection;
            camera.FieldOfView = 45;
            camera.NearPlaneDistance = 1.0; // Evita clipping cercano
            camera.FarPlaneDistance = 50000; // Asegura ver objetos lejanos (Mapas completos)
        }

        public void SetFieldOfView(double fov)
        {
            if (Viewport.Camera is PerspectiveCamera camera)
            {
                camera.FieldOfView = fov;
            }
        }

        private double _currentPhi = 0;
        private double _currentTheta = 0;
        private double _currentAmbient = 100;

        public void SetAmbientIntensity(double intensity)
        {
            _currentAmbient = intensity;
            UpdateStudioLighting();
        }

        public void SetLightDirection(double phi, double theta)
        {
            _currentPhi = phi;
            _currentTheta = theta;
            UpdateStudioLighting();
        }

        private void UpdateStudioLighting()
        {
            if (GlobalAmbientLight == null || StudioLight == null || FillLight == null) return;

            // 1. Set Ambient Color
            byte ambVal = (byte)(255 * (_currentAmbient / 100.0));
            GlobalAmbientLight.Color = Color.FromRgb(ambVal, ambVal, ambVal);

            // 2. Set Studio Lights Intensity (Inverse of Ambient)
            // If Ambient is 100, Studio lights are 0 (Black).
            // If Ambient is 0, Studio lights are 100 (Full Color).
            double studioFactor = 1.0 - (_currentAmbient / 100.0);
            
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
            double phiRad = _currentPhi * Math.PI / 180.0;
            double thetaRad = _currentTheta * Math.PI / 180.0;
            double x = Math.Cos(thetaRad) * Math.Sin(phiRad);
            double y = Math.Sin(thetaRad);
            double z = Math.Cos(thetaRad) * Math.Cos(phiRad);
            StudioLight.Direction = new Vector3D(-x, -y, -z);
        }

        public void ResetStudioLighting()
        {
            _currentAmbient = 100;
            _currentPhi = 0;
            _currentTheta = 0;
            UpdateStudioLighting();
            if (Viewport.Camera is PerspectiveCamera camera) camera.FieldOfView = 45;
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

        private void ResetCameraButton_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            ResetCamera();
        }

        private void ScreenshotButton_Click(object sender, RoutedEventArgs e)
        {
            InitiateSnapshot(1.0);
        }

        private void FpsToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Primitives.ToggleButton toggleButton)
            {
                Viewport3D.ShowFrameRate = toggleButton.IsChecked ?? false;
            }
        }

        private void MaximizeToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Primitives.ToggleButton toggleButton)
            {
                MaximizeClicked?.Invoke(this, toggleButton.IsChecked ?? false);
            }
        }

        private void AutoRotateToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Primitives.ToggleButton toggleButton)
            {
                _isAutoRotating = toggleButton.IsChecked ?? false;
                if (!_isAutoRotating && _activeSceneModel != null)
                {
                    var transformGroup = _activeSceneModel.RootVisual.Transform as Transform3DGroup;
                    if (transformGroup != null && transformGroup.Children.Contains(_autoRotation))
                    {
                        AutoRotationStopped?.Invoke(this, ((AxisAngleRotation3D)_autoRotation.Rotation).Angle);
                        transformGroup.Children.Remove(_autoRotation);
                        ((AxisAngleRotation3D)_autoRotation.Rotation).Angle = 0;
                    }
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