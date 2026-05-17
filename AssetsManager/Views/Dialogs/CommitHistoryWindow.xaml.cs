using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using AssetsManager.Views.Models.Dialogs;
using AssetsManager.Services.Core;
using AssetsManager.Services.Updater;
using Serilog;

namespace AssetsManager.Views.Dialogs
{
    /// <summary>
    /// Orchestrator for the Commit History view.
    /// Handles service communication and updates the pure-data ViewModel.
    /// </summary>
    public partial class CommitHistoryWindow
    {
        private readonly CommitHistoryModel _viewModel;
        public CommitHistoryModel ViewModel => _viewModel;
        
        private readonly GitHubApiService _gitHubApi;
        private readonly UpdateManager _updateManager;
        private readonly LogService _logService;

        public CommitHistoryWindow(GitHubApiService gitHubApi, UpdateManager updateManager, LogService logService)
        {
            InitializeComponent();
            _gitHubApi = gitHubApi;
            _updateManager = updateManager;
            _logService = logService;
            
            // 1. Initialize Pure Data Model (Owner Pattern)
            _viewModel = new CommitHistoryModel();
            DataContext = _viewModel;

            // 2. Orchestrate initial load
            Loaded += async (s, e) => await LoadUpdatesAsync();
        }

        public void Initialize(string currentVersion)
        {
            _viewModel.CurrentVersion = currentVersion;
        }

        /// <summary>
        /// Main orchestration method to fetch and synchronize revision history.
        /// </summary>
        public async Task LoadUpdatesAsync()
        {
            _viewModel.IsLoading = true;
            try
            {
                // Call the enriched service layer with maximum history depth (100 commits)
                var enrichedCommits = await _gitHubApi.GetEnrichedCommitsAsync("qa", "qa-testing", 100);
                
                // Update UI state
                _viewModel.Commits.ReplaceRange(enrichedCommits);

                // Group commits by date for the new GitHub-style timeline
                var groups = enrichedCommits
                    .GroupBy(c => c.Commit.Author.Date.Date)
                    .OrderByDescending(g => g.Key)
                    .Select(g => new CommitGroup
                    {
                        DateHeader = $"Commits on {g.Key:MMM dd, yyyy}",
                        Commits = g.ToList()
                    })
                    .ToList();

                _viewModel.GroupedCommits.ReplaceRange(groups);
                _viewModel.StatusMessage = _viewModel.Commits.Count > 0 ? "Commits synchronized" : "No commits found";
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to synchronize QA updates");
                _viewModel.StatusMessage = "Synchronization failed";
            }
            finally
            {
                _viewModel.IsLoading = false;
            }
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            await LoadUpdatesAsync();
        }

        private async void BtnDownload_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is GitHubCommit commit)
            {
                var asset = commit.DownloadableAsset ?? commit.ParentBuildAsset;
                var sha = commit.DownloadableAsset != null ? commit.ShortSha : commit.ParentBuildSha;

                if (asset != null)
                {
                    // Delegate the download and install process to the UpdateManager service
                    await _updateManager.DownloadAndInstallDevelopmentBuildAsync(
                        asset.DownloadUrl,
                        asset.Size,
                        sha,
                        this);
                }
            }
        }
    }
}
