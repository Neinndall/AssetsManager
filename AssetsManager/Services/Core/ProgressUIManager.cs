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
        #region Fields & Constructor

        private readonly LogService _logService;
        private readonly IServiceProvider _serviceProvider;
        private readonly CustomMessageBoxService _customMessageBoxService;
        private readonly TaskCancellationManager _taskCancellationManager;

        private StatusBarViewModel _statusBarViewModel;
        private Window _owner;
        private ProgressDetailsWindow _progressDetailsWindow;
        private int _totalFiles;

        public ProgressUIManager(
            LogService logService,
            IServiceProvider serviceProvider,
            CustomMessageBoxService customMessageBoxService,
            TaskCancellationManager taskCancellationManager)
        {
            _logService = logService;
            _serviceProvider = serviceProvider;
            _customMessageBoxService = customMessageBoxService;
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
            
            CloseProgressWindow();
            _statusBarViewModel = null;
            _owner = null;
        }

        #endregion

        #region Internal Helpers (Dry Logic)

        /// <summary>
        /// Centralizes the logic to start an operation: Updates StatusBar and opens ProgressWindow.
        /// </summary>
        private void StartOperation(string headerText, string verb, string iconKind, int totalItems, string initialStatusMsg)
        {
            _owner.Dispatcher.Invoke(() =>
            {
                _totalFiles = totalItems;
                UpdateStatusBar(initialStatusMsg, 0, totalItems);

                _progressDetailsWindow = new ProgressDetailsWindow(_logService, headerText, _taskCancellationManager)
                {
                    Owner = _owner,
                    OperationVerb = verb,
                    HeaderIconKind = iconKind,
                    HeaderText = headerText
                };
                
                _progressDetailsWindow.Closed += (s, e) => _progressDetailsWindow = null;
                _progressDetailsWindow.UpdateProgress(0, totalItems, "Initializing...", true, null);
            });
        }

        /// <summary>
        /// Centralizes logic to update both StatusBar and ProgressWindow.
        /// </summary>
        private void UpdateOperation(string statusMessage, int current, int total, string currentFileDetail, bool success = true, string errorMessage = null)
        {
            UpdateStatusBar(statusMessage, current, total);
            _owner.Dispatcher.Invoke(() =>
            {
                _progressDetailsWindow?.UpdateProgress(current, total, currentFileDetail, success, errorMessage);
            });
        }

        /// <summary>
        /// Centralizes logic to finish an operation.
        /// </summary>
        private async void FinishOperation()
        {
            if (_taskCancellationManager.IsCancelling) await Task.Delay(1500);
            UpdateStatusBar("Ready");
            _owner.Dispatcher.Invoke(() =>
            {
                CloseProgressWindow();
            });
        }

        private void CloseProgressWindow()
        {
            _progressDetailsWindow?.Close();
            _progressDetailsWindow = null;
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
                    _statusBarViewModel.ProgressPercentage = string.Empty; // Show spinner, no number
                }
                else
                {
                    _statusBarViewModel.ProgressPercentage = null; // Hide
                }
            });
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

        #endregion

        #region Public Interface (Event Handlers)

        public void ClearStatusText() => UpdateStatusBar("");

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

        // --- Downloads ---

        public void OnDownloadProgressChanged(int completedFiles, int totalFiles, string currentFileName, bool isSuccess, string errorMessage)
        {
            UpdateOperation($"Downloading {completedFiles} of {totalFiles} files: {currentFileName}", completedFiles, totalFiles, currentFileName, isSuccess, errorMessage);
        }

        public void OnDownloadCompleted() => FinishOperation();

        // --- Comparator ---

        public void OnComparisonStarted(int totalFiles)
        {
            _owner.Dispatcher.Invoke(() =>
            {
                // If the operation is already active (indeterminate start), just update the total
                if (_progressDetailsWindow != null && _totalFiles == 0 && totalFiles > 0)
                {
                    _totalFiles = totalFiles;
                    UpdateStatusBar("Comparing WADs...", 0, totalFiles);
                    _progressDetailsWindow.UpdateProgress(0, totalFiles, "Initializing...", true, null);
                    return;
                }
                
                StartOperation("Comparing WADs", "Comparing", "Compare", totalFiles, "Comparing WADs...");
            });
        }

        public void OnComparisonProgressChanged(int completedFiles, string currentFile, bool isSuccess, string errorMessage)
        {
            // Note: currentFile already comes formatted as "{fileIndex} of {total} files: {wadName}" from WadComparatorService
            UpdateOperation($"Comparing {currentFile}", completedFiles, _totalFiles, currentFile, isSuccess, errorMessage);
        }

        public void OnComparisonCompleted(List<ChunkDiff> allDiffs, string oldPbePath, string newPbePath) => FinishOperation();

        // --- Extraction ---

        public void OnExtractionStarted(object sender, (string message, int totalFiles) data)
        {
            _owner.Dispatcher.Invoke(() =>
            {
                if (_progressDetailsWindow != null && _totalFiles == 0 && data.totalFiles > 0)
                {
                    _totalFiles = data.totalFiles;
                    UpdateStatusBar("Extracting Assets...", 0, data.totalFiles);
                    _progressDetailsWindow.UpdateProgress(0, data.totalFiles, "Initializing...", true, null);
                    return;
                }

                StartOperation("Extracting Assets", "Extracting", "PackageDown", data.totalFiles, "Extracting Assets...");
            });
        }

        public void OnExtractionProgressChanged(int completedFiles, int totalFiles, string currentFile)
        {
            UpdateOperation($"Extracting {completedFiles} of {totalFiles} assets: {currentFile}", completedFiles, totalFiles, currentFile);
        }

        public void OnExtractionCompleted() => FinishOperation();

        // --- Saving ---

        public void OnSavingStarted(int totalFiles)
        {
            _owner.Dispatcher.Invoke(() =>
            {
                if (_progressDetailsWindow != null && _totalFiles == 0 && totalFiles > 0)
                {
                    _totalFiles = totalFiles;
                    UpdateStatusBar("Saving Assets...", 0, totalFiles);
                    _progressDetailsWindow.UpdateProgress(0, totalFiles, "Initializing...", true, null);
                    return;
                }

                StartOperation("Saving Assets", "Saving", "ContentSave", totalFiles, "Saving Assets...");
            });
        }

        public void OnSavingProgressChanged(int completedFiles, int totalFiles, string currentFile)
        {
            UpdateOperation($"Saving {completedFiles} of {totalFiles} assets: {currentFile}", completedFiles, totalFiles, currentFile);
        }

        public void OnSavingCompleted() => FinishOperation();

        // --- Versions (Update) ---

        public void OnVersionDownloadStarted(object sender, string taskName)
        {
            // Version download is a bit specific, starts indeterminate
            StartOperation("Versions Update", "Updating", "Download", 0, "Verifying Files...");
        }

        public void OnVersionDownloadProgressChanged(object sender, (string TaskName, int CurrentValue, int TotalValue, string CurrentFile) data)
        {
            _owner.Dispatcher.Invoke(() =>
            {
                if (_progressDetailsWindow != null)
                {
                    _progressDetailsWindow.OperationVerb = data.TaskName; // Cambia dinámicamente entre Verifying y Updating
                }
            });
            // data.CurrentFile already contains "X of Y files: name", so we just prepend the TaskName
            UpdateOperation($"{data.TaskName} {data.CurrentFile}", data.CurrentValue, data.TotalValue, data.CurrentFile);
        }

        public async void OnVersionDownloadCompleted(object sender, (string TaskName, bool Success, string Message) data)
        {
            if (_taskCancellationManager.IsCancelling) await Task.Delay(1500);
            UpdateStatusBar("Ready");
            
            _owner.Dispatcher.Invoke(() =>
            {
                CloseProgressWindow();

                if (!data.Success && !_taskCancellationManager.IsCancelling) 
                {
                    _customMessageBoxService.ShowError("Error", data.Message, _owner);
                }
            });
        }

        // --- Backups ---

        public void OnBackupStarted(object sender, int totalFiles)
        {
            _owner.Dispatcher.Invoke(() =>
            {
                // If already active (indeterminate start), update the total
                if (_progressDetailsWindow != null && _totalFiles == 0 && totalFiles > 0)
                {
                    _totalFiles = totalFiles;
                    UpdateStatusBar("Creating backup...", 0, totalFiles);
                    _progressDetailsWindow.UpdateProgress(0, totalFiles, "Initializing...", true, null);
                    return;
                }

                StartOperation("Creating Backup", "Backing up", "ContentSave", totalFiles, "Creating backup...");
            });
        }

        public void OnBackupProgressChanged(object sender, (int Processed, int Total, string CurrentFile) data)
        {
            UpdateOperation($"Backing up {data.Processed} of {data.Total} files: {data.CurrentFile}", data.Processed, data.Total, data.CurrentFile);
        }

        public void OnBackupCompleted(object sender, bool success)
        {
            // BackupsControl handles the success message/logic. We just close the progress UI.
            FinishOperation();
        }

        #endregion
    }
}