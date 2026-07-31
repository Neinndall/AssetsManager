using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.WindowsAPICodePack.Dialogs;
using AssetsManager.Services.Core;
using AssetsManager.Services.Viewer.MapGeometry;
using AssetsManager.Services.Viewer.Models;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Views
{
    /// <summary>
    /// Passive orchestrator for the Viewer module.
    /// Responsibility: Dependency Injection and Peer-to-Peer linking between sub-controls.
    /// </summary>
    public partial class ViewerWindow : UserControl, IDisposable
    {
        public ViewerWindowModel ViewModel => _viewModel;

        private readonly ViewerWindowModel _viewModel;
        private readonly LogService _logService;
        private readonly TaskCancellationManager _taskCancellationManager;
        private bool _isCleanedUp;
        private double _lastExplorerHeight = 220;

        public ViewerWindow(IServiceProvider serviceProvider)
        {
            InitializeComponent();
 
            _viewModel = new ViewerWindowModel();
            DataContext = _viewModel;
 
            _logService = serviceProvider.GetRequiredService<LogService>();
            _taskCancellationManager = serviceProvider.GetRequiredService<TaskCancellationManager>();
 
            // Service injection (Peer-to-Peer Support)
            ViewportControl.LogService = _logService;
            ViewportControl.AppSettings = serviceProvider.GetRequiredService<AppSettings>();
 
            PanelControl.SknLoadingService = serviceProvider.GetRequiredService<SknLoadingService>();
            PanelControl.MapGeometryLoadingService = serviceProvider.GetRequiredService<MapGeometryLoadingService>();
            PanelControl.ChromaScannerService = serviceProvider.GetRequiredService<ChromaScannerService>();
            PanelControl.LogService = _logService;
            PanelControl.CustomMessageBoxService = serviceProvider.GetRequiredService<CustomMessageBoxService>();
            PanelControl.TaskCancellationManager = _taskCancellationManager;
            PanelControl.WindowViewModel = _viewModel;
            PanelControl.ProjectExplorer = ProjectExplorer;
 
            ChromaSelectionOverlay.ScannerService = serviceProvider.GetRequiredService<ChromaScannerService>();

            // Peer-to-Peer wiring between sub-controls
            PanelControl.Viewport = ViewportControl;
            PanelControl.ViewModel.ViewportViewModel = ViewportControl.ViewModel;
            PanelControl.ChromaGallery = ChromaSelectionOverlay;

            ViewportControl.Panel = PanelControl;

            PanelControl.ProjectExplorer = ProjectExplorer;
            ChromaSelectionOverlay.ParentPanel = PanelControl;

            // Project Explorer event wiring
            ProjectExplorer.ModelSelected += ProjectExplorer_ModelSelected;
            ProjectExplorer.CloseRequested += (s, e) => _viewModel.IsProjectExplorerVisible = false;

            _viewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(ViewerWindowModel.IsProjectExplorerVisible))
                {
                    UpdateProjectExplorerRowHeight();
                }
            };

            // Set initial state
            UpdateProjectExplorerRowHeight();

            Loaded += (s, e) => _isCleanedUp = false;
        }

        // Empty-state handlers: thin 1-liners that delegate to the Panel
        private async void OpenFile_Click(object sender, RoutedEventArgs e) => await PanelControl.OpenSknModel();
        private void OpenChromaFile_Click(object sender, RoutedEventArgs e) => PanelControl.OpenChromaFolder();
        private async void OpenGeometryFile_Click(object sender, RoutedEventArgs e) => await PanelControl.OpenMapGeometry();

        private void OpenVfxInspector_Click(object sender, RoutedEventArgs e)
        {
            var window = new AssetsManager.Views.Dialogs.VfxInspectorWindow(_logService)
            {
                Owner = Window.GetWindow(this)
            };
            window.ShowDialog();
        }

        private void OpenProjectFolder_Click(object sender, RoutedEventArgs e)
        {
            var folderBrowser = new CommonOpenFileDialog { IsFolderPicker = true, Title = "Select extracted WAD root folder" };
            if (folderBrowser.ShowDialog() == CommonFileDialogResult.Ok)
            {
                ProjectExplorer.LoadProjectFolder(folderBrowser.FileName);
                _viewModel.IsProjectExplorerVisible = true;
                PanelControl.ViewModel.ShowMainContent();
            }
        }

        private async void ProjectExplorer_ModelSelected(object sender, string filePath)
        {
            var extension = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
            if (extension == ".skl")
            {
                PanelControl.LoadSkeleton(filePath);
            }
            else if (extension == ".dds" || extension == ".tex" || extension == ".png" || extension == ".jpg" || extension == ".tga")
            {
                PanelControl.ShowImagePreview(filePath);
            }
            else if (extension == ".anm")
            {
                PanelControl.LoadAnimationDirectly(filePath);
            }
            else
            {
                PanelControl.ViewModel.ShowMainContent();
                await PanelControl.LoadInitialModel(filePath);
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

                ViewportControl?.Cleanup();
                PanelControl?.Cleanup();
            }
            catch (Exception ex)
            {
                _logService?.LogError(ex, "Error during ViewerWindow.CleanupResources");
            }
        }

        public void Dispose()
        {
            CleanupResources();
        }
    }
}
