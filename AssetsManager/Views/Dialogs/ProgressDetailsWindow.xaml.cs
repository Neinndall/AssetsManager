using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using AssetsManager.Views.Helpers;
using AssetsManager.Views.Models.Dialogs;
using Material.Icons;

namespace AssetsManager.Views.Dialogs
{
    public partial class ProgressDetailsWindow : HudWindow
    {
        private readonly LogService _logService;
        private DateTime _startTime;
        private int _completedFiles;
        private int _totalFiles;
        private readonly DispatcherTimer _timer;

        public ProgressDetailsModel ViewModel { get; }

        private readonly TaskCancellationManager _taskCancellationManager;

        public ProgressDetailsWindow(LogService logService, string windowTitle, TaskCancellationManager taskCancellationManager)
        {
            InitializeComponent();
            _logService = logService;
            _taskCancellationManager = taskCancellationManager;
            _startTime = DateTime.Now;

            ViewModel = new ProgressDetailsModel();
            this.DataContext = ViewModel;

            this.HeaderTitle = windowTitle;
            this.HeaderIcon = MaterialIconKind.ProgressClock;

            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _timer.Tick += Timer_Tick;
            _timer.Start();
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            UpdateEstimatedTime(_completedFiles, _totalFiles);
        }

        public void UpdateProgress(int completedFiles, int totalFiles, string currentFileName, bool isSuccess, string errorMessage)
        {
            _completedFiles = completedFiles;
            _totalFiles = totalFiles;

            string verb = ViewModel.OperationVerb;

            if (!string.IsNullOrEmpty(currentFileName) && currentFileName.Contains(" of ") && currentFileName.Contains(": "))
            {
                var mainParts = currentFileName.Split(new[] { ": " }, 2, StringSplitOptions.None);
                if (mainParts.Length == 2)
                {
                    var countParts = mainParts[0].Split(new[] { " " }, StringSplitOptions.RemoveEmptyEntries);
                    int startIdx = 0;
                    if (countParts.Length > 0 && !char.IsDigit(countParts[0][0])) 
                    {
                        startIdx = 1;
                        verb = countParts[0];
                        ViewModel.OperationVerb = verb;
                    }

                    if (countParts.Length >= startIdx + 3)
                    {
                        ViewModel.ItemProgressText = $"{countParts[startIdx]} of {countParts[startIdx + 2]}";
                    }
                    else
                    {
                        ViewModel.ItemProgressText = mainParts[0];
                    }
                    
                    ViewModel.CurrentFileName = mainParts[1];
                }
                else
                {
                    ViewModel.ItemProgressText = "0 / 0";
                    ViewModel.CurrentFileName = currentFileName;
                }
            }
            else
            {
                ViewModel.ItemProgressText = $"{completedFiles} of {totalFiles}";
                ViewModel.CurrentFileName = currentFileName ?? "...";
            }

            string fileSpecificProgress = null;
            string cleanFileName = ViewModel.CurrentFileName;
            if (cleanFileName != null && cleanFileName.Contains("|"))
            {
                var pipeParts = cleanFileName.Split('|');
                ViewModel.CurrentFileName = pipeParts[0].Trim();
                if (pipeParts.Length > 1) fileSpecificProgress = pipeParts[1].Trim();
            }

            ViewModel.ShowChunks = verb == "Comparing" || verb == "Updating";
            
            if (ViewModel.ShowChunks)
            {
                if (!string.IsNullOrEmpty(fileSpecificProgress))
                {
                    string formattedFileProgress = fileSpecificProgress.Replace("/", " of ");
                    ViewModel.SubProgressText = $"Chunks: {formattedFileProgress} ({completedFiles}/{totalFiles})";
                }
                else
                {
                    ViewModel.SubProgressText = $"Chunks: {completedFiles} of {totalFiles}";
                }
            }

            if (totalFiles > 0)
            {
                ViewModel.ProgressValue = (double)completedFiles / totalFiles * 100;
            }
            else
            {
                ViewModel.ProgressValue = 0;
            }

            UpdateEstimatedTime(completedFiles, totalFiles);

            if (completedFiles >= totalFiles && totalFiles > 0)
            {
                _timer.Stop();
                ViewModel.EstimatedTimeText = "Estimated time remaining: 00:00:00";
                ViewModel.ProgressValue = 100;
            }
        }

        private void UpdateEstimatedTime(int completedFiles, int totalFiles)
        {
            if (completedFiles == 0 || totalFiles == 0)
            {
                ViewModel.EstimatedTimeText = "Estimated time: Calculating...";
                return;
            }

            TimeSpan elapsed = DateTime.Now - _startTime;
            double progress = (double)completedFiles / totalFiles;

            if (progress > 0)
            {
                TimeSpan estimatedTotalTime = TimeSpan.FromSeconds(elapsed.TotalSeconds / progress);
                TimeSpan estimatedRemainingTime = estimatedTotalTime - elapsed;

                if (estimatedRemainingTime.TotalSeconds < 0)
                {
                    ViewModel.EstimatedTimeText = "Estimated time remaining: 00:00:00";
                }
                else
                {
                    ViewModel.EstimatedTimeText = $"Estimated time remaining: {estimatedRemainingTime.ToString(@"hh\:mm\:ss")}";
                }
            }
            else
            {
                ViewModel.EstimatedTimeText = "Estimated time: Calculating...";
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // If the process is not finished, just hide the window to keep background progress
            if (!ViewModel.IsFinished && Application.Current != null && !Application.Current.Dispatcher.HasShutdownStarted)
            {
                e.Cancel = true;
                this.Hide();
                return;
            }

            // If finished or app shutdown, clean up resources and allow closure
            if (_timer != null)
            {
                _timer.Stop();
                _timer.Tick -= Timer_Tick;
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _taskCancellationManager.CancelCurrentOperation();
            if (sender is Button button) button.IsEnabled = false;
        }
    }
}
