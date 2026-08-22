using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using AssetsManager.Services.Core;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Views
{
    /// <summary>
    /// Passive orchestrator for the Viewer module.
    /// Responsibility: Dependency Injection and Peer-to-Peer linking between sub-controls.
    /// </summary>
    public partial class ViewerWindow : UserControl
    {
        public ViewerWindowModel ViewModel => _viewModel;

        private readonly ViewerWindowModel _viewModel;
        private readonly LogService _logService;
        private readonly TaskCancellationManager _taskCancellationManager;
        private readonly VfxLoadingService _vfxLoadingService;
        private bool _isCleanedUp;
        private double _lastExplorerHeight = 220;

        public ViewerWindow(
            LogService logService,
            TaskCancellationManager taskCancellationManager,
            AppSettings appSettings,
            SknLoadingService sknLoadingService,
            MapGeometryLoadingService mapGeometryLoadingService,
            ChromaLoadingService chromaLoadingService,
            VfxLoadingService vfxLoadingService,
            CustomMessageBoxService customMessageBoxService)
        {
            InitializeComponent();
 
            _viewModel = new ViewerWindowModel();
            DataContext = _viewModel;
 
            _logService = logService;
            _taskCancellationManager = taskCancellationManager;
            _vfxLoadingService = vfxLoadingService;
 
            // Service injection (Peer-to-Peer Support)
            ViewportControl.LogService = _logService;
            ViewportControl.AppSettings = appSettings;

            PanelControl.SknLoadingService = sknLoadingService;
            PanelControl.MapGeometryLoadingService = mapGeometryLoadingService;
            PanelControl.ChromaLoadingService = chromaLoadingService;
            PanelControl.LogService = _logService;
            PanelControl.CustomMessageBoxService = customMessageBoxService;
            PanelControl.TaskCancellationManager = _taskCancellationManager;
            PanelControl.WindowViewModel = _viewModel;

            ChromaSelectionControl.ChromaLoadingService = chromaLoadingService;

            VfxInspectorControl.LogService = _logService;
            VfxInspectorControl.VfxLoadingService = _vfxLoadingService;

            // Peer-to-Peer wiring between sub-controls
            PanelControl.Viewport = ViewportControl;
            PanelControl.ViewModel.ViewportViewModel = ViewportControl.ViewModel;
            PanelControl.ChromaGallery = ChromaSelectionControl;

            ViewportControl.Panel = PanelControl;

            PanelControl.ProjectExplorer = ProjectExplorer;
            ChromaSelectionControl.ParentPanel = PanelControl;

            // Project Explorer event wiring
            ProjectExplorer.ModelSelected += ProjectExplorer_ModelSelected;
            ProjectExplorer.AnimationsSelected += (_, paths) => PanelControl.LoadAnimationsDirectly(paths);
            ProjectExplorer.CloseRequested += (s, e) => _viewModel.IsProjectExplorerVisible = false;

            _viewModel.PropertyChanged += OnViewModelPropertyChanged;

            // Set initial state
            UpdateProjectExplorerRowHeight();

            Unloaded += OnViewerUnloaded;
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewerWindowModel.IsProjectExplorerVisible))
            {
                UpdateProjectExplorerRowHeight();
            }

            if (e.PropertyName == nameof(ViewerWindowModel.IsVfxStudioVisible))
            {
                if (_viewModel.IsVfxStudioVisible)
                {
                    VfxInspectorControl.Activate();
                }
                else
                {
                    VfxInspectorControl.Deactivate();
                }
            }
        }

        private void OnViewerUnloaded(object sender, RoutedEventArgs e)
        {
            CleanupResources();
        }

        // Empty-state handlers: thin 1-liners that delegate to the Panel
        private async void OpenFile_Click(object sender, RoutedEventArgs e) => await PanelControl.OpenSknModel();
        private void OpenChromaFile_Click(object sender, RoutedEventArgs e) => PanelControl.OpenChromaFolder();
        private async void OpenGeometryFile_Click(object sender, RoutedEventArgs e) => await PanelControl.OpenMapGeometry();

        private void OpenVfxInspector_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.IsVfxStudioVisible = true;
        }

        private void OpenProjectFolder_Click(object sender, RoutedEventArgs e)
        {
            var folderBrowser = new OpenFolderDialog { Title = "Select extracted WAD root folder" };
            if (folderBrowser.ShowDialog() == true)
            {
                ProjectExplorer.LoadProjectFolder(folderBrowser.FolderName);
                _viewModel.IsProjectExplorerVisible = true;
                PanelControl.ViewModel.ShowMainContent();
            }
        }

        private async void ProjectExplorer_ModelSelected(object sender, string filePath)
        {
            var extension = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
            bool isImage = SupportedFileTypes.IsImage(filePath);
            if (!isImage)
            {
                ProjectExplorer.ClearImagePreview();
            }

            if (extension == ".skl")
            {
                PanelControl.LoadSkeleton(filePath);
            }
            else if (isImage)
            {
                ShowProjectImagePreview(filePath);
            }
            else if (extension == ".anm")
            {
                PanelControl.LoadAnimationDirectly(filePath);
            }
            else if (extension == ".mapgeo")
            {
                string materialsBinPath = System.IO.Path.ChangeExtension(filePath, ".materials.bin");
                string gameDataPath = !string.IsNullOrEmpty(ProjectExplorer?.CurrentRootFolder)
                    ? ProjectExplorer.CurrentRootFolder
                    : System.IO.Path.GetDirectoryName(filePath);
                _viewModel.LoadingTitle = ViewerWindowModel.MapGeoLoadingTitle;
                _viewModel.LoadingDescription = ViewerWindowModel.MapGeoLoadingDescription;
                _viewModel.IsLoadingVisible = true;

                await PanelControl.LoadMapGeometry(filePath, materialsBinPath, gameDataPath);

                _viewModel.IsLoadingVisible = false;
            }
            else
            {
                PanelControl.ViewModel.ShowMainContent();
                await PanelControl.LoadInitialModel(filePath);
            }
        }

        private void ShowProjectImagePreview(string filePath)
        {
            try
            {
                ProjectExplorer.ShowImagePreview(filePath, TextureUtils.LoadTextureFromFile(filePath));
            }
            catch (Exception ex)
            {
                ProjectExplorer.ClearImagePreview();
                _logService.LogError(ex, $"[IMAGE PREVIEW] Failed to load preview image: {filePath}");
            }
        }

        private void UpdateProjectExplorerRowHeight()
        {
            if (ProjectExplorerRow == null) return;

            if (_viewModel.IsProjectExplorerVisible)
            {
                ProjectExplorerRow.MinHeight = 120;
                ProjectExplorerRow.Height = new GridLength(_lastExplorerHeight > 0 ? _lastExplorerHeight : 220);
            }
            else
            {
                // Save current height if it's set and greater than 0
                if (ProjectExplorerRow.Height.IsAbsolute && ProjectExplorerRow.Height.Value > 0)
                {
                    _lastExplorerHeight = ProjectExplorerRow.Height.Value;
                }
                ProjectExplorerRow.MinHeight = 0;
                ProjectExplorerRow.Height = new GridLength(0);
            }
        }

        public void CleanupResources()
        {
            if (_isCleanedUp) return;
            _isCleanedUp = true;

            try
            {
                _taskCancellationManager?.CancelCurrentOperation(false);
                _viewModel.IsVfxStudioVisible = false;

                // Release the VFX consumer before disposing the service it uses.
                VfxInspectorControl?.Cleanup();
                ViewportControl?.Cleanup();
                PanelControl?.Cleanup();
            }
            catch (Exception ex)
            {
                _logService?.LogError(ex, "Error during ViewerWindow.CleanupResources");
            }
            finally
            {
                try
                {
                    _vfxLoadingService?.Dispose();
                }
                catch (Exception ex)
                {
                    _logService?.LogError(ex, "Error during VfxLoadingService cleanup");
                }
            }
        }
    }
}
