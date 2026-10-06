using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using AssetsManager.Services.Core;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Utils;
using AssetsManager.Views.Controls.Viewer;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Views
{
    public partial class ViewerWindow : UserControl
    {
        public ViewerWindowModel ViewModel => _viewModel;

        private readonly ViewerWindowModel _viewModel;
        private readonly LogService _logService;
        private readonly AppSettings _appSettings;
        private readonly SknLoadingService _sknLoadingService;
        private readonly MapViewerSceneService _mapViewerSceneService;
        private readonly ChromaLoadingService _chromaLoadingService;
        private readonly VfxLoadingService _vfxLoadingService;
        private readonly CustomMessageBoxService _customMessageBoxService;
        private Grid _projectView;
        private ViewerViewportControl _viewportControl;
        private ViewerPanelControl _panelControl;
        private ViewerProjectExplorerControl _projectExplorer;
        private RowDefinition _projectExplorerRow;
        private double _lastExplorerHeight = 220;
        private StudioControl _studioControl;
        private bool _isCleanedUp;

        public ViewerWindow(
            LogService logService,
            AppSettings appSettings,
            SknLoadingService sknLoadingService,
            MapViewerSceneService mapViewerSceneService,
            ChromaLoadingService chromaLoadingService,
            VfxLoadingService vfxLoadingService,
            CustomMessageBoxService customMessageBoxService)
        {
            _viewModel = new ViewerWindowModel();
            _logService = logService;
            _appSettings = appSettings;
            _sknLoadingService = sknLoadingService;
            _mapViewerSceneService = mapViewerSceneService;
            _chromaLoadingService = chromaLoadingService;
            _vfxLoadingService = vfxLoadingService;
            _customMessageBoxService = customMessageBoxService;

            InitializeComponent();
            DataContext = _viewModel;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            Unloaded += OnViewerUnloaded;
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_isCleanedUp) return;

            if (e.PropertyName == nameof(ViewerWindowModel.IsProjectExplorerVisible))
            {
                UpdateProjectExplorerRowHeight();
                return;
            }
            if (e.PropertyName != nameof(ViewerWindowModel.IsStudioVisible)) return;

            if (_viewModel.IsStudioVisible)
            {
                if (_studioControl == null)
                {
                    _studioControl = new StudioControl
                    {
                        ChromaLoadingService = _chromaLoadingService,
                        CustomMessageBoxService = _customMessageBoxService,
                        LogService = _logService,
                        AppSettings = _appSettings,
                        SknLoadingService = _sknLoadingService,
                        VfxLoadingService = _vfxLoadingService,
                        MapViewerSceneService = _mapViewerSceneService
                    };
                    _studioControl.ExitRequested += OnStudioExitRequested;
                    StudioHost.Content = _studioControl;
                }
                _studioControl.Activate();
            }
            else
            {
                _studioControl?.Deactivate();
            }
        }

        private void OnStudioExitRequested(object sender, EventArgs e) => _viewModel.IsStudioVisible = false;

        private void OnViewerUnloaded(object sender, RoutedEventArgs e) => CleanupResources();

        private void OpenStudio_Click(object sender, RoutedEventArgs e) => _viewModel.IsStudioVisible = true;

        private void OpenProjectFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_isCleanedUp) return;

            var folderBrowser = new OpenFolderDialog { Title = "Select extracted WAD root folder" };
            if (folderBrowser.ShowDialog() != true) return;

            OpenProject(folderBrowser.FolderName);
        }

        private void OpenProject(string folderPath)
        {
            if (_isCleanedUp) return;

            if (_projectView == null)
                CreateProjectView();

            _projectExplorer.LoadProjectFolder(folderPath);
            _viewModel.IsProjectExplorerVisible = true;
            _panelControl.ViewModel.ShowMainContent();
        }

        private void CreateProjectView()
        {
            // Keep the template unmaterialized until the user accepts a project folder.
            _projectView = (Grid)((DataTemplate)Resources["ProjectViewTemplate"]).LoadContent();
            _viewportControl = (ViewerViewportControl)_projectView.FindName("ViewportControl");
            _panelControl = (ViewerPanelControl)_projectView.FindName("PanelControl");
            _projectExplorer = (ViewerProjectExplorerControl)_projectView.FindName("ProjectExplorer");
            _projectExplorerRow = (RowDefinition)_projectView.FindName("ProjectExplorerRow");

            _viewportControl.LogService = _logService;
            _viewportControl.AppSettings = _appSettings;
            _panelControl.SknLoadingService = _sknLoadingService;
            _panelControl.LogService = _logService;
            _panelControl.CustomMessageBoxService = _customMessageBoxService;
            _panelControl.WindowViewModel = _viewModel;
            _panelControl.Viewport = _viewportControl;
            _panelControl.ViewModel.ViewportViewModel = _viewportControl.ViewModel;
            _viewportControl.Panel = _panelControl;
            _panelControl.ProjectExplorer = _projectExplorer;

            _projectExplorer.ModelSelected += ProjectExplorer_ModelSelected;
            _projectExplorer.AnimationsSelected += (_, paths) => _panelControl.LoadAnimationsDirectly(paths);
            _projectExplorer.CloseRequested += (_, _) => _viewModel.IsProjectExplorerVisible = false;
            _projectView.DataContext = _panelControl.ViewModel;
            ProjectHost.Content = _projectView;
            UpdateProjectExplorerRowHeight();
        }

        private async void ProjectExplorer_ModelSelected(object sender, string filePath)
        {
            var extension = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
            bool isImage = SupportedFileTypes.IsImage(filePath);
            if (!isImage)
            {
                _projectExplorer.ClearImagePreview();
            }

            if (extension == ".skl")
            {
                _panelControl.LoadSkeleton(filePath);
            }
            else if (isImage)
            {
                ShowProjectImagePreview(filePath);
            }
            else if (extension == ".anm")
            {
                _panelControl.LoadAnimationDirectly(filePath);
            }
            else
            {
                _panelControl.ViewModel.ShowMainContent();
                await _panelControl.LoadInitialModel(filePath);
            }
        }

        private void ShowProjectImagePreview(string filePath)
        {
            try
            {
                _projectExplorer.ShowImagePreview(filePath, TextureUtils.LoadTextureFromFile(filePath));
            }
            catch (Exception ex)
            {
                _projectExplorer.ClearImagePreview();
                _logService.LogError(ex, $"[IMAGE PREVIEW] Failed to load preview image: {filePath}");
            }
        }

        private void UpdateProjectExplorerRowHeight()
        {
            if (_projectExplorerRow == null) return;

            if (_viewModel.IsProjectExplorerVisible)
            {
                _projectExplorerRow.MinHeight = 120;
                _projectExplorerRow.Height = new GridLength(_lastExplorerHeight > 0 ? _lastExplorerHeight : 220);
            }
            else
            {
                // Save current height if it's set and greater than 0
                if (_projectExplorerRow.Height.IsAbsolute && _projectExplorerRow.Height.Value > 0)
                {
                    _lastExplorerHeight = _projectExplorerRow.Height.Value;
                }
                _projectExplorerRow.MinHeight = 0;
                _projectExplorerRow.Height = new GridLength(0);
            }
        }

        public void CleanupResources()
        {
            if (_isCleanedUp) return;
            _isCleanedUp = true;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            Unloaded -= OnViewerUnloaded;
            _viewModel.IsStudioVisible = false;
            if (_studioControl != null)
                _studioControl.ExitRequested -= OnStudioExitRequested;
            if (_projectExplorer != null)
                _projectExplorer.ModelSelected -= ProjectExplorer_ModelSelected;

            // A failed teardown must not prevent the other route or the shared loader from being released.
            RunCleanupStep(nameof(StudioControl), () => _studioControl?.Cleanup());
            RunCleanupStep(nameof(ViewerProjectExplorerControl), () => _projectExplorer?.ClearWorkspace());
            RunCleanupStep(nameof(ViewerViewportControl), () => _viewportControl?.Cleanup());
            RunCleanupStep(nameof(ViewerPanelControl), () => _panelControl?.Cleanup());
            RunCleanupStep(nameof(VfxLoadingService), () => _vfxLoadingService.Dispose());
        }

        private void RunCleanupStep(string componentName, Action cleanup)
        {
            try
            {
                cleanup();
            }
            catch (Exception ex)
            {
                _logService.LogDebug($"Notice during ViewerWindow {componentName} cleanup: {ex.Message}");
            }
        }
    }
}
