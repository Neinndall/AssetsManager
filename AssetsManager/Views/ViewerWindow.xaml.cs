using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AssetsManager.Services.Core;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Utils;
using AssetsManager.Views.Controls.Viewer;
using AssetsManager.Views.Models.Viewer;
using Microsoft.Win32;

namespace AssetsManager.Views
{
    public partial class ViewerWindow : UserControl
    {
        private readonly StudioHomeModel _model = new();
        private readonly LogService _logService;
        private readonly AppSettings _appSettings;
        private readonly SknLoadingService _sknLoadingService;
        private readonly MapViewerSceneService _mapSceneService;
        private readonly ChromaLoadingService _chromaLoadingService;
        private readonly VfxLoadingService _vfxLoadingService;
        private readonly CustomMessageBoxService _messageBox;
        private StudioControl _studio;
        private ChromaSelectionControl _homeChromas;
        private bool _isCleanedUp;
        private Task<bool> _studioEntryTask;

        public ViewerWindow(LogService logService, AppSettings appSettings,
            SknLoadingService sknLoadingService, MapViewerSceneService mapViewerSceneService,
            ChromaLoadingService chromaLoadingService, VfxLoadingService vfxLoadingService,
            CustomMessageBoxService customMessageBoxService)
        {
            _logService = logService;
            _appSettings = appSettings;
            _sknLoadingService = sknLoadingService;
            _mapSceneService = mapViewerSceneService;
            _chromaLoadingService = chromaLoadingService;
            _vfxLoadingService = vfxLoadingService;
            _messageBox = customMessageBoxService;
            InitializeComponent();
            DataContext = _model;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_isCleanedUp) return;
            _appSettings.ConfigurationSaved -= OnConfigurationSaved;
            _appSettings.ConfigurationSaved += OnConfigurationSaved;
            RefreshRecentProjects();
        }

        private void OnConfigurationSaved(object sender, EventArgs e) => Dispatcher.InvokeAsync(() =>
        {
            if (!_isCleanedUp) RefreshRecentProjects();
        });

        private void RefreshRecentProjects() => _model.SetRecentProjects(_appSettings.StudioRecentProjects);

        private Task<bool> EnterStudioAsync()
        {
            if (_studioEntryTask is { IsCompleted: false }) return _studioEntryTask;
            return _studioEntryTask = EnterStudioCoreAsync();
        }

        private async Task<bool> EnterStudioCoreAsync()
        {
            if (_isCleanedUp) return false;
            _model.IsEnteringStudio = true;
            try
            {
                // Let the click finish and the opening state render before constructing WPF controls.
                await Dispatcher.Yield(DispatcherPriority.Background);
                if (_isCleanedUp || !IsLoaded) return false;
                if (_studio == null)
                {
                    _studio = new StudioControl
                    {
                        LogService = _logService, AppSettings = _appSettings,
                        SknLoadingService = _sknLoadingService, MapViewerSceneService = _mapSceneService,
                        ChromaLoadingService = _chromaLoadingService, VfxLoadingService = _vfxLoadingService,
                        CustomMessageBoxService = _messageBox
                    };
                    _studio.ExitRequested += OnStudioExitRequested;
                    StudioHost.Content = _studio;
                }
                _model.IsStudioVisible = true;
                _model.IsChromaLibraryVisible = false;
                _studio.Activate();
                return true;
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to open 3D Studio.");
                _model.IsStudioVisible = false;
                return false;
            }
            finally
            {
                _model.IsEnteringStudio = false;
            }
        }

        private void OnStudioExitRequested(object sender, EventArgs e)
        {
            _studio.Deactivate();
            _model.IsStudioVisible = false;
            RefreshRecentProjects();
        }

        private async void OpenStudio_Click(object sender, RoutedEventArgs e) => await EnterStudioAsync();

        private async void OpenProject_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "Open 3D Studio project folder" };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true) await OpenProjectAsync(dialog.FolderName);
        }

        private async void OpenRecent_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: StudioRecentProject project }) await OpenProjectAsync(project.Path);
        }

        private async Task OpenProjectAsync(string path)
        {
            if (_isCleanedUp) return;
            if (!Directory.Exists(path))
            {
                _messageBox.ShowWarning("Project unavailable", "This project folder is no longer available. Open its new location or remove it from recent projects.", Window.GetWindow(this));
                return;
            }
            if (!await EnterStudioAsync()) return;
            _studio.LoadExtractedContainer(path);
        }

        private void RemoveRecent_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: StudioRecentProject project }) return;
            _appSettings.StudioRecentProjects = _appSettings.StudioRecentProjects
                .Where(path => !string.Equals(path, project.Path, StringComparison.OrdinalIgnoreCase)).ToList();
            try { _appSettings.Save(); }
            catch (Exception ex) { _logService.LogError(ex, "Failed to save recent Studio projects."); }
            RefreshRecentProjects();
        }

        private async void OpenChromas_Click(object sender, RoutedEventArgs e)
            => await ChooseHomeChromaFolderAsync();

        private async Task ChooseHomeChromaFolderAsync()
        {
            if (_isCleanedUp) return;
            var dialog = new OpenFolderDialog
            {
                Title = "Select the skins folder for Chroma Library",
                InitialDirectory = _homeChromas?.ViewModel.CurrentSourcePath ?? string.Empty
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            await ShowChromaLibraryAsync(dialog.FolderName);
        }

        internal async Task ShowChromaLibraryAsync(string skinsPath)
        {
            if (_isCleanedUp || !Directory.Exists(skinsPath)) return;
            if (_homeChromas == null)
            {
                _homeChromas = new ChromaSelectionControl
                {
                    ChromaLoadingService = _chromaLoadingService,
                    CustomMessageBoxService = _messageBox
                };
                _homeChromas.CloseRequested += OnHomeChromasCloseRequested;
                _homeChromas.SourceChangeRequested += OnHomeChromasSourceChangeRequested;
                _homeChromas.SelectionRequested += OnHomeChromasSelectionRequested;
                ChromaHost.Content = _homeChromas;
            }
            _model.IsChromaLibraryVisible = true;
            await _homeChromas.InitializeAsync(skinsPath);
        }

        private void OnHomeChromasCloseRequested(object sender, EventArgs e)
        {
            _model.IsChromaLibraryVisible = false;
            _homeChromas.Reset();
        }

        private async void OnHomeChromasSourceChangeRequested(object sender, EventArgs e)
            => await ChooseHomeChromaFolderAsync();

        private async void OnHomeChromasSelectionRequested(IReadOnlyList<ChromaSkinModel> selected, bool addToScene)
        {
            if (selected.Count == 0 || _isCleanedUp) return;
            if (!await EnterStudioAsync()) return;
            _studio.LoadChromas(selected, addToScene);
        }

        private void OnUnloaded(object sender, RoutedEventArgs e) => CleanupResources();

        public void CleanupResources()
        {
            if (_isCleanedUp) return;
            _isCleanedUp = true;
            Loaded -= OnLoaded;
            Unloaded -= OnUnloaded;
            _appSettings.ConfigurationSaved -= OnConfigurationSaved;
            if (_homeChromas != null)
            {
                _homeChromas.CloseRequested -= OnHomeChromasCloseRequested;
                _homeChromas.SourceChangeRequested -= OnHomeChromasSourceChangeRequested;
                _homeChromas.SelectionRequested -= OnHomeChromasSelectionRequested;
                _homeChromas.Reset();
                ChromaHost.Content = null;
                _homeChromas = null;
            }
            if (_studio != null)
            {
                _studio.ExitRequested -= OnStudioExitRequested;
                try { _studio.Cleanup(); }
                catch (Exception ex) { _logService.LogError(ex, "Failed to release 3D Studio."); }
                StudioHost.Content = null;
                _studio = null;
            }
            try { _vfxLoadingService.Dispose(); }
            catch (Exception ex) { _logService.LogError(ex, "Failed to release Studio asset loader."); }
        }
    }
}
