using System;
using System.Windows;
using System.Windows.Controls;
using AssetsManager.Services.Core;
using AssetsManager.Services.Viewer;
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

            _logService = logService;
            _taskCancellationManager = taskCancellationManager;

            // Service injection (Peer-to-Peer Support)
            ViewportControl.LogService = _logService;
            ViewportControl.AppSettings = appSettings;

            PanelControl.SknLoadingService = sknLoadingService;
            PanelControl.ScoLoadingService = scoLoadingService;
            PanelControl.MapGeometryLoadingService = mapGeometryLoadingService;
            PanelControl.ChromaScannerService = chromaScannerService;
            PanelControl.LogService = _logService;
            PanelControl.CustomMessageBoxService = customMessageBoxService;
            PanelControl.TaskCancellationManager = _taskCancellationManager;
            PanelControl.WindowViewModel = _viewModel;

            ChromaSelectionOverlay.ScannerService = chromaScannerService;

            // Peer-to-Peer wiring between sub-controls
            PanelControl.Viewport = ViewportControl;
            PanelControl.ViewModel.ViewportViewModel = ViewportControl.ViewModel;
            PanelControl.ChromaGallery = ChromaSelectionOverlay;

            ViewportControl.Panel = PanelControl;

            ChromaSelectionOverlay.ParentPanel = PanelControl;
        }

        // Empty-state handlers: thin 1-liners that delegate to the Panel
        private async void OpenFile_Click(object sender, RoutedEventArgs e) => await PanelControl.OpenSknModel();
        private void OpenChromaFile_Click(object sender, RoutedEventArgs e) => PanelControl.OpenChromaFolder();
        private async void OpenGeometryFile_Click(object sender, RoutedEventArgs e) => await PanelControl.OpenMapGeometry();

        public void CleanupResources()
        {
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
    }
}
