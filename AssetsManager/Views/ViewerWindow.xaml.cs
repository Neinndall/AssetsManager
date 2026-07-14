using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
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
    public partial class ViewerWindow : UserControl, IDisposable
    {
        public ViewerWindowModel ViewModel => _viewModel;

        private readonly ViewerWindowModel _viewModel;
        private readonly LogService _logService;
        private readonly TaskCancellationManager _taskCancellationManager;
        private bool _isCleanedUp;

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
            PanelControl.ScoLoadingService = serviceProvider.GetRequiredService<ScoLoadingService>();
            PanelControl.MapGeometryLoadingService = serviceProvider.GetRequiredService<MapGeometryLoadingService>();
            PanelControl.ChromaScannerService = serviceProvider.GetRequiredService<ChromaScannerService>();
            PanelControl.LogService = _logService;
            PanelControl.CustomMessageBoxService = serviceProvider.GetRequiredService<CustomMessageBoxService>();
            PanelControl.TaskCancellationManager = _taskCancellationManager;
            PanelControl.WindowViewModel = _viewModel;
 
            ChromaSelectionOverlay.ScannerService = serviceProvider.GetRequiredService<ChromaScannerService>();

            // Peer-to-Peer wiring between sub-controls
            PanelControl.Viewport = ViewportControl;
            PanelControl.ViewModel.ViewportViewModel = ViewportControl.ViewModel;
            PanelControl.ChromaGallery = ChromaSelectionOverlay;

            ViewportControl.Panel = PanelControl;

            ChromaSelectionOverlay.ParentPanel = PanelControl;

            Loaded += (s, e) => _isCleanedUp = false;
        }

        // Empty-state handlers: thin 1-liners that delegate to the Panel
        private async void OpenFile_Click(object sender, RoutedEventArgs e) => await PanelControl.OpenSknModel();
        private void OpenChromaFile_Click(object sender, RoutedEventArgs e) => PanelControl.OpenChromaFolder();
        private async void OpenGeometryFile_Click(object sender, RoutedEventArgs e) => await PanelControl.OpenMapGeometry();

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
