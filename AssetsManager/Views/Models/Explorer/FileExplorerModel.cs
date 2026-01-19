using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace AssetsManager.Views.Models.Explorer
{
    public enum ExplorerLoadingState
    {
        None,
        LoadingHashes,
        LoadingWads,
        ExploringDirectory,
        LoadingBackup
    }

    public class FileExplorerModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private bool _isBusy;
        private bool _isTreeReady;
        private bool _isEmptyState;
        private bool _isWadMode = true; // Default WAD mode
        private bool _isBackupMode = false;
        private bool _isSortingEnabled = true;
        private bool _isFavoritesEnabled = true; // Default toggle state
        private bool _hasFavorites;
        private string _loadingStatus = "Loading assets...";
        private string _loadingOperation = "LOADING";
        private string _loadingDetail = "Preparing the file explorer";
        private ObservableCollection<FileSystemNodeModel> _rootNodes;

        public FileExplorerModel()
        {
            RootNodes = new ObservableCollection<FileSystemNodeModel>();
            IsBusy = false;
            IsTreeReady = false;
            IsEmptyState = true; // Start empty
        }

        public void SetLoadingState(ExplorerLoadingState state)
        {
            if (state == ExplorerLoadingState.None)
            {
                IsBusy = false;
                // Only show tree if we actually have nodes
                if (RootNodes.Count > 0)
                {
                    IsTreeReady = true;
                    IsEmptyState = false;
                }
                return;
            }

            IsBusy = true;
            IsTreeReady = false;
            IsEmptyState = false;

            switch (state)
            {
                case ExplorerLoadingState.LoadingHashes:
                    LoadingStatus = "Synchronizing Hashes";
                    LoadingOperation = "HASH ENGINE";
                    LoadingDetail = "Loading dictionaries of hashes...";
                    break;
                case ExplorerLoadingState.LoadingWads:
                    LoadingStatus = "Loading WAD Files";
                    LoadingOperation = "WAD EXPLORER";
                    LoadingDetail = "Scanning files from the directory...";
                    break;
                case ExplorerLoadingState.ExploringDirectory:
                    LoadingStatus = "Exploring Directory";
                    LoadingOperation = "DIRECTORY";
                    LoadingDetail = "Scanning files from the directory...";
                    break;
                case ExplorerLoadingState.LoadingBackup:
                    LoadingStatus = "Loading Backup";
                    LoadingOperation = "BACKUP";
                    LoadingDetail = "Reading Backup File...";
                    break;
                default:
                    LoadingStatus = "Loading Explorer";
                    LoadingOperation = "LOADING";
                    LoadingDetail = "Initializing components...";
                    break;
            }
        }

        public string LoadingOperation
        {
            get => _loadingOperation;
            set { if (_loadingOperation != value) { _loadingOperation = value; OnPropertyChanged(); } }
        }

        public string LoadingStatus
        {
            get => _loadingStatus;
            set { if (_loadingStatus != value) { _loadingStatus = value; OnPropertyChanged(); } }
        }

        public string LoadingDetail
        {
            get => _loadingDetail;
            set { if (_loadingDetail != value) { _loadingDetail = value; OnPropertyChanged(); } }
        }

        public ObservableCollection<FileSystemNodeModel> RootNodes
        {
            get => _rootNodes;
            set { _rootNodes = value; OnPropertyChanged(); }
        }

        public bool HasFavorites
        {
            get => _hasFavorites;
            set
            {
                if (_hasFavorites != value)
                {
                    _hasFavorites = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(AreFavoritesVisible));
                }
            }
        }

        public bool IsBusy
        {
            get => _isBusy;
            set { if (_isBusy != value) { _isBusy = value; OnPropertyChanged(); } }
        }

        public bool IsTreeReady
        {
            get => _isTreeReady;
            set 
            { 
                if (_isTreeReady != value) 
                { 
                    _isTreeReady = value; 
                    OnPropertyChanged(); 
                    OnPropertyChanged(nameof(AreFavoritesVisible));
                    OnPropertyChanged(nameof(IsToolbarVisible));
                } 
            }
        }

        public bool IsEmptyState
        {
            get => _isEmptyState;
            set 
            { 
                if (_isEmptyState != value) 
                { 
                    _isEmptyState = value; 
                    OnPropertyChanged(); 
                    OnPropertyChanged(nameof(IsToolbarVisible));
                } 
            }
        }

        public bool IsWadMode
        {
            get => _isWadMode;
            set
            {
                if (_isWadMode != value)
                {
                    _isWadMode = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsToolbarVisible));
                    OnPropertyChanged(nameof(AreFavoritesVisible));
                    OnPropertyChanged(nameof(IsFavoritesToggleVisible));
                }
            }
        }

        public bool IsBackupMode
        {
            get => _isBackupMode;
            set 
            { 
                if (_isBackupMode != value) 
                { 
                    _isBackupMode = value; 
                    OnPropertyChanged(); 
                    OnPropertyChanged(nameof(AreFavoritesVisible));
                    OnPropertyChanged(nameof(IsFavoritesToggleVisible));
                } 
            }
        }

        public bool IsSortingEnabled
        {
            get => _isSortingEnabled;
            set { if (_isSortingEnabled != value) { _isSortingEnabled = value; OnPropertyChanged(); } }
        }

        public bool IsFavoritesEnabled
        {
            get => _isFavoritesEnabled;
            set
            {
                if (_isFavoritesEnabled != value)
                {
                    _isFavoritesEnabled = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(AreFavoritesVisible));
                }
            }
        }

        // Computed property for visibility
        public bool AreFavoritesVisible => IsTreeReady && IsFavoritesEnabled && IsWadMode && !IsBackupMode && HasFavorites;

        public bool IsFavoritesToggleVisible => IsWadMode && !IsBackupMode;
        
        // Toolbar is visible if Tree is ready OR if we are in Directory Mode (even if empty, to allow switching back)
        public bool IsToolbarVisible => IsTreeReady || (IsEmptyState && !IsWadMode);

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
