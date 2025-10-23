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

        private readonly Stopwatch _stopwatch = new Stopwatch();
        private readonly LinesVisual3D _skeletonVisual = new LinesVisual3D { Color = Colors.Red, Thickness = 2 };
        private readonly PointsVisual3D _jointsVisual = new PointsVisual3D { Color = Colors.Blue, Size = 5 };
        private AnimationPlayer _animationPlayer;

        private IAnimationAsset _currentAnimation;
        private RigResource _skeleton;
        private SceneModel _sceneModel;
        public bool IsAnimationPaused { get; private set; }

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
            _stopwatch.Start();
        }

        public void Cleanup()
        {
            // 1. Desuscribir eventos
            CompositionTarget.Rendering -= CompositionTarget_Rendering;
            _stopwatch.Stop();
            
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
            _currentAnimation = null;
            _skeleton = null;
        }

        public void SetAnimation(IAnimationAsset animation)
        {
            _currentAnimation = animation;
            _stopwatch.Restart();
            IsAnimationPaused = false;
        }

        public void TogglePauseResume(IAnimationAsset animationToToggle)
        {
            if (_currentAnimation != animationToToggle) return;

            IsAnimationPaused = !IsAnimationPaused;
            if (IsAnimationPaused)
            {
                _stopwatch.Stop();
            }
            else
            {
                _stopwatch.Start();
            }
        }

        public void StopAnimation()
        {
            _currentAnimation = null;
            _stopwatch.Stop();
            IsAnimationPaused = false;
        }

        public void ResetScene()
        {
            StopAnimation();

            if (_sceneModel != null)
            {
                // 🆕 Remover del viewport ANTES de Dispose
                if (Viewport.Children.Contains(_sceneModel.RootVisual))
                    Viewport.Children.Remove(_sceneModel.RootVisual);
                
                // 🆕 CRÍTICO: Llamar a Dispose para liberar recursos
                _sceneModel.Dispose();
                _sceneModel = null;
            }

            _skeletonVisual.Points?.Clear();
            _jointsVisual.Points?.Clear();
            _skeleton = null;
        }

        public void SetSkeleton(RigResource skeleton)
        {
            _skeleton = skeleton;
        }

        public void SetModel(SceneModel model)
        {
            // 🆕 Si ya hay un modelo, limpiarlo antes de reemplazar
            if (_sceneModel != null && _sceneModel != model)
            {
                ResetScene();
            }
            
            _sceneModel = model;
            Viewport.Children.Add(model.RootVisual);
        }

        private void CompositionTarget_Rendering(object sender, System.EventArgs e)
        {
            if (_animationPlayer != null && _currentAnimation != null && _skeleton != null && 
                _sceneModel != null && _sceneModel.SkinnedMesh != null)
            {
                _animationPlayer.Update(
                    (float)_stopwatch.Elapsed.TotalSeconds, 
                    _currentAnimation, 
                    _skeleton, 
                    _sceneModel.SkinnedMesh, 
                    _sceneModel.Parts.ToList(), 
                    _skeletonVisual, 
                    _jointsVisual
                );
            }
        }

        public void ResetCamera()
        {
            if (Viewport.Camera is not PerspectiveCamera camera) return;

            var position = new Point3D(0, 250, 300);
            var lookDirection = new Vector3D(-2.059, -235.936, -598.980);
            var upDirection = new Vector3D(0.008, 0.930, -0.367);

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
            if (_sceneModel == null || string.IsNullOrEmpty(_sceneModel.Name))
            {
                LogService.LogWarning("No model loaded to name the screenshot automatically. Please load a model first.");
                return;
            }

            string modelName = _sceneModel.Name;
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
    }
}