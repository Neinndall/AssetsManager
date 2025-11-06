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
using AssetsManager.Views.Models;
using Microsoft.WindowsAPICodePack.Dialogs;
using System.Windows;

namespace AssetsManager.Views.Controls.Models
{
    public partial class ModelViewportControl : UserControl
    {
        public HelixViewport3D Viewport => Viewport3D;
        public LogService LogService { get; set; }
        public IAnimationAsset CurrentlyPlayingAnimation => _activeSceneModel?.CurrentAnimation;
        public double CurrentAnimationTime => _activeSceneModel?.AnimationTime ?? 0;
        public event EventHandler<double> AnimationProgressChanged;
        public event EventHandler<bool> MaximizeClicked;
        public event EventHandler<bool> SkyboxVisibilityChanged;

        private readonly LinesVisual3D _skeletonVisual = new LinesVisual3D { Color = Colors.Red, Thickness = 2 };
        private readonly PointsVisual3D _jointsVisual = new PointsVisual3D { Color = Colors.Blue, Size = 5 };
        private AnimationPlayer _animationPlayer;
        private DateTime _lastFrameTime;

        private bool _isAutoRotating = false;
        private readonly RotateTransform3D _autoRotation = new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), 0));

        private SceneModel _activeSceneModel;
        private readonly List<SceneModel> _loadedModels = new();

        public ModelViewportControl()
        {
            InitializeComponent();
            
            Loaded += (s, e) => {
                _animationPlayer = new AnimationPlayer(LogService);
            };

            Unloaded += (s, e) => Cleanup();

            Viewport.Children.Add(_skeletonVisual);
            Viewport.Children.Add(_jointsVisual);

            CompositionTarget.Rendering += CompositionTarget_Rendering;
            _lastFrameTime = DateTime.Now;
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
        }

        public void SetAnimation(IAnimationAsset animation)
        {
            if (_activeSceneModel == null) return;

            _activeSceneModel.CurrentAnimation = animation;
            _activeSceneModel.AnimationTime = 0;
            _activeSceneModel.IsAnimationPaused = false;
            _lastFrameTime = DateTime.Now;
        }

        public void TogglePauseResume(IAnimationAsset animationToToggle)
        {
            if (_activeSceneModel?.CurrentAnimation != animationToToggle) return;

            _activeSceneModel.IsAnimationPaused = !_activeSceneModel.IsAnimationPaused;
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
            if (_activeSceneModel == null) return;

            _activeSceneModel.CurrentAnimation = null;
            _activeSceneModel.AnimationTime = 0;
            _activeSceneModel.IsAnimationPaused = true;
        }

        public void ResetScene()
        {
            StopAnimation();

            foreach (var model in _loadedModels)
            {
                if (Viewport.Children.Contains(model.RootVisual))
                    Viewport.Children.Remove(model.RootVisual);
                model.Dispose();
            }
            _loadedModels.Clear();
            _activeSceneModel = null;

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
                    _activeSceneModel.AnimationTime += deltaTime;
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

            var position = new Point3D(-6, 2298, 465);
            var lookDirection = new Vector3D(4, -164, -445);
            var upDirection = new Vector3D(0, 1, 0);

            camera.Position = position;
            camera.LookDirection = lookDirection;
            camera.UpDirection = upDirection;
            camera.FieldOfView = 45;
        }

        public void TakeScreenshot(string filePath)
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

                double scalingFactor = 4.0;
                int width = (int)(Viewport3D.ActualWidth * scalingFactor);
                int height = (int)(Viewport3D.ActualHeight * scalingFactor);

                var renderBitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);

                var visual = new DrawingVisual();
                using (var context = visual.RenderOpen())
                {
                    var brush = new VisualBrush(Viewport3D);
                    context.DrawRectangle(brush, null, new Rect(0, 0, Viewport3D.ActualWidth, Viewport3D.ActualHeight));
                }

                visual.Transform = new ScaleTransform(scalingFactor, scalingFactor);
                renderBitmap.Render(visual);

                BitmapEncoder bitmapEncoder = new PngBitmapEncoder();
                bitmapEncoder.Frames.Add(BitmapFrame.Create(renderBitmap));

                using (var stream = File.Create(finalFilePath))
                {
                    bitmapEncoder.Save(stream);
                }
                LogService.LogInteractiveSuccess($"Screenshot saved to {finalFilePath}", finalFilePath);
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
        
        private void ResetCameraButton_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            ResetCamera();
        }

        private void ScreenshotButton_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSceneModel == null || string.IsNullOrEmpty(_activeSceneModel.Name))
            {
                LogService.LogWarning("No model loaded to name the screenshot automatically. Please load a model first.");
                return;
            }

            string modelName = _activeSceneModel.Name;
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string defaultFileName = $"{modelName}_{timestamp}.png";

            var saveFileDialog = new CommonSaveFileDialog
            {
                Filters = { new CommonFileDialogFilter("PNG Image", "*.png") },
                Title = "Save Screenshot File",
                DefaultExtension = ".png",
                DefaultFileName = defaultFileName // Pre-populate with the generated name
            };

            if (saveFileDialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                TakeScreenshot(saveFileDialog.FileName);
            }
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
                        transformGroup.Children.Remove(_autoRotation);
                        ((AxisAngleRotation3D)_autoRotation.Rotation).Angle = 0;
                    }
                }
            }
        }

        private void SkyboxToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Primitives.ToggleButton toggleButton)
            {
                SkyboxVisibilityChanged?.Invoke(this, !(toggleButton.IsChecked ?? false));
            }
        }
    }
}