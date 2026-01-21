using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using AssetsManager.Services.Comparator;
using AssetsManager.Services.Downloads;
using AssetsManager.Views.Dialogs;
using AssetsManager.Views.Models.Wad;
using AssetsManager.Views.Models.Controls;
using AssetsManager.Utils;

namespace AssetsManager.Services.Core
{
    public class ProgressUIManager
    {
        private readonly LogService _logService;
        private readonly IServiceProvider _serviceProvider;
        private readonly CustomMessageBoxService _customMessageBoxService;
        private readonly DirectoriesCreator _directoriesCreator;
        private readonly AssetDownloader _assetDownloader;
        private readonly WadDifferenceService _wadDifferenceService;
        private readonly WadPackagingService _wadPackagingService;
        private readonly DiffViewService _diffViewService;
        private readonly TaskCancellationManager _taskCancellationManager;

        private StatusBarViewModel _statusBarViewModel;
        private Window _owner;

        private ProgressDetailsWindow _progressDetailsWindow;
        private int _totalFiles;

        public ProgressUIManager(
            LogService logService,
            IServiceProvider serviceProvider,
            CustomMessageBoxService customMessageBoxService,
            DirectoriesCreator directoriesCreator,
            AssetDownloader assetDownloader,
            WadDifferenceService wadDifferenceService,
            WadPackagingService wadPackagingService,
            DiffViewService diffViewService,
            TaskCancellationManager taskCancellationManager)
        {
            _logService = logService;
            _serviceProvider = serviceProvider;
            _customMessageBoxService = customMessageBoxService;
            _directoriesCreator = directoriesCreator;
            _assetDownloader = assetDownloader;
            _wadDifferenceService = wadDifferenceService;
            _wadPackagingService = wadPackagingService;
            _diffViewService = diffViewService;
            _taskCancellationManager = taskCancellationManager;
            _taskCancellationManager.OperationStateChanged += TaskCancellationManager_OperationStateChanged;
        }

        public void Initialize(StatusBarViewModel statusBarViewModel, Window owner)
        {
            _statusBarViewModel = statusBarViewModel;
            _owner = owner;
        }

        public void Cleanup()
        {
            if (_taskCancellationManager != null)
            {
                _taskCancellationManager.OperationStateChanged -= TaskCancellationManager_OperationStateChanged;
            }
            
            _progressDetailsWindow?.Close();
            _progressDetailsWindow = null;
            _statusBarViewModel = null;
            _owner = null;
        }

        private void TaskCancellationManager_OperationStateChanged(object sender, EventArgs e)
        {
            if (_taskCancellationManager.IsCancelling)
            {
                UpdateStatusBar(_taskCancellationManager.CancellationMessage);
            }
            else
            {
                _owner.Dispatcher.Invoke(() =>
                {
                    if (_statusBarViewModel != null && _statusBarViewModel.StatusText == _taskCancellationManager.CancellationMessage)
                    {
                        UpdateStatusBar("Ready");
                    }
                });
            }
        }

        private void UpdateStatusBar(string message, int completed = -1, int total = -1)
        {
            _owner.Dispatcher.Invoke(() =>
            {
                if (_statusBarViewModel == null) return;

                _statusBarViewModel.StatusText = message;
                
                if (completed >= 0 && total > 0)
                {
                    double percentage = (double)completed / total * 100;
                    _statusBarViewModel.ProgressPercentage = $"{(int)percentage}%";
                }
                else if (!string.IsNullOrEmpty(message) && message != "Ready")
                {
                    // Task is active but no numbers yet (e.g. Initializing)
                    // Set to empty string so NullToVisibilityConverter shows the button (spinner) 
                    // but the TextBlock remains empty.
                    _statusBarViewModel.ProgressPercentage = string.Empty;
                }
                else
                {
                    // No task or "Ready" state -> Hide the progress button
                    _statusBarViewModel.ProgressPercentage = null;
                }
            });
        }

        public void OnDownloadProgressChanged(int completedFiles, int totalFiles, string currentFileName, bool isSuccess, string errorMessage)
        {
            UpdateStatusBar($"Downloading: {currentFileName}", completedFiles, totalFiles);
            _owner.Dispatcher.Invoke(() =>
            {
                _progressDetailsWindow?.UpdateProgress(completedFiles, totalFiles, currentFileName, isSuccess, errorMessage);
            });
        }

        public async void OnDownloadCompleted()
        {
            if (_taskCancellationManager.IsCancelling) await Task.Delay(1500);
            UpdateStatusBar("Ready");
            _owner.Dispatcher.Invoke(() =>
            {
                _progressDetailsWindow?.Close();
            });
        }

        public void OnComparisonStarted(int totalFiles)
        {
            _owner.Dispatcher.Invoke(() =>
            {
                _totalFiles = totalFiles;
                
                // Show progress immediately
                UpdateStatusBar("Comparing WADs...", 0, totalFiles);

                _progressDetailsWindow = new ProgressDetailsWindow(_logService, "Comparator", _taskCancellationManager);
                _progressDetailsWindow.Owner = _owner;
                _progressDetailsWindow.OperationVerb = "Comparing";
                _progressDetailsWindow.HeaderIconKind = "Compare";
                _progressDetailsWindow.HeaderText = "Comparing WADs";
                _progressDetailsWindow.Closed += (s, e) => _progressDetailsWindow = null;
                _progressDetailsWindow.UpdateProgress(0, totalFiles, "Initializing...", true, null);
            });
        }

        public void OnComparisonProgressChanged(int completedFiles, string currentFile, bool isSuccess, string errorMessage)
        {
            UpdateStatusBar($"Comparing: {currentFile}", completedFiles, _totalFiles);
            _owner.Dispatcher.Invoke(() =>
            {
                _progressDetailsWindow?.UpdateProgress(completedFiles, _totalFiles, currentFile, isSuccess, errorMessage);
            });
        }

        public async void OnComparisonCompleted(List<ChunkDiff> allDiffs, string oldPbePath, string newPbePath)
        {
            if (_taskCancellationManager.IsCancelling) await Task.Delay(1500);
            UpdateStatusBar("Ready");
            _owner.Dispatcher.Invoke(() =>
            {
                _progressDetailsWindow?.Close();
            });
        }

        public void OnExtractionStarted(object sender, (string message, int totalFiles) data)
        {
            _owner.Dispatcher.Invoke(() =>
            {
                UpdateStatusBar("Extracting assets...", 0, data.totalFiles);

                _progressDetailsWindow = new ProgressDetailsWindow(_logService, "Extractor", _taskCancellationManager);
                _progressDetailsWindow.Owner = _owner;
                _progressDetailsWindow.OperationVerb = "Extracting";
                _progressDetailsWindow.HeaderIconKind = "PackageDown";
                _progressDetailsWindow.HeaderText = "Extracting Assets";
                _progressDetailsWindow.Closed += (s, e) => _progressDetailsWindow = null;
                _progressDetailsWindow.UpdateProgress(0, data.totalFiles, data.message, true, null);
            });
        }

        public void OnExtractionProgressChanged(int completedFiles, int totalFiles, string currentFile)
        {
            UpdateStatusBar($"Extracting: {currentFile}", completedFiles, totalFiles);
            _owner.Dispatcher.Invoke(() =>
            {
                _progressDetailsWindow?.UpdateProgress(completedFiles, totalFiles, currentFile, true, null);
            });
        }

        public async void OnExtractionCompleted()
        {
            if (_taskCancellationManager.IsCancelling) await Task.Delay(1500);
            UpdateStatusBar("Ready");
            _owner.Dispatcher.Invoke(() =>
            {
                _progressDetailsWindow?.Close();
            });
        }

        public void OnVersionDownloadStarted(object sender, string taskName)
        {
            _owner.Dispatcher.Invoke(() =>
            {
                // Indeterminate progress initially
                UpdateStatusBar($"Updating {taskName}...");

                _progressDetailsWindow = new ProgressDetailsWindow(_logService, "Versions Update", _taskCancellationManager);
                _progressDetailsWindow.Owner = _owner;
                _progressDetailsWindow.OperationVerb = "Updating";
                _progressDetailsWindow.HeaderIconKind = "Download";
                _progressDetailsWindow.HeaderText = "Versions Update";
                _progressDetailsWindow.Closed += (s, e) => _progressDetailsWindow = null;
                _progressDetailsWindow.UpdateProgress(0, 0, "Initializing...", true, null);
            });
        }

        public void OnVersionDownloadProgressChanged(object sender, (string TaskName, int Progress, string Details) data)
        {
            UpdateStatusBar($"Downloading {data.TaskName}: {data.Details}");
            _owner.Dispatcher.Invoke(() =>
            {
                _progressDetailsWindow?.UpdateProgress(0, 1, data.Details, true, null); 
            });
        }

        public async void OnVersionDownloadCompleted(object sender, (string TaskName, bool Success, string Message) data)
        {
            if (_taskCancellationManager.IsCancelling) await Task.Delay(1500);
            UpdateStatusBar("Ready");
            _owner.Dispatcher.Invoke(() =>
            {
                _progressDetailsWindow?.Close();

                if (data.Success)
                {
                    _customMessageBoxService.ShowSuccess("Success", data.Message, _owner);
                }
                else if (!_taskCancellationManager.IsCancelling) 
                {
                    _customMessageBoxService.ShowError("Error", data.Message, _owner);
                }
            });
        }

        public void ClearStatusText()
        {
            UpdateStatusBar("");
        }
        
        // This functionality is now handled by the StatusBarView command/event, 
        // but we keep the method if needed to programmatically show it, though Logic is now in View/VM.
        public void ShowDetails()
        {
             if (_progressDetailsWindow != null)
            {
                if (!_progressDetailsWindow.IsVisible) _progressDetailsWindow.Show();
                _progressDetailsWindow.Activate();
            }
            else
            {
                _logService.Log("No active process to show details for.");
            }
        }
    }
}
