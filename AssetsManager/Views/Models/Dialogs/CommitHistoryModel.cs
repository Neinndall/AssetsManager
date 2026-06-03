using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using AssetsManager.Services.Core;
using AssetsManager.Utils.Framework;

namespace AssetsManager.Views.Models.Dialogs
{
    public class CommitGroup
    {
        public string DateHeader { get; set; }
        public List<GitHubCommit> Commits { get; set; } = new();
    }

    /// <summary>
    /// Pure data container for the Commit History window.
    /// Follows the project standard of keeping ViewModels focused on information and state.
    /// </summary>
    public class CommitHistoryModel : INotifyPropertyChanged
    {
        public ObservableRangeCollection<GitHubCommit> Commits { get; } = new();
        public ObservableRangeCollection<CommitGroup> GroupedCommits { get; } = new();

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

        public CommitHistoryModel()
        {
            StatusMessage = "Checking for commits...";
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    #region GitHub Models

    public class GitHubCommit
    {
        [JsonPropertyName("sha")]
        public string Sha { get; set; }

        [JsonPropertyName("commit")]
        public CommitInfo Commit { get; set; }

        [JsonPropertyName("author")]
        public GitHubUser Author { get; set; }

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; set; }

        // Local helper for UI to link with a build asset
        public GitHubAsset DownloadableAsset { get; set; }
        public GitHubAsset ParentBuildAsset { get; set; }
        public string ParentBuildSha { get; set; }
        public bool HasInheritedBuild => DownloadableAsset == null && ParentBuildAsset != null;
        public bool IsLatest { get; set; }

        private string _shortSha;
        public string ShortSha => _shortSha ?? (_shortSha = Sha?.Substring(0, 7) ?? "");

        private string _messageSummary;
        public string MessageSummary => _messageSummary ?? (_messageSummary = Commit?.Message?.Split('\n').FirstOrDefault()?.Trim() ?? string.Empty);

        private string _messageBody;
        private bool _messageBodyComputed;
        public string MessageBody
        {
            get
            {
                if (!_messageBodyComputed)
                {
                    _messageBodyComputed = true;
                    if (string.IsNullOrEmpty(Commit?.Message))
                    {
                        _messageBody = string.Empty;
                    }
                    else
                    {
                        var lines = Commit.Message.Split('\n');
                        if (lines.Length <= 1)
                        {
                            _messageBody = string.Empty;
                        }
                        else
                        {
                            var trimmedLines = lines.Skip(1)
                                                    .Select(l => l.Trim())
                                                    .Where(l => !string.IsNullOrEmpty(l));
                            _messageBody = string.Join(Environment.NewLine, trimmedLines);
                        }
                    }
                }
                return _messageBody;
            }
        }

        private bool? _hasDescription;
        public bool HasDescription => _hasDescription ??= !string.IsNullOrEmpty(MessageBody);
    }

    public class CommitInfo
    {
        [JsonPropertyName("message")]
        public string Message { get; set; }

        [JsonPropertyName("author")]
        public CommitAuthor Author { get; set; }
    }

    public class CommitAuthor
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("date")]
        public DateTime Date { get; set; }
    }

    public class GitHubUser
    {
        [JsonPropertyName("login")]
        public string Login { get; set; }

        [JsonPropertyName("avatar_url")]
        public string AvatarUrl { get; set; }
    }

    public class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset> Assets { get; set; }
    }

    public class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string DownloadUrl { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("created_at")]
        public DateTime CreatedAt { get; set; }
    }

    #endregion
}
