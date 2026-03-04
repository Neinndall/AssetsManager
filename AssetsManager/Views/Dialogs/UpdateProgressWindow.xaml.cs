using System;
using System.Windows;
using Serilog;

namespace AssetsManager.Views.Dialogs
{
    public partial class UpdateProgressWindow : Window
    {
        public UpdateProgressWindow()
        {
            InitializeComponent();
        }

        public void SetProgress(int percentage, string message)
        {
            if (!CheckAccess())
            {
                Dispatcher.Invoke(() => SetProgress(percentage, message));
                return;
            }

            Log.Debug($"UpdateProgressWindow: Setting progress to {percentage}% with message: {message}");
            DownloadProgressBar.Value = percentage;
            MessageTextBlock.Text = message;
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
