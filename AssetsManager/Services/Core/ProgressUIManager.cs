using System;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows;
using Material.Icons.WPF;
using AssetsManager.Services.Comparator;
using AssetsManager.Services.Downloads;
using AssetsManager.Views.Dialogs;
using AssetsManager.Views.Models.Wad;
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
        private readonly TaskCancellationManager _taskCancellationManager; // New field

        private Button _progressSummaryButton;
        private MaterialIcon _progressIcon;
        private TextBlock _statusTextBlock;
        private TextBlock _progressPercentageTextBlock;
        private Window _owner;

        private Storyboard _spinningIconAnimationStoryboard;
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
            _taskCancellationManager = taskCancellationManager; // Assign new dependency
        }

        public void Initialize(Button progressSummaryButton, MaterialIcon progressIcon, TextBlock statusTextBlock, TextBlock progressPercentageTextBlock, Window owner)
        {
            _progressSummaryButton = progressSummaryButton;
            _progressIcon = progressIcon;
            _statusTextBlock = statusTextBlock;
            _progressPercentageTextBlock = progressPercentageTextBlock;
            _owner = owner;
            _progressSummaryButton.Click += ProgressSummaryButton_Click;
        }

        public void Cleanup()
        {
            if (_progressSummaryButton != null)
            {
                _progressSummaryButton.Click -= ProgressSummaryButton_Click;
            }
            _spinningIconAnimationStoryboard?.Stop();
            _spinningIconAnimationStoryboard = null;
            _progressDetailsWindow?.Close();
            _progressDetailsWindow = null;
            _progressSummaryButton = null;
            _progressIcon = null;
            _statusTextBlock = null;
            _progressPercentageTextBlock = null;
            _owner = null;
        }

        private void UpdateStatusBar(string message, int completed = -1, int total = -1)
        {
            _owner.Dispatcher.Invoke(() =>
            {
                if (_statusTextBlock != null) _statusTextBlock.Text = message;
                
                if (_progressPercentageTextBlock != null)
                {
                    if (completed >= 0 && total > 0)
                    {
                        double percentage = (double)completed / total * 100;
                        _progressPercentageTextBlock.Text = $"{(int)percentage}%";
                        _progressPercentageTextBlock.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        _progressPercentageTextBlock.Visibility = Visibility.Collapsed;
                    }
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

        public void OnDownloadCompleted()
        {
            UpdateStatusBar("Ready");
            _owner.Dispatcher.Invoke(() =>
            {
                _progressSummaryButton.Visibility = Visibility.Collapsed;
                _spinningIconAnimationStoryboard?.Stop();
                _spinningIconAnimationStoryboard = null;
                _progressDetailsWindow?.Close();
            });
        }

        public void OnComparisonStarted(int totalFiles)
        {
            _owner.Dispatcher.Invoke(() =>
            {
                _totalFiles = totalFiles;
                _progressSummaryButton.Visibility = Visibility.Visible;
                _progressSummaryButton.ToolTip = "Click to see comparison details";

                UpdateStatusBar("Comparing WADs...", 0, totalFiles);

                if (_spinningIconAnimationStoryboard == null)
                {
                    var originalStoryboard = (Storyboard)_owner.FindResource("SpinningIconAnimation");
                    _spinningIconAnimationStoryboard = originalStoryboard?.Clone();
                    if (_spinningIconAnimationStoryboard != null) Storyboard.SetTarget(_spinningIconAnimationStoryboard, _progressIcon);
                }
                _spinningIconAnimationStoryboard?.Begin();

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

        public void OnComparisonCompleted(List<ChunkDiff> allDiffs, string oldPbePath, string newPbePath)
        {
            UpdateStatusBar("");
            _owner.Dispatcher.Invoke(() =>
            {
                _progressSummaryButton.Visibility = Visibility.Collapsed;
                _spinningIconAnimationStoryboard?.Stop();
                _spinningIconAnimationStoryboard = null;
                _progressDetailsWindow?.Close();
            });
        }

        public void OnExtractionStarted(object sender, (string message, int totalFiles) data)
        {
            _owner.Dispatcher.Invoke(() =>
            {
                _progressSummaryButton.Visibility = Visibility.Visible;
                _progressSummaryButton.ToolTip = "Click to see extraction details";

                UpdateStatusBar("Extracting assets...", 0, data.totalFiles);

                if (_spinningIconAnimationStoryboard == null)
                {
                    var originalStoryboard = (Storyboard)_owner.FindResource("SpinningIconAnimation");
                    _spinningIconAnimationStoryboard = originalStoryboard?.Clone();
                    if (_spinningIconAnimationStoryboard != null) Storyboard.SetTarget(_spinningIconAnimationStoryboard, _progressIcon);
                }
                _spinningIconAnimationStoryboard?.Begin();

                _progressDetailsWindow = new ProgressDetailsWindow(_logService, "Extractor", _taskCancellationManager);
                _progressDetailsWindow.Owner = _owner;
                _progressDetailsWindow.OperationVerb = "Extracting";
                _progressDetailsWindow.HeaderIconKind = "PackageDown";
                _progressDetailsWindow.HeaderText = "Extracting New Assets";
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

        public void OnExtractionCompleted()
        {
            UpdateStatusBar("");
            _owner.Dispatcher.Invoke(() =>
            {
                _progressSummaryButton.Visibility = Visibility.Collapsed;
                _spinningIconAnimationStoryboard?.Stop();
                _spinningIconAnimationStoryboard = null;
                _progressDetailsWindow?.Close();
            });
        }

        public void OnVersionDownloadStarted(object sender, string taskName)
        {
            _owner.Dispatcher.Invoke(() =>
            {
                _progressSummaryButton.Visibility = Visibility.Visible;
                _progressSummaryButton.ToolTip = "Click to see download details";

                UpdateStatusBar($"Updating {taskName}...");

                if (_spinningIconAnimationStoryboard == null)
                {
                    var originalStoryboard = (Storyboard)_owner.FindResource("SpinningIconAnimation");
                    _spinningIconAnimationStoryboard = originalStoryboard?.Clone();
                    if (_spinningIconAnimationStoryboard != null) Storyboard.SetTarget(_spinningIconAnimationStoryboard, _progressIcon);
                }
                _spinningIconAnimationStoryboard?.Begin();

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
                _progressDetailsWindow?.UpdateProgress(0, 1, data.Details, true, null); // Indeterminate progress, update current file
            });
        }

        public void OnVersionDownloadCompleted(object sender, (string TaskName, bool Success, string Message) data)
        {
            UpdateStatusBar("");
            _owner.Dispatcher.Invoke(() =>
            {
                _progressSummaryButton.Visibility = Visibility.Collapsed;
                _spinningIconAnimationStoryboard?.Stop();
                _spinningIconAnimationStoryboard = null;
                _progressDetailsWindow?.Close();

                if (data.Success)
                {
                    _customMessageBoxService.ShowSuccess("Success", data.Message, _owner);
                }
                else
                {
                    _customMessageBoxService.ShowError("Error", data.Message, _owner);
                }
            });
        }

        private void ProgressSummaryButton_Click(object sender, RoutedEventArgs e)
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
