using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Media3D;
using Microsoft.WindowsAPICodePack.Dialogs;
using AssetsManager.Views.Camera;
using AssetsManager.Services.Core;
using AssetsManager.Services.Models;
using AssetsManager.Views.Helpers;

namespace AssetsManager.Views
{
    public partial class ModelWindow : UserControl
    {
        private readonly ModelLoadingService _modelLoadingService;
        private readonly LogService _logService;
        private readonly CustomMessageBoxService _customMessageBoxService;
        private readonly CustomCameraController _cameraController;

        public ModelWindow(ModelLoadingService modelLoadingService, LogService logService, CustomMessageBoxService customMessageBoxService)
        {
            InitializeComponent();
            _modelLoadingService = modelLoadingService;
            _logService = logService;
            _customMessageBoxService = customMessageBoxService;
            _cameraController = new CustomCameraController(ViewportControl.Viewport);

            // Inject services into controls
            ViewportControl.LogService = _logService;
            PanelControl.ModelLoadingService = _modelLoadingService;
            PanelControl.LogService = _logService;
            PanelControl.CustomMessageBoxService = _customMessageBoxService;
            
            PanelControl.ModelRemovedFromViewport += (model) => ViewportControl.Viewport.Children.Remove(model.RootVisual);
            PanelControl.AnimationReadyForDisplay += (s, anim) => ViewportControl.SetAnimation(anim);
            PanelControl.AnimationStopRequested += PanelControl_AnimationStopRequested;

            // Model loading events
            PanelControl.SceneSetupRequested += SetupScene;
            PanelControl.ModelReadyForViewport += (model) => ViewportControl.SetModel(model);
            PanelControl.SkeletonReadyForViewport += (skeleton) => ViewportControl.SetSkeleton(skeleton);
            PanelControl.CameraResetRequested += () => ViewportControl.ResetCamera();
            PanelControl.EmptyStateVisibilityChanged += (visibility) => EmptyStatePanel.Visibility = visibility;
            PanelControl.MainContentVisibilityChanged += (visibility) => MainContentGrid.Visibility = visibility;
        }

        private void PanelControl_AnimationStopRequested(object sender, System.EventArgs e)
        {
            ViewportControl.TogglePauseResume();
        }

        private void SetupScene()
        {
            var ground = SceneElements.CreateGroundPlane(path => _modelLoadingService.LoadTexture(path), _logService.LogError);
            var sky = SceneElements.CreateSidePlanes(path => _modelLoadingService.LoadTexture(path), _logService.LogError);

            ViewportControl.Viewport.Children.Add(ground);
            ViewportControl.Viewport.Children.Add(sky);
        }

        private void OpenFile_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new CommonOpenFileDialog
            {
                Filters = { new CommonFileDialogFilter("3D Model Files", "*.skn;*.skl"), new CommonFileDialogFilter("All Files", "*.*") }
            };

            if (openFileDialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                var extension = Path.GetExtension(openFileDialog.FileName).ToLower();
                if (extension == ".skl")
                {
                    PanelControl.LoadSkeleton(openFileDialog.FileName);
                }
                else if (extension == ".skn")
                {
                    PanelControl.LoadInitialModel(openFileDialog.FileName);
                }
            }
        }

    }
}
