using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Media3D;
using Microsoft.WindowsAPICodePack.Dialogs;
using AssetsManager.Services.Core;
using AssetsManager.Services.Viewer;
using AssetsManager.Views.Helpers;
using AssetsManager.Utils;
using System.Threading.Tasks;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Views
{
    public partial class ViewerWindow : UserControl
    {
        private readonly ViewerWindowModel _viewModel;
        public ViewerWindowModel ViewModel => _viewModel;

        private readonly SknLoadingService _sknLoadingService;
        private readonly ScoLoadingService _scoLoadingService;
        private readonly MapGeometryLoadingService _mapGeometryLoadingService;
        private readonly ChromaScannerService _chromaScannerService;
        private readonly LogService _logService;
        private readonly AppSettings _appSettings;
        private readonly TaskCancellationManager _taskCancellationManager;

        private ModelVisual3D _groundVisual;
        private ModelVisual3D _skyVisual;

        public ViewerWindow(
            SknLoadingService sknLoadingService,
            ScoLoadingService scoLoadingService,
            MapGeometryLoadingService mapGeometryLoadingService,
            ChromaScannerService chromaScannerService,
            LogService logService,
            CustomMessageBoxService customMessageBoxService,
            AppSettings appSettings,
            TaskCancellationManager taskCancellationManager)
        {
            InitializeComponent();

            _viewModel = new ViewerWindowModel();
            DataContext = _viewModel;

            _sknLoadingService = sknLoadingService;
            _scoLoadingService = scoLoadingService;
            _mapGeometryLoadingService = mapGeometryLoadingService;
            _chromaScannerService = chromaScannerService;
            _logService = logService;
            _appSettings = appSettings;
            _taskCancellationManager = taskCancellationManager;

            // Service injection (Peer-to-Peer Support)
            ViewportControl.LogService = _logService;

            PanelControl.SknLoadingService = _sknLoadingService;
            PanelControl.ScoLoadingService = _scoLoadingService;
            PanelControl.MapGeometryLoadingService = _mapGeometryLoadingService;
            PanelControl.ChromaScannerService = _chromaScannerService;
            PanelControl.LogService = _logService;
            PanelControl.CustomMessageBoxService = customMessageBoxService;
            PanelControl.TaskCancellationManager = _taskCancellationManager;
            PanelControl.ParentWindow = this;

            ChromaSelectionOverlay.ScannerService = _chromaScannerService;

            PanelControl.Viewport = ViewportControl;
            PanelControl.ViewModel.ViewportViewModel = ViewportControl.ViewModel;
            PanelControl.ChromaGallery = ChromaSelectionOverlay;

            ViewportControl.Panel = PanelControl;

            ChromaSelectionOverlay.ParentPanel = PanelControl;
        }

        public void ShowLoading(string title, string description)
        {
            _viewModel.LoadingTitle = title;
            _viewModel.LoadingDescription = description;
            _viewModel.IsLoadingVisible = true;
        }

        public void HideLoading()
        {
            _viewModel.IsLoadingVisible = false;
        }

        public void CleanupResources()
        {
            try
            {
                _taskCancellationManager?.CancelCurrentOperation(false);

                if (_groundVisual != null && ViewportControl != null) ViewportControl.Viewport.Children.Remove(_groundVisual);
                if (_skyVisual != null && ViewportControl != null) ViewportControl.Viewport.Children.Remove(_skyVisual);
                _groundVisual = null;
                _skyVisual = null;

                ViewportControl?.Cleanup();
                PanelControl?.Cleanup();
            }
            catch (Exception ex)
            {
                _logService?.LogError(ex, "Error during ViewerWindow.CleanupResources");
            }
        }

        public void SetupScene(bool isMapGeometry)
        {
            if (isMapGeometry)
            {
                if (_skyVisual != null && ViewportControl.Viewport.Children.Contains(_skyVisual))
                    ViewportControl.Viewport.Children.Remove(_skyVisual);
                if (_groundVisual != null && ViewportControl.Viewport.Children.Contains(_groundVisual))
                    ViewportControl.Viewport.Children.Remove(_groundVisual);
                _skyVisual = null;
                _groundVisual = null;
                return;
            }

            if (_groundVisual == null)
            {
                _groundVisual = SceneElements.CreateGroundPlane(p => SceneElements.LoadSceneTexture(p, _logService), _logService.LogError, _appSettings.CustomFloorTexturePath);
                ViewportControl.Viewport.Children.Add(_groundVisual);
            }

            if (_skyVisual == null)
            {
                _skyVisual = SceneElements.CreateSidePlanes(p => SceneElements.LoadSceneTexture(p, _logService), _logService.LogError);
                ViewportControl.Viewport.Children.Add(_skyVisual);
            }

            ViewportControl.RegisterEnvironment(_skyVisual, _groundVisual);
        }

        private async void OpenFile_Click(object sender, RoutedEventArgs e) => await OpenSknModel();

        private void OpenChromaFile_Click(object sender, RoutedEventArgs e) => OpenChromaFolder();

        private async void OpenGeometryFile_Click(object sender, RoutedEventArgs e) => await OpenMapGeometry();

        public async Task OpenSknModel()
        {
            var openFileDialog = new CommonOpenFileDialog
            {
                Filters = { new CommonFileDialogFilter("3D Model Files", "*.skn;*.skl;*.sco;*.scb"), new CommonFileDialogFilter("All Files", "*.*") },
                Title = "Select a model file"
            };

            if (openFileDialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                var extension = Path.GetExtension(openFileDialog.FileName).ToLower();
                if (extension == ".skl") PanelControl.LoadSkeleton(openFileDialog.FileName);
                else await PanelControl.LoadInitialModel(openFileDialog.FileName);
            }
        }

        public void OpenChromaFolder()
        {
            var folderBrowserDialog = new CommonOpenFileDialog { IsFolderPicker = true, Title = "Select the skins folder" };

            if (folderBrowserDialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                PanelControl.HandleChromaGalleryRequest(folderBrowserDialog.FileName);
            }
        }

        public async Task OpenMapGeometry()
        {
            var openMapGeoDialog = new CommonOpenFileDialog { Filters = { new CommonFileDialogFilter("MapGeometry Files", "*.mapgeo") }, Title = "Select a mapgeo file" };

            if (openMapGeoDialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                string mapGeoPath = openMapGeoDialog.FileName;
                string materialsBinPath = Path.ChangeExtension(mapGeoPath, ".materials.bin");

                var openGameDataDialog = new CommonOpenFileDialog { IsFolderPicker = true, Title = "Select map root" };
                if (openGameDataDialog.ShowDialog() == CommonFileDialogResult.Ok)
                {
                    ShowLoading(ViewerWindowModel.MapGeoLoadingTitle, ViewerWindowModel.MapGeoLoadingDescription);
                    await PanelControl.LoadMapGeometry(mapGeoPath, materialsBinPath, openGameDataDialog.FileName);
                    HideLoading();
                }
            }
        }
    }
}
