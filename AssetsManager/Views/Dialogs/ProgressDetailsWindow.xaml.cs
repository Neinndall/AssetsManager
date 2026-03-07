using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using AssetsManager.Views.Helpers;
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

        public string OperationVerb { get; set; }

        private readonly TaskCancellationManager _taskCancellationManager;

        public ProgressDetailsWindow(LogService logService, string windowTitle, TaskCancellationManager taskCancellationManager)
        {
            InitializeComponent();
            _logService = logService;
            _taskCancellationManager = taskCancellationManager;
            _startTime = DateTime.Now;

            this.HeaderTitle = windowTitle;
            this.HeaderIcon = MaterialIconKind.ProgressClock;
            this.DataContext = this;

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

            if (!string.IsNullOrEmpty(OperationVerb)) VerbTextBlock.Text = OperationVerb;

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
                        VerbTextBlock.Text = countParts[0];
                        OperationVerb = countParts[0];
                    }

                    if (countParts.Length >= startIdx + 3)
                    {
                        ItemProgressTextBlock.Text = $"{countParts[startIdx]} of {countParts[startIdx + 2]}";
                    }
                    else
                    {
                        ItemProgressTextBlock.Text = mainParts[0];
                    }
                    
                    CurrentFileTextBlock.Text = mainParts[1];
                }
                else
                {
                    ItemProgressTextBlock.Text = "0 / 0";
                    CurrentFileTextBlock.Text = currentFileName;
                }
            }
            else
            {
                ItemProgressTextBlock.Text = $"{completedFiles} of {totalFiles}";
                CurrentFileTextBlock.Text = currentFileName ?? "...";
            }

            string fileSpecificProgress = null;
            if (CurrentFileTextBlock.Text.Contains("|"))
            {
                var pipeParts = CurrentFileTextBlock.Text.Split('|');
                CurrentFileTextBlock.Text = pipeParts[0].Trim();
                if (pipeParts.Length > 1) fileSpecificProgress = pipeParts[1].Trim();
            }

            bool showChunks = OperationVerb == "Comparing" || OperationVerb == "Updating";
            
            if (showChunks)
            {
                SubProgressRow.Visibility = Visibility.Visible;
                if (!string.IsNullOrEmpty(fileSpecificProgress))
                {
                    string formattedFileProgress = fileSpecificProgress.Replace("/", " of ");
                    SubProgressTextBlock.Text = $"Chunks: {formattedFileProgress} ({completedFiles}/{totalFiles})";
                }
                else
                {
                    SubProgressTextBlock.Text = $"Chunks: {completedFiles} of {totalFiles}";
                }
            }
            else
            {
                SubProgressRow.Visibility = Visibility.Collapsed;
            }

            if (totalFiles > 0)
            {
                MainProgressBar.Value = (double)completedFiles / totalFiles * 100;
            }
            else
            {
                MainProgressBar.Value = 0;
            }

            UpdateEstimatedTime(completedFiles, totalFiles);

            if (completedFiles >= totalFiles && totalFiles > 0)
            {
                _timer.Stop();
                EstimatedTimeTextBlock.Text = "Estimated time remaining: 00:00:00";
                MainProgressBar.Value = 100;
            }
        }

        private void UpdateEstimatedTime(int completedFiles, int totalFiles)
        {
            if (completedFiles == 0 || totalFiles == 0)
            {
                EstimatedTimeTextBlock.Text = "Estimated time: Calculating...";
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
                    EstimatedTimeTextBlock.Text = "Estimated time remaining: 00:00:00";
                }
                else
                {
                    EstimatedTimeTextBlock.Text = $"Estimated time remaining: {estimatedRemainingTime.ToString(@"hh\:mm\:ss")}";
                }
            }
            else
            {
                EstimatedTimeTextBlock.Text = "Estimated time: Calculating...";
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // Instead of closing, we hide it to preserve state if needed,
            // but we must check if we are actually shutting down the app.
            if (Application.Current != null && !Application.Current.Dispatcher.HasShutdownStarted)
            {
                e.Cancel = true;
                this.Hide();
            }
            
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
