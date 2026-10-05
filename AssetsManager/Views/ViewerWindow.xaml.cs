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
        private readonly TaskCancellationManager _taskCancellationManager;
        private readonly AppSettings _appSettings;
        private readonly SknLoadingService _sknLoadingService;
        private readonly MapViewerSceneService _mapViewerSceneService;
        private readonly ChromaLoadingService _chromaLoadingService;
        private readonly VfxLoadingService _vfxLoadingService;
        private readonly CustomMessageBoxService _customMessageBoxService;
        private ViewerProjectControl _projectControl;
        private StudioControl _studioControl;
        private bool _isCleanedUp;

        public ViewerWindow(
            LogService logService,
            TaskCancellationManager taskCancellationManager,
            AppSettings appSettings,
            SknLoadingService sknLoadingService,
            MapViewerSceneService mapViewerSceneService,
            ChromaLoadingService chromaLoadingService,
            VfxLoadingService vfxLoadingService,
            CustomMessageBoxService customMessageBoxService)
        {
            _viewModel = new ViewerWindowModel();
            _logService = logService;
            _taskCancellationManager = taskCancellationManager;
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
            if (_isCleanedUp || e.PropertyName != nameof(ViewerWindowModel.IsStudioVisible)) return;

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

            if (_projectControl == null)
            {
                _projectControl = new ViewerProjectControl(_viewModel, _logService,
                    _taskCancellationManager, _appSettings, _sknLoadingService, _customMessageBoxService);
                ProjectHost.Content = _projectControl;
            }
            _projectControl.OpenProject(folderPath);
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

            // A failed teardown must not prevent the other route or the shared loader from being released.
            RunCleanupStep(nameof(StudioControl), () => _studioControl?.Cleanup());
            RunCleanupStep(nameof(ViewerProjectControl), () => _projectControl?.Cleanup());
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
