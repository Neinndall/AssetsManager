using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using AssetsManager.Services.Core;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Views.Controls.Viewer
{
    public partial class ViewerProjectControl : UserControl
    {
        public ViewerPanelModel ViewModel => PanelControl.ViewModel;

        private readonly ViewerWindowModel _viewModel;
        private readonly LogService _logService;
        private bool _isCleanedUp;
        private double _lastExplorerHeight = 220;

        public ViewerProjectControl(
            ViewerWindowModel windowModel,
            LogService logService,
            TaskCancellationManager taskCancellationManager,
            AppSettings appSettings,
            SknLoadingService sknLoadingService,
            CustomMessageBoxService customMessageBoxService)
        {
            InitializeComponent();
            _viewModel = windowModel;
            _logService = logService;
            DataContext = windowModel;

            ViewportControl.LogService = logService;
            ViewportControl.AppSettings = appSettings;
            PanelControl.SknLoadingService = sknLoadingService;
            PanelControl.LogService = logService;
            PanelControl.CustomMessageBoxService = customMessageBoxService;
            PanelControl.TaskCancellationManager = taskCancellationManager;
            PanelControl.WindowViewModel = windowModel;
            PanelControl.Viewport = ViewportControl;
            PanelControl.ViewModel.ViewportViewModel = ViewportControl.ViewModel;
            ViewportControl.Panel = PanelControl;
            PanelControl.ProjectExplorer = ProjectExplorer;

            ProjectExplorer.ModelSelected += ProjectExplorer_ModelSelected;
            ProjectExplorer.AnimationsSelected += (_, paths) => PanelControl.LoadAnimationsDirectly(paths);
            ProjectExplorer.CloseRequested += (_, _) => _viewModel.IsProjectExplorerVisible = false;
            _viewModel.PropertyChanged += OnWindowModelPropertyChanged;
            UpdateProjectExplorerRowHeight();
        }

        public void OpenProject(string folderPath)
        {
            if (_isCleanedUp) return;

            ProjectExplorer.LoadProjectFolder(folderPath);
            _viewModel.IsProjectExplorerVisible = true;
            ViewModel.ShowMainContent();
        }

        private void OnWindowModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewerWindowModel.IsProjectExplorerVisible))
                UpdateProjectExplorerRowHeight();
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

        public void Cleanup()
        {
            if (_isCleanedUp) return;
            _isCleanedUp = true;
            _viewModel.PropertyChanged -= OnWindowModelPropertyChanged;
            ProjectExplorer.ModelSelected -= ProjectExplorer_ModelSelected;
            try
            {
                ProjectExplorer.ClearWorkspace();
            }
            finally
            {
                try
                {
                    ViewportControl.Cleanup();
                }
                finally
                {
                    PanelControl.Cleanup();
                }
            }
        }
    }
}
