using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Threading; // Added for CancellationToken and OperationCanceledException
using AssetsManager.Services;
using AssetsManager.Services.Core;
using AssetsManager.Utils; // Added for TaskCancellationManager

namespace AssetsManager.Views.Dialogs
{
    public partial class ProgressDetailsWindow : Window
    {
        private readonly LogService _logService;
        private DateTime _startTime;
        private int _completedFiles;
        private int _totalFiles;
        private readonly DispatcherTimer _timer;

        public string OperationVerb { get; set; }
        public string WindowTitle { get; set; }
        public string HeaderIconKind { get; set; }
        public string HeaderText { get; set; }

        private readonly TaskCancellationManager _taskCancellationManager; // New field

        public ProgressDetailsWindow(LogService logService, string windowTitle, TaskCancellationManager taskCancellationManager) // Add new parameter
        {
            InitializeComponent();
            _logService = logService;
            _taskCancellationManager = taskCancellationManager; // Assign new dependency
            _startTime = DateTime.Now;

            this.Title = windowTitle; // Set the window title from parameter
            this.WindowTitle = windowTitle; // Also set the property for consistency
            this.DataContext = this; // Set DataContext for binding

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

            // Determine unit based on operation
            string unit = OperationVerb switch
            {
                "Comparing" => "Chunks",
                "Saving" => "Assets",
                "Extracting" => "Assets",
                "Downloading" => "Files",
                "Backing up" => "Files",
                _ => "Items"
            };

            // Intelligent parsing for technical messages (e.g. "1 of 30 files: Aatrox.wad.client")
            if (!string.IsNullOrEmpty(currentFileName) && currentFileName.Contains(" of ") && currentFileName.Contains(" files: "))
            {
                var parts = currentFileName.Split(new[] { " files: " }, 2, StringSplitOptions.None);
                if (parts.Length == 2)
                {
                    // parts[0] is "1 of 30" -> we format it as "File 1 / 30"
                    ItemProgressTextBlock.Text = $"File {parts[0].Replace("of", "/")}";
                    CurrentFileTextBlock.Text = parts[1];
                }
                else
                {
                    ItemProgressTextBlock.Text = "File 0 / 0";
                    CurrentFileTextBlock.Text = currentFileName;
                }
            }
            else
            {
                ItemProgressTextBlock.Text = "File 0 / 0";
                CurrentFileTextBlock.Text = currentFileName;
            }

            // Sub-progress text
            if (totalFiles > 0)
            {
                SubProgressTextBlock.Text = $"{unit}: {completedFiles} of {totalFiles}";
                MainProgressBar.Value = (double)completedFiles / totalFiles * 100;
            }
            else
            {
                SubProgressTextBlock.Text = "0 of 0";
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

                // Ensure remaining time is not negative and use robust formatting
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

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Hide();
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            _timer?.Stop();
            _timer.Tick -= Timer_Tick; // Unsubscribe to prevent memory leaks
            base.OnClosing(e);
        }

        private void TitleBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
            {
                this.DragMove();
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _taskCancellationManager.CancelCurrentOperation();
            if (sender is Button button)
            {
                button.IsEnabled = false;
            }
            // The comparison logic will handle closing the window after full cancellation.
        }
    }
}
