using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AssetsManager.Services.Core;
using AssetsManager.Utils.Framework;

namespace AssetsManager.Views.Models.Dialogs
{
    /// <summary>
    /// Pure data container for the Commit History window.
    /// Follows the project standard of keeping ViewModels focused on information and state.
    /// </summary>
    public class CommitHistoryModel : INotifyPropertyChanged
    {
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

        public CommitHistoryModel()
        {
            StatusMessage = "Checking for revisions...";
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
