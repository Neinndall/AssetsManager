using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using AssetsManager.Utils;
using AssetsManager.Utils.Framework;

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
        private bool _isFavoritesEnabled = false; // Default toggle state
        private bool _hasFavorites;
        private string _loadingStatus = "Loading assets...";
        private string _loadingOperation = "LOADING";
        private string _loadingDetail = "Preparing the file explorer";
        private ObservableRangeCollection<FileSystemNodeModel> _rootNodes;
        private ExplorerToolbarModel _toolbar;

        private string _statusTitle;
        private string _statusDescription;
        private bool _isSelectDirectoryActionVisible;

        private FileSystemNodeModel _selectedItem;
        private ObservableCollection<FileSystemNodeModel> _selectedNodes = new();

        public FileExplorerModel()
        {
            RootNodes = new ObservableRangeCollection<FileSystemNodeModel>();
            Toolbar = new ExplorerToolbarModel();
            IsBusy = false;
            IsTreeReady = false;
            IsEmptyState = true; // Start empty
        }

        public FileSystemNodeModel SelectedItem
        {
            get => _selectedItem;
            set
            {
                if (_selectedItem != value)
                {
                    _selectedItem = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ViewChangesHeader));
                    OnPropertyChanged(nameof(CanViewChanges));
                }
            }
        }

        public ObservableCollection<FileSystemNodeModel> SelectedNodes
        {
            get => _selectedNodes;
            set
            {
                _selectedNodes = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ViewChangesHeader));
                OnPropertyChanged(nameof(CanViewChanges));
            }
        }

        public string ViewChangesHeader => SelectedNodes.Count > 1 
            ? "View Selected Differences" 
            : "View Differences";

        public bool CanViewChanges
        {
            get
            {
                if (SelectedNodes.Count > 1)
                    return SelectedNodes.Any(n => n.ChunkDiff != null && !SupportedFileTypes.IsAudioDataContainer(n.Name));

                return (SelectedItem?.Status == DiffStatus.Modified || SelectedItem?.ChunkDiff != null) && !SupportedFileTypes.IsAudioDataContainer(SelectedItem?.Name);
            }
        }

        public ExplorerToolbarModel Toolbar
        {
            get => _toolbar;
            set { _toolbar = value; OnPropertyChanged(); }
        }

        public void UpdateEmptyState(bool isWadMode)
        {
            if (isWadMode)
            {
                StatusTitle = "Select a LoL Directory";
                StatusDescription = "Choose the root folder where you installed League of Legends to browse its WAD files.";
                IsSelectDirectoryActionVisible = true;
            }
            else
            {
                StatusTitle = "Assets Directory Not Found";
                StatusDescription = "The application could not find the directory for downloaded assets.";
                IsSelectDirectoryActionVisible = false;
            }

            RootNodes.Clear();
            IsBusy = false;
            IsTreeReady = false;
            IsEmptyState = true;
            OnPropertyChanged(nameof(IsToolbarVisible));
        }

        public string StatusTitle
        {
            get => _statusTitle;
            set { if (_statusTitle != value) { _statusTitle = value; OnPropertyChanged(); } }
        }

        public string StatusDescription
        {
            get => _statusDescription;
            set { if (_statusDescription != value) { _statusDescription = value; OnPropertyChanged(); } }
        }

        public bool IsSelectDirectoryActionVisible
        {
            get => _isSelectDirectoryActionVisible;
            set { if (_isSelectDirectoryActionVisible != value) { _isSelectDirectoryActionVisible = value; OnPropertyChanged(); } }
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

        public ObservableRangeCollection<FileSystemNodeModel> RootNodes
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
                    Toolbar.IsWadMode = value;
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
                    Toolbar.IsBackupMode = value;
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
                    Toolbar.IsFavoritesEnabled = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(AreFavoritesVisible));
                }
            }
        }

        // Computed property for visibility
        public bool AreFavoritesVisible => IsTreeReady && IsFavoritesEnabled && IsWadMode && !IsBackupMode && HasFavorites;

        public bool IsFavoritesToggleVisible => IsWadMode && !IsBackupMode;
        
        // Toolbar is visible if Tree is ready OR if we are NOT in WAD mode 
        // (to allow switching back to WAD mode even if the directory is empty)
        public bool IsToolbarVisible => IsTreeReady || !IsWadMode;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
