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

namespace AssetsManager.Views.Controls.Models
{
    /// <summary>
    /// Interaction logic for ModelViewportControl.xaml
    /// </summary>
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

            Viewport.Children.Add(_skeletonVisual);
            Viewport.Children.Add(_jointsVisual);

            CompositionTarget.Rendering += CompositionTarget_Rendering;
            _stopwatch.Start();
        }

        public void SetAnimation(IAnimationAsset animation)
        {
            _currentAnimation = animation;
            _stopwatch.Restart();
            IsAnimationPaused = false;
        }

        public void TogglePauseResume()
        {
            if (_currentAnimation == null) return;

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

        public void SetSkeleton(RigResource skeleton)
        {
            _skeleton = skeleton;
        }

        public void SetModel(SceneModel model)
        {
            _sceneModel = model;
            Viewport.Children.Add(model.RootVisual);
        }

        private void CompositionTarget_Rendering(object sender, System.EventArgs e)
        {
            if (_animationPlayer != null && _currentAnimation != null && _skeleton != null && _sceneModel != null && _sceneModel.SkinnedMesh != null)
            {
                _animationPlayer.Update((float)_stopwatch.Elapsed.TotalSeconds, _currentAnimation, _skeleton, _sceneModel.SkinnedMesh, _sceneModel.Parts.ToList(), _skeletonVisual, _jointsVisual);
            }
        }

        public void ResetCamera()
        {
            if (Viewport.Camera is not PerspectiveCamera camera) return;

            var position = new Point3D(-14.158, 352.651, 553.062);
            var lookDirection = new Vector3D(-2.059, -235.936, -598.980);
            var upDirection = new Vector3D(0.008, 0.930, -0.367);

            camera.Position = position;
            camera.LookDirection = lookDirection;
            camera.UpDirection = upDirection;
            camera.FieldOfView = 45;
        }

        public void TakeScreenshot(string filePath)
        {
            var renderBitmap = new RenderTargetBitmap((int)Viewport3D.ActualWidth, (int)Viewport3D.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            renderBitmap.Render(Viewport3D);

            // Always use PngBitmapEncoder
            BitmapEncoder bitmapEncoder = new PngBitmapEncoder();
            bitmapEncoder.Frames.Add(BitmapFrame.Create(renderBitmap));

            // Ensure the file path has a .png extension
            string finalFilePath = filePath;
            if (Path.GetExtension(finalFilePath).ToLower() != ".png")
            {
                finalFilePath = Path.ChangeExtension(finalFilePath, ".png");
            }

            try
            {
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
        }
    }
}