using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using AssetsManager.Services.Core;
using AssetsManager.Utils.Framework;
using System.Collections.Generic;
using System.Linq;
using Serilog;

namespace AssetsManager.Views.Models.Dialogs
{
    public class CommitHistoryModel : INotifyPropertyChanged
    {
        private readonly GitHubApiService _gitHubApi;
        
        public ObservableRangeCollection<GitHubCommit> Commits { get; } = new();

        private string _currentVersion;
        public string CurrentVersion { get => _currentVersion; set { _currentVersion = value; OnPropertyChanged(); } }

        private string _availableVersion;
        public string AvailableVersion { get => _availableVersion; set { _availableVersion = value; OnPropertyChanged(); } }

        private bool _isUpdateAvailable;
        public bool IsUpdateAvailable { get => _isUpdateAvailable; set { _isUpdateAvailable = value; OnPropertyChanged(); } }

        private bool _isLoading;
        public bool IsLoading { get => _isLoading; set { _isLoading = value; OnPropertyChanged(); } }

        private string _statusMessage;
        public string StatusMessage { get => _statusMessage; set { _statusMessage = value; OnPropertyChanged(); } }

        public CommitHistoryModel(GitHubApiService gitHubApi)
        {
            _gitHubApi = gitHubApi;
            StatusMessage = "Checking for revisions...";
        }

        public async Task LoadUpdatesAsync()
        {
            IsLoading = true;
            try
            {
                // 1. Fetch real commit history from 'qa' branch
                var commits = await _gitHubApi.GetCommitsAsync("qa", 20);
                
                // 2. Fetch specific QA release (like FModel's 'qa')
                var developmentRelease = await _gitHubApi.GetReleaseAsync("qa-testing");
                var assets = developmentRelease?.Assets?.OrderByDescending(a => a.CreatedAt).ToList() ?? new List<GitHubAsset>();

                Log.Information("Fetched {CommitCount} commits and {AssetCount} assets from QA release", commits.Count, assets.Count);

                // 3. Link real commits with assets
                foreach (var commit in commits)
                {
                    commit.DownloadableAsset = assets.FirstOrDefault(a => 
                        !string.IsNullOrEmpty(commit.Sha) && a.Name.Contains(commit.Sha, StringComparison.OrdinalIgnoreCase));
                    
                    commit.IsLatest = commits.IndexOf(commit) == 0;
                }

                // 4. FModel Logic: Add virtual commits for orphaned assets (assets without a commit in the recent list)
                foreach (var asset in assets)
                {
                    bool isOrphan = !commits.Any(c => c.DownloadableAsset?.DownloadUrl == asset.DownloadUrl);
                    if (isOrphan)
                    {
                        // Extract SHA from filename (SHA.zip or AssetsManager_qa_SHA.zip)
                        string sha = asset.Name.Replace(".zip", "");
                        if (sha.Contains("qa_")) sha = sha.Split("qa_").Last();
                        
                        commits.Add(new GitHubCommit
                        {
                            Sha = sha,
                            Commit = new CommitInfo 
                            { 
                                Message = $"Build found in QA release ({asset.Name})",
                                Author = new CommitAuthor { Name = "GitHub Action", Date = asset.CreatedAt }
                            },
                            DownloadableAsset = asset,
                            IsLatest = false // Real commits take precedence for the LATEST tag
                        });
                    }
                }

                // Sort by date to maintain chronological order
                var finalSorted = commits.OrderByDescending(c => c.Commit.Author.Date).ToList();
                
                Commits.ReplaceRange(finalSorted);
                StatusMessage = Commits.Count > 0 ? "Revisions synchronized" : "No revisions found";
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to synchronize QA updates");
                StatusMessage = "Synchronization failed";
            }
            finally
            {
                IsLoading = false;
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
