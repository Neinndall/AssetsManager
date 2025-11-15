using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AssetsManager.Services;
using AssetsManager.Services.Core;

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

        public ProgressDetailsWindow(LogService logService, string windowTitle) // Add windowTitle parameter
        {
            InitializeComponent();
            _logService = logService;
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

            ProgressSummaryTextBlock.Text = $"{OperationVerb}: {completedFiles} of {totalFiles}";
            CurrentFileTextBlock.Text = $"Current file: {currentFileName}";
            UpdateEstimatedTime(completedFiles, totalFiles); // Update once immediately for responsiveness

            if (completedFiles >= totalFiles && totalFiles > 0)
            {
                _timer.Stop();
                EstimatedTimeTextBlock.Text = "Estimated time remaining: 00:00:00";
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
    }
}
