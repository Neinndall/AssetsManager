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
using Material.Icons;

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

                // Convert string iconKind to MaterialIconKind enum
                MaterialIconKind icon = MaterialIconKind.ProgressClock; // Default
                if (!string.IsNullOrEmpty(iconKind))
                {
                    if (Enum.TryParse(iconKind, out MaterialIconKind parsedIcon))
                    {
                        icon = parsedIcon;
                    }
                }

                _progressDetailsWindow = new ProgressDetailsWindow(_logService, headerText, _taskCancellationManager)
                {
                    Owner = _owner,
                    HeaderIcon = icon,
                    HeaderTitle = headerText
                };
                
                _progressDetailsWindow.ViewModel.OperationVerb = verb;
                
                _progressDetailsWindow.Closed += (s, e) => _progressDetailsWindow = null;
                _progressDetailsWindow.UpdateProgress(0, totalItems, "Initializing...", true, null);
                _progressDetailsWindow.Show();
            });
        }

        /// <summary>
        /// Centralizes logic to update both StatusBar and ProgressWindow.
        /// </summary>
        private void UpdateOperation(string statusMessage, int current, int total, string currentFileDetail, bool success = true, string errorMessage = null)
        {
            // Clean statusMessage for StatusBar (remove anything after '|')
            string cleanStatus = statusMessage;
            if (cleanStatus != null && cleanStatus.Contains("|"))
            {
                cleanStatus = cleanStatus.Split('|')[0].Trim();
            }

            UpdateStatusBar(cleanStatus, current, total);
            _owner.Dispatcher.Invoke(() =>
            {
                _progressDetailsWindow?.UpdateProgress(current, total, currentFileDetail, success, errorMessage);
            });
        }

        /// <summary>
        private async Task FinishOperation()
        {
            bool wasCancelled = _taskCancellationManager.IsCancelling;
            if (wasCancelled)
            {
                await Task.Delay(1500);
            }
            else
            {
                // Yield control to the UI thread to allow it to render the final progress state (e.g. 207 of 207)
                if (System.Windows.Application.Current != null)
                {
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                }

                // Give the user 50ms to visually register the completed progress before closing the window
                await Task.Delay(50);
            }

            _taskCancellationManager.CompleteCurrentOperation();
            UpdateStatusBar("Ready");
            _owner.Dispatcher.Invoke(() =>
            {
                CloseProgressWindow();
            });
        }

        private void CloseProgressWindow()
        {
            if (_progressDetailsWindow != null)
            {
                _progressDetailsWindow.ViewModel.IsFinished = true;
                _progressDetailsWindow.Close();
                _progressDetailsWindow = null;
            }
        }

        private void UpdateStatusBar(string message, int completed = -1, int total = -1)
        {
            _owner.Dispatcher.Invoke(() =>
            {
                if (_statusBarViewModel == null) return;

                // PROTECT CANCELLATION STATE: If we are cancelling, don't let other progress messages 
                // overwrite the "Cancelling Task..." message, except for the "Ready" reset.
                if (_taskCancellationManager.IsCancelling && 
                    message != _taskCancellationManager.CancellationMessage && 
                    message != "Ready")
                {
                    return;
                }

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
                _owner.Dispatcher.InvokeAsync(() =>
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

        // --- Comparator ---

        public void OnComparisonStarted(int totalFiles)
        {
            _owner.Dispatcher.Invoke(() =>
            {
                // If the operation is already active (indeterminate start), just update the total
                if (_progressDetailsWindow != null && _totalFiles == 0 && totalFiles > 0)
                {
                    _totalFiles = totalFiles;
                    UpdateStatusBar("Preparing WADs...", 0, totalFiles);
                    _progressDetailsWindow.UpdateProgress(0, totalFiles, "Initializing...", true, null);
                    return;
                }
                
                StartOperation("Comparing WADs", "Comparing", "Compare", totalFiles, "Preparing WADs...");
            });
        }

        public void OnComparisonProgressChanged(int completedFiles, string currentFile, bool isSuccess, string errorMessage)
        {
            // Note: currentFile already comes formatted as "{fileIndex} of {total} files: {wadName}" from WadComparatorService
            UpdateOperation($"Comparing {currentFile}", completedFiles, _totalFiles, currentFile, isSuccess, errorMessage);
        }

        public async void OnComparisonCompleted(List<ChunkDiff> allDiffs, string oldPbePath, string newPbePath, string version) => await FinishComparisonAsync();

        /// <summary>
        /// Closes the comparison progress window after rendering the final 100% state.
        /// Callers should await this before opening follow-up UI (e.g. results window)
        /// to guarantee the progress window is fully closed first.
        /// </summary>
        public async Task FinishComparisonAsync() => await FinishOperation();

        // --- Extraction ---

        public void OnExtractionStarted(object sender, (string message, int totalFiles) data)
        {
            _owner.Dispatcher.Invoke(() =>
            {
                if (_progressDetailsWindow != null && _totalFiles == 0 && data.totalFiles > 0)
                {
                    _totalFiles = data.totalFiles;
                    UpdateStatusBar("Preparing Files...", 0, data.totalFiles);
                    _progressDetailsWindow.UpdateProgress(0, data.totalFiles, "Initializing...", true, null);
                    return;
                }

                StartOperation("Extracting Assets", "Extracting", "PackageDown", data.totalFiles, "Preparing Files...");
            });
        }

        public void OnExtractionProgressChanged(int completedFiles, int totalFiles, string currentFile)
        {
            string detail = string.IsNullOrEmpty(currentFile) ? "Preparing Assets..." : currentFile;
            UpdateOperation($"Extracting {completedFiles} of {totalFiles} assets: {detail}", completedFiles, totalFiles, currentFile);
        }

        public async void OnExtractionCompleted() => await FinishOperation();

        // --- Saving ---

        public void OnSavingStarted(int totalFiles)
        {
            _owner.Dispatcher.Invoke(() =>
            {
                if (_progressDetailsWindow != null && _totalFiles == 0 && totalFiles > 0)
                {
                    _totalFiles = totalFiles;
                    UpdateStatusBar("Preparing Files...", 0, totalFiles);
                    _progressDetailsWindow.UpdateProgress(0, totalFiles, "Initializing...", true, null);
                    return;
                }

                StartOperation("Saving Assets", "Saving", "ContentSave", totalFiles, "Preparing Files...");
            });
        }

        public void OnSavingProgressChanged(int completedFiles, int totalFiles, string currentFile)
        {
            string detail = string.IsNullOrEmpty(currentFile) ? "Preparing Assets..." : currentFile;
            UpdateOperation($"Saving {completedFiles} of {totalFiles} assets: {detail}", completedFiles, totalFiles, currentFile);
        }

        public async void OnSavingCompleted() => await FinishOperation();

        // --- Versions (Update) ---

        public void OnVersionDownloadStarted(object sender, string taskName)
        {
            // Version download always starts with verification
            StartOperation("Versions Update", "Verifying", "Download", 0, "Preparing Manifests...");
        }

        public void OnVersionDownloadProgressChanged(object sender, (string TaskName, int CurrentValue, int TotalValue, string CurrentFile) data)
        {
            _owner.Dispatcher.Invoke(() =>
            {
                if (_progressDetailsWindow != null)
                {
                    _progressDetailsWindow.ViewModel.OperationVerb = data.TaskName; // Cambia dinámicamente entre Verifying y Updating
                }
            });
            // data.CurrentFile already contains "X of Y files: name", so we just prepend the TaskName
            UpdateOperation($"{data.TaskName} {data.CurrentFile}", data.CurrentValue, data.TotalValue, data.CurrentFile);
        }

        public async void OnVersionDownloadCompleted(object sender, (string TaskName, bool Success, string Message) data)
        {
            bool wasCancelled = _taskCancellationManager.IsCancelling;

            await FinishOperation();
            
            await _owner.Dispatcher.InvokeAsync(() =>
            {
                if (!data.Success && !wasCancelled) 
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
                    UpdateStatusBar("Preparing Backup...", 0, totalFiles);
                    _progressDetailsWindow.UpdateProgress(0, totalFiles, "Initializing...", true, null);
                    return;
                }

                StartOperation("Creating Backup", "Backing up", "ContentSave", totalFiles, "Preparing Backup...");
            });
        }

        public void OnBackupProgressChanged(object sender, (int Processed, int Total, string CurrentFile) data)
        {
            string detail = string.IsNullOrEmpty(data.CurrentFile) ? "Preparing Backup..." : data.CurrentFile;
            UpdateOperation($"Backing up {data.Processed} of {data.Total} files: {detail}", data.Processed, data.Total, data.CurrentFile);
        }

        public async void OnBackupCompleted(object sender, bool success)
        {
            // BackupsControl handles the success message/logic. We just close the progress UI.
            await FinishOperation();
        }

        #endregion
    }
}