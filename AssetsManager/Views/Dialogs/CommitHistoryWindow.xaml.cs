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
    public partial class CommitHistoryWindow
    {
        private readonly UpdatesModel _viewModel;
        private readonly NotificationService _notificationService;
        private readonly UpdateManager _updateManager;

        public CommitHistoryWindow(NotificationService notificationService, GitHubApiService gitHubApi, UpdateManager updateManager)
        {
            InitializeComponent();
            _notificationService = notificationService;
            _updateManager = updateManager;
            
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

        private async void BtnDownload_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is GitHubCommit commit)
            {
                if (commit.DownloadableAsset != null)
                {
                    // Delegate the download and install process to the UpdateManager service
                    await _updateManager.DownloadAndInstallDevelopmentBuildAsync(
                        commit.DownloadableAsset.DownloadUrl,
                        commit.DownloadableAsset.Size,
                        commit.ShortSha,
                        this);
                }
            }
        }
    }
}
