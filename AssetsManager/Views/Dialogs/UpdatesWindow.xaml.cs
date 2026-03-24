using System;
using System.Diagnostics;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using AssetsManager.Views.Models.Dialogs;
using AssetsManager.Views.Models.Notifications;
using AssetsManager.Services.Core;
using AssetsManager.Services.Updater;

namespace AssetsManager.Views.Dialogs
{
    public partial class UpdatesWindow
    {
        private readonly UpdatesModel _viewModel;
        private readonly NotificationService _notificationService;

        public UpdatesWindow(NotificationService notificationService, GitHubApiService gitHubApi)
        {
            InitializeComponent();
            _notificationService = notificationService;
            
            // The model can now use the injected service instance
            _viewModel = new UpdatesModel(gitHubApi);
            DataContext = _viewModel;

            Loaded += async (s, e) => await _viewModel.LoadUpdatesAsync();
        }

        public void Initialize(string currentVersion)
        {
            _viewModel.CurrentVersion = currentVersion;
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            await _viewModel.LoadUpdatesAsync();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void BtnDownload_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is GitHubCommit commit)
            {
                if (commit.DownloadableAsset != null)
                {
                    // Open browser for download
                    Process.Start(new ProcessStartInfo(commit.DownloadableAsset.DownloadUrl) { UseShellExecute = true });
                    
                    _notificationService.AddNotification("Download started", $"Downloading build for commit {commit.ShortSha}", NotificationType.Info);
                }
            }
        }
    }
}
