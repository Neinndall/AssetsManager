// AssetsManager/UI/UpdateProgressWindow.xaml.cs
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Serilog;

namespace AssetsManager.Views.Dialogs
{
    /// <summary>
    /// Interaction logic for UpdateProgressWindow.xaml
    /// </summary>
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
            // Update the progress bar value directly
            DownloadProgressBar.Value = percentage;
            MessageTextBlock.Text = message;
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                this.DragMove();
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}
