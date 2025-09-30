using AssetsManager.Services.Comparator;
using AssetsManager.Services.Downloads;
using AssetsManager.Views.Dialogs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows;
using AssetsManager.Views.Models;
using AssetsManager.Utils;
using Material.Icons.WPF;
using Microsoft.Extensions.DependencyInjection;
using AssetsManager.Services.Monitor;

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

        private Button _progressSummaryButton;
        private MaterialIcon _progressIcon;
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
            DiffViewService diffViewService
            )
        {
            _logService = logService;
            _serviceProvider = serviceProvider;
            _customMessageBoxService = customMessageBoxService;
            _directoriesCreator = directoriesCreator;
            _assetDownloader = assetDownloader;
            _wadDifferenceService = wadDifferenceService;
            _wadPackagingService = wadPackagingService;
            _diffViewService = diffViewService;
        }

        public void Initialize(Button progressSummaryButton, MaterialIcon progressIcon, Window owner)
        {
            _progressSummaryButton = progressSummaryButton;
            _progressIcon = progressIcon;
            _owner = owner;
            _progressSummaryButton.Click += ProgressSummaryButton_Click;
        }

        public void OnDownloadStarted(int totalFiles)
        {
            _owner.Dispatcher.Invoke(() =>
            {
                _totalFiles = totalFiles;
                _progressSummaryButton.Visibility = Visibility.Visible;
                _progressSummaryButton.ToolTip = "Click to see download details";

                if (_spinningIconAnimationStoryboard == null)
                {
                    var originalStoryboard = (Storyboard)_owner.FindResource("SpinningIconAnimation");
                    _spinningIconAnimationStoryboard = originalStoryboard?.Clone();
                    if (_spinningIconAnimationStoryboard != null) Storyboard.SetTarget(_spinningIconAnimationStoryboard, _progressIcon);
                }
                _spinningIconAnimationStoryboard?.Begin();

                _progressDetailsWindow = new ProgressDetailsWindow(_logService, "Download Details");
                _progressDetailsWindow.Owner = _owner;
                _progressDetailsWindow.HeaderIconKind = "Download";
                _progressDetailsWindow.HeaderText = "Download Details";
                _progressDetailsWindow.Closed += (s, e) => _progressDetailsWindow = null;
                _progressDetailsWindow.UpdateProgress(0, totalFiles, "Initializing...", true, null);
            });
        }

        public void OnDownloadProgressChanged(int completedFiles, int totalFiles, string currentFileName, bool isSuccess, string errorMessage)
        {
            _owner.Dispatcher.Invoke(() =>
            {
                _progressDetailsWindow?.UpdateProgress(completedFiles, totalFiles, currentFileName, isSuccess, errorMessage);
            });
        }

        public void OnDownloadCompleted()
        {
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

                if (_spinningIconAnimationStoryboard == null)
                {
                    var originalStoryboard = (Storyboard)_owner.FindResource("SpinningIconAnimation");
                    _spinningIconAnimationStoryboard = originalStoryboard?.Clone();
                    if (_spinningIconAnimationStoryboard != null) Storyboard.SetTarget(_spinningIconAnimationStoryboard, _progressIcon);
                }
                _spinningIconAnimationStoryboard?.Begin();

                _progressDetailsWindow = new ProgressDetailsWindow(_logService, "Comparison Details");
                _progressDetailsWindow.Owner = _owner;
                _progressDetailsWindow.OperationVerb = "Comparing";
                _progressDetailsWindow.HeaderIconKind = "Compare";
                _progressDetailsWindow.HeaderText = "Comparison Details";
                _progressDetailsWindow.Closed += (s, e) => _progressDetailsWindow = null;
                _progressDetailsWindow.UpdateProgress(0, totalFiles, "Comparison starting...", true, null);
            });
        }

        public void OnComparisonProgressChanged(int completedFiles, string currentFile, bool isSuccess, string errorMessage)
        {
            _owner.Dispatcher.Invoke(() =>
            {
                _progressDetailsWindow?.UpdateProgress(completedFiles, _totalFiles, currentFile, isSuccess, errorMessage);
            });
        }

        public void OnComparisonCompleted(List<ChunkDiff> allDiffs, string oldPbePath, string newPbePath)
        {
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

                if (_spinningIconAnimationStoryboard == null)
                {
                    var originalStoryboard = (Storyboard)_owner.FindResource("SpinningIconAnimation");
                    _spinningIconAnimationStoryboard = originalStoryboard?.Clone();
                    if (_spinningIconAnimationStoryboard != null) Storyboard.SetTarget(_spinningIconAnimationStoryboard, _progressIcon);
                }
                _spinningIconAnimationStoryboard?.Begin();

                _progressDetailsWindow = new ProgressDetailsWindow(_logService, taskName);
                _progressDetailsWindow.Owner = _owner;
                _progressDetailsWindow.OperationVerb = "Downloading";
                _progressDetailsWindow.HeaderIconKind = "Download";
                _progressDetailsWindow.HeaderText = taskName;
                _progressDetailsWindow.Closed += (s, e) => _progressDetailsWindow = null;
                _progressDetailsWindow.UpdateProgress(0, 1, "Initializing...", true, null); // Use 0 of 1 for indeterminate progress
            });
        }

        public void OnVersionDownloadProgressChanged(object sender, (string TaskName, int Progress, string Details) data)
        {
            _owner.Dispatcher.Invoke(() =>
            {
                _progressDetailsWindow?.UpdateProgress(0, 1, data.Details, true, null); // Indeterminate progress, update current file
            });
        }

        public void OnVersionDownloadCompleted(object sender, (string TaskName, bool Success, string Message) data)
        {
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
