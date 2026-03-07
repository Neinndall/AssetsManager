using System;
using System.Windows;
using Serilog;
using AssetsManager.Views.Helpers;

namespace AssetsManager.Views.Dialogs
{
    public partial class UpdateProgressWindow : HudWindow
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
    }
}
