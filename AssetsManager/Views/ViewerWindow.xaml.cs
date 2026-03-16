using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Media3D;
using Microsoft.WindowsAPICodePack.Dialogs;
using AssetsManager.Services.Core;
using AssetsManager.Services.Viewer;
using AssetsManager.Views.Helpers;
using System.Windows.Media.Imaging;
using AssetsManager.Utils;
using System.Threading.Tasks;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Views
{
    /// <summary>
    /// Passive Container for the Viewer Module.
    /// Responsibility: Dependency Injection and initial Peer-to-Peer linking.
    /// </summary>
    public partial class ViewerWindow : UserControl
    {
        private readonly ViewerWindowModel _viewModel;
        public ViewerWindowModel ViewModel => _viewModel;

        private readonly SknLoadingService _sknLoadingService;
        private readonly ScoLoadingService _scoLoadingService;
        private readonly MapGeometryLoadingService _mapGeometryLoadingService;
        private readonly ChromaScannerService _chromaScannerService;
        private readonly LogService _logService;
        private readonly CustomMessageBoxService _customMessageBoxService;
        private readonly CustomCameraController _cameraController;
        private readonly AppSettings _appSettings;

        private bool _isMapGeometry = false;
        private ModelVisual3D _groundVisual;
        private ModelVisual3D _skyVisual;

        public ViewerWindow(
            SknLoadingService sknLoadingService, 
            ScoLoadingService scoLoadingService, 
            MapGeometryLoadingService mapGeometryLoadingService, 
            ChromaScannerService chromaScannerService,
            LogService logService, 
            CustomMessageBoxService customMessageBoxService, 
            AppSettings appSettings)
        {
            InitializeComponent();
            
            _viewModel = new ViewerWindowModel();
            DataContext = _viewModel;

            _sknLoadingService = sknLoadingService;
            _scoLoadingService = scoLoadingService;
            _mapGeometryLoadingService = mapGeometryLoadingService;
            _chromaScannerService = chromaScannerService;
            _logService = logService;
            _customMessageBoxService = customMessageBoxService;
            _appSettings = appSettings;
            
            _cameraController = new CustomCameraController(ViewportControl.Viewport);

            // 1. Service Injection (Peer-to-Peer Support)
            ViewportControl.LogService = _logService;
            
            PanelControl.SknLoadingService = _sknLoadingService;
            PanelControl.ScoLoadingService = _scoLoadingService;
            PanelControl.MapGeometryLoadingService = _mapGeometryLoadingService;
            PanelControl.ChromaScannerService = _chromaScannerService;
            PanelControl.LogService = _logService;
            PanelControl.CustomMessageBoxService = _customMessageBoxService;

            ChromaSelectionOverlay.ScannerService = _chromaScannerService;

            PanelControl.Viewport = ViewportControl;
            PanelControl.ChromaGallery = ChromaSelectionOverlay;
            
            ViewportControl.Panel = PanelControl;
            
            ChromaSelectionOverlay.ParentPanel = PanelControl;

            // 3. Subscription to direct UI state events (Standard pattern)
            PanelControl.EmptyStateVisibilityChanged += (v) => _viewModel.IsMainContentVisible = (v != Visibility.Visible);
            PanelControl.MainContentVisibilityChanged += (v) => _viewModel.IsMainContentVisible = (v == Visibility.Visible);
            ViewportControl.SceneSetupRequested += SetupScene;
            ViewportControl.MapGeometryLoadRequested += OnMapGeometryLoadRequested;
        }

        private void OnMapGeometryLoadRequested() => OpenGeometryFile_Click(null, null);

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
            _cameraController?.Dispose();
            ViewportControl?.Cleanup();

            if (_groundVisual != null) ViewportControl.Viewport.Children.Remove(_groundVisual);
            if (_skyVisual != null) ViewportControl.Viewport.Children.Remove(_skyVisual);

            PanelControl?.Cleanup();
        }

        private void SetupScene()
        {
            if (_isMapGeometry) return;

            if (_groundVisual != null && ViewportControl.Viewport.Children.Contains(_groundVisual))
                ViewportControl.Viewport.Children.Remove(_groundVisual);
            
            if (_skyVisual != null && ViewportControl.Viewport.Children.Contains(_skyVisual))
                ViewportControl.Viewport.Children.Remove(_skyVisual);

            _groundVisual = SceneElements.CreateGroundPlane(path => LoadSceneTexture(path), _logService.LogError, _appSettings.CustomFloorTexturePath);
            _skyVisual = SceneElements.CreateSidePlanes(path => LoadSceneTexture(path), _logService.LogError);

            ViewportControl.Viewport.Children.Add(_groundVisual);
            ViewportControl.Viewport.Children.Add(_skyVisual);
            
            ViewportControl.RegisterEnvironment(_skyVisual, _groundVisual);
            PanelControl?.UpdateEnvironment();
        }

        private BitmapSource LoadSceneTexture(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    using (FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read))
                        return TextureUtils.LoadTexture(fileStream, Path.GetExtension(path), 2048);
                }
                else
                {
                    using (Stream resourceStream = Application.GetResourceStream(new Uri(path)).Stream)
                        return TextureUtils.LoadTexture(resourceStream, Path.GetExtension(path), 2048);
                }
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Failed to load scene texture: {path}");
                return null;
            }
        }

        private void OpenFile_Click(object sender, RoutedEventArgs e)
        {
            _isMapGeometry = false;
            var openFileDialog = new CommonOpenFileDialog
            {
                Filters = { new CommonFileDialogFilter("3D Model Files", "*.skn;*.skl;*.sco;*.scb"), new CommonFileDialogFilter("All Files", "*.*") },
                Title = "Select a model file"
            };

            if (openFileDialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                var extension = Path.GetExtension(openFileDialog.FileName).ToLower();
                if (extension == ".skl") PanelControl.LoadSkeleton(openFileDialog.FileName);
                else PanelControl.LoadInitialModel(openFileDialog.FileName);
            }
        }

        private void OpenChromaFile_Click(object sender, RoutedEventArgs e)
        {
            _isMapGeometry = false;
            var folderBrowserDialog = new CommonOpenFileDialog { IsFolderPicker = true, Title = "Select the 'skins' folder" };

            if (folderBrowserDialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                PanelControl.HandleChromaGalleryRequest(folderBrowserDialog.FileName);
            }
        }

        private async void OpenGeometryFile_Click(object sender, RoutedEventArgs e)
        {
            _isMapGeometry = true;
            var openMapGeoDialog = new CommonOpenFileDialog { Filters = { new CommonFileDialogFilter("MapGeometry Files", "*.mapgeo") }, Title = "Select a mapgeo file" };

            if (openMapGeoDialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                string mapGeoPath = openMapGeoDialog.FileName;
                string materialsBinPath = Path.ChangeExtension(mapGeoPath, ".materials.bin");

                var openGameDataDialog = new CommonOpenFileDialog { IsFolderPicker = true, Title = "Select map root" };
                if (openGameDataDialog.ShowDialog() == CommonFileDialogResult.Ok)
                {
                    ShowLoading("Loading MapGeometry...", "Processing geometry and textures.");
                    await PanelControl.LoadMapGeometry(mapGeoPath, materialsBinPath, openGameDataDialog.FileName);
                    HideLoading();
                }
            }
        }
    }
}
