using System;
using System.Windows;
using Serilog;
using AssetsManager.Views.Helpers;
using AssetsManager.Views.Models.Dialogs;

namespace AssetsManager.Views.Dialogs
{
    public partial class UpdateProgressWindow : HudWindow
    {
        private readonly UpdateProgressModel _viewModel;

        public UpdateProgressWindow()
        {
            InitializeComponent();
            _viewModel = new UpdateProgressModel();
            DataContext = _viewModel;
        }

        public void SetProgress(int percentage, string message)
        {
            if (!CheckAccess())
            {
                Dispatcher.Invoke(() => SetProgress(percentage, message));
                return;
            }

            Log.Debug($"UpdateProgressWindow: Setting progress to {percentage}% with message: {message}");
            _viewModel.ProgressPercentage = percentage;

            // Process message to clean up the details
            if (message != null)
            {
                string details = message;
                if (details.StartsWith("Downloading... ", StringComparison.OrdinalIgnoreCase))
                {
                    details = details.Substring("Downloading... ".Length);
                }
                else if (details.StartsWith("Downloading ", StringComparison.OrdinalIgnoreCase))
                {
                    details = details.Substring("Downloading ".Length);
                }
                
                _viewModel.DetailsText = details;
            }
        }
    }
}
