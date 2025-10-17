using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Media3D;
using Microsoft.WindowsAPICodePack.Dialogs;
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
        private bool _isMapGeometry = false;
        private ModelVisual3D _groundVisual;
        private ModelVisual3D _skyVisual;

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

            // Scene events
            PanelControl.SceneClearRequested += OnSceneClearRequested;
            PanelControl.SceneSetupRequested += SetupScene;
            PanelControl.CameraResetRequested += () => ViewportControl.ResetCamera();
            PanelControl.EmptyStateVisibilityChanged += (visibility) => EmptyStatePanel.Visibility = visibility;
            PanelControl.MainContentVisibilityChanged += (visibility) => MainContentGrid.Visibility = visibility;

            // Model events
            PanelControl.ModelReadyForViewport += (model) => ViewportControl.SetModel(model);
            PanelControl.ModelRemovedFromViewport += (model) => ViewportControl.Viewport.Children.Remove(model.RootVisual);
            PanelControl.SkeletonReadyForViewport += (skeleton) => ViewportControl.SetSkeleton(skeleton);

            // Animation events
            PanelControl.AnimationReadyForDisplay += (s, anim) => ViewportControl.SetAnimation(anim);
            PanelControl.AnimationStopRequested += (s, animAsset) => ViewportControl.TogglePauseResume(animAsset);
        }

        private void OnSceneClearRequested(object sender, EventArgs e)
        {
            ViewportControl.StopAnimation();
            ViewportControl.ResetCamera();
        }

        private void SetupScene()
        {
            // If we are loading a map, stop here.
            if (_isMapGeometry)
            {
                return;
            }

            // Otherwise, create and add them
            _groundVisual = SceneElements.CreateGroundPlane(path => _modelLoadingService.LoadTexture(path), _logService.LogError);
            _skyVisual = SceneElements.CreateSidePlanes(path => _modelLoadingService.LoadTexture(path), _logService.LogError);

            ViewportControl.Viewport.Children.Add(_groundVisual);
            ViewportControl.Viewport.Children.Add(_skyVisual);
        }

        private void OpenFile_Click(object sender, RoutedEventArgs e)
        {
            _isMapGeometry = false;
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

        private async void OpenGeometryFile_Click(object sender, RoutedEventArgs e)
        {
            _isMapGeometry = true;
            var openMapGeoDialog = new CommonOpenFileDialog
            {
                Filters = { new CommonFileDialogFilter("Map Geometry Files", "*.mapgeo"), new CommonFileDialogFilter("All Files", "*.*") }
            };

            if (openMapGeoDialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                var openMaterialsBinDialog = new CommonOpenFileDialog
                {
                    Filters = { new CommonFileDialogFilter("Materials files", "*.materials.bin"), new CommonFileDialogFilter("All files", "*.*") },
                    Title = "Select a materials.bin File"
                };

                if (openMaterialsBinDialog.ShowDialog() == CommonFileDialogResult.Ok)
                {
                    var openGameDataDialog = new CommonOpenFileDialog
                    {
                        IsFolderPicker = true,
                        Title = "Select Map Root for Textures"
                    };

                    if (openGameDataDialog.ShowDialog() == CommonFileDialogResult.Ok)
                    {
                        await PanelControl.LoadMapGeometry(openMapGeoDialog.FileName, openMaterialsBinDialog.FileName, openGameDataDialog.FileName);
                    }
                    else
                    {
                        _customMessageBoxService.ShowWarning("Game Data Path Not Selected", "Map geometry cannot be loaded without the game data root folder.");
                    }
                }
                else
                {
                    _customMessageBoxService.ShowWarning("Materials.bin Not Selected", "Map geometry cannot be loaded without the materials.bin file.");
                }
            }
        }

    }
}
