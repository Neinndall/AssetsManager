using System;
using System.Windows;
using System.Windows.Input;
using Serilog;
using MahApps.Metro.Controls;

namespace AssetsManager.Views.Dialogs
{
    public partial class UpdateProgressWindow : MetroWindow
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

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                this.DragMove();
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

        private void CloseButton_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);
    }
}
