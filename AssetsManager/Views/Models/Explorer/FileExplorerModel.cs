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
        LoadingResults
    }

    public class FileExplorerModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private bool _isBusy;
        private bool _isTreeReady;
        private bool _isEmptyState;
        private bool _isNoResultsFound;
        private bool _hasFavorites;
        private ExplorerLoadingState _currentState;
        private string _loadingStatus = "Loading assets...";
        private string _loadingOperation = "LOADING";
        private string _loadingDetail = "Preparing the file explorer";
        private ObservableRangeCollection<FileSystemNodeModel> _rootNodes;
        private ExplorerToolbarModel _toolbar;

        private string _statusTitle;
        private string _statusDescription;
        private string _searchNoResultsTitle;
        private string _searchNoResultsDescription;
        private bool _isSelectDirectoryActionVisible;

        private FileSystemNodeModel _selectedItem;
        private ObservableCollection<FileSystemNodeModel> _selectedNodes = new();

        public FileExplorerModel()
        {
            RootNodes = new ObservableRangeCollection<FileSystemNodeModel>();
            Toolbar = new ExplorerToolbarModel();
            
            // Centralized Listener: React to ANY toolbar change and notify dependent properties
            Toolbar.PropertyChanged += (s, e) => 
            { 
                switch(e.PropertyName)
                {
                    case nameof(ExplorerToolbarModel.IsFavoritesEnabled):
                    case nameof(ExplorerToolbarModel.IsWadMode):
                    case nameof(ExplorerToolbarModel.IsBackupMode):
                        OnPropertyChanged(nameof(AreFavoritesVisible)); 
                        OnPropertyChanged(nameof(IsFavoritesToggleVisible));
                        OnPropertyChanged(nameof(IsWadMode));
                        OnPropertyChanged(nameof(IsBackupMode));
                        OnPropertyChanged(nameof(IsToolbarVisible));
                        break;
                    case nameof(ExplorerToolbarModel.IsGroupingEnabled):
                        OnPropertyChanged(nameof(IsSortingEnabled));
                        break;
                }
            };

            IsBusy = false;
            IsTreeReady = false;
            IsEmptyState = true; // Start empty

            SearchNoResultsTitle = "No Matching Results";
            SearchNoResultsDescription = "Try adjusting your search or filters.";
        }

        // ── Proxy Properties ─────────────────────────────────────────────────

        public bool IsWadMode
        {
            get => Toolbar.IsWadMode;
            set { if (Toolbar.IsWadMode != value) { Toolbar.IsWadMode = value; OnPropertyChanged(); } }
        }

        public bool IsBackupMode
        {
            get => Toolbar.IsBackupMode;
            set { if (Toolbar.IsBackupMode != value) { Toolbar.IsBackupMode = value; OnPropertyChanged(); } }
        }

        public bool IsSortingEnabled
        {
            get => !Toolbar.IsGroupingEnabled;
            set { if (Toolbar.IsGroupingEnabled == value) { Toolbar.IsGroupingEnabled = !value; OnPropertyChanged(); } }
        }

        public bool IsFavoritesEnabled
        {
            get => Toolbar.IsFavoritesEnabled;
            set { if (Toolbar.IsFavoritesEnabled != value) { Toolbar.IsFavoritesEnabled = value; OnPropertyChanged(); } }
        }

        // ── Standard Properties ──────────────────────────────────────────────

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
                _viewChangesHeader = null;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ViewChangesHeader));
                OnPropertyChanged(nameof(CanViewChanges));
            }
        }

        private string _viewChangesHeader;
        public string ViewChangesHeader 
        {
            get
            {
                if (_viewChangesHeader == null)
                {
                    int diffableCount = SelectedNodes.Count(n => n.ChunkDiff != null && n.Status != DiffStatus.Dependency && !SupportedFileTypes.IsAudioDataContainer(n.Name));
                    _viewChangesHeader = diffableCount > 1 ? "View Selected Differences" : "View Differences";
                }
                return _viewChangesHeader;
            }
        }

        public bool CanViewChanges
        {
            get
            {
                if (SelectedNodes.Count > 1)
                {
                    return SelectedNodes.Any(n => n.ChunkDiff != null && n.Status != DiffStatus.Dependency && !SupportedFileTypes.IsAudioDataContainer(n.Name));
                }

                return (SelectedItem?.Status == DiffStatus.Modified || (SelectedItem?.ChunkDiff != null && SelectedItem?.Status != DiffStatus.Dependency)) && !SupportedFileTypes.IsAudioDataContainer(SelectedItem?.Name);
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
                StatusTitle = "Asset explorer";
                StatusDescription = "Select your LoL installation directory to start exploring game assets.";
                IsSelectDirectoryActionVisible = true;
            }
            else
            {
                StatusTitle = "Assets directory";
                StatusDescription = "The local assets directory could not be found. Ensure you have extracted assets before exploring this mode.";
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

        public string SearchNoResultsTitle
        {
            get => _searchNoResultsTitle;
            set { if (_searchNoResultsTitle != value) { _searchNoResultsTitle = value; OnPropertyChanged(); } }
        }

        public string SearchNoResultsDescription
        {
            get => _searchNoResultsDescription;
            set { if (_searchNoResultsDescription != value) { _searchNoResultsDescription = value; OnPropertyChanged(); } }
        }

        public bool IsSelectDirectoryActionVisible
        {
            get => _isSelectDirectoryActionVisible;
            set { if (_isSelectDirectoryActionVisible != value) { _isSelectDirectoryActionVisible = value; OnPropertyChanged(); } }
        }

        public void SetLoadingState(ExplorerLoadingState state, string fileName = null, bool isDirectory = false)
        {
            _currentState = state; // Track current state

            if (state == ExplorerLoadingState.None)
            {
                IsBusy = false;
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

            string context = Toolbar.CurrentClientName?.ToLowerInvariant() ?? "assets";

            switch (state)
            {
                case ExplorerLoadingState.LoadingHashes:
                    LoadingStatus = "Synchronizing Hashes";
                    LoadingOperation = "HASH ENGINE";
                    LoadingDetail = "Loading dictionaries of hashes...";
                    break;
                case ExplorerLoadingState.LoadingWads:
                    LoadingStatus = "Loading WAD Files";
                    LoadingOperation = "EXPLORER";
                    if (fileName != null)
                    {
                        LoadingDetail = isDirectory ? $"Indexing {fileName}..." : $"Scanning {context} directories...";
                    }
                    else
                    {
                        LoadingDetail = $"Locating WAD files in {context}...";
                    }
                    break;
                case ExplorerLoadingState.ExploringDirectory:
                    LoadingStatus = "Exploring Local Assets";
                    LoadingOperation = "EXPLORER";
                    if (fileName != null)
                    {
                        LoadingDetail = isDirectory ? $"Entering {fileName}..." : $"Scanning {context} assets...";
                    }
                    else
                    {
                        LoadingDetail = $"Exploring local assets in {context}...";
                    }
                    break;
                case ExplorerLoadingState.LoadingResults:
                    LoadingStatus = "Loading Results";
                    LoadingOperation = "RESULTS";
                    LoadingDetail = "Reading comparison results...";
                    break;
                default:
                    LoadingStatus = "Loading Explorer";
                    LoadingOperation = "LOADING";
                    LoadingDetail = "Initializing components...";
                    break;
            }
        }

        public void UpdateScanningProgress(string fileName)
        {
            SetLoadingState(_currentState, fileName, false);
        }

        public void UpdateMountingProgress(string dirName)
        {
            SetLoadingState(_currentState, dirName, true);
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

        public bool IsNoResultsFound
        {
            get => _isNoResultsFound;
            set 
            { 
                if (_isNoResultsFound != value) 
                { 
                    _isNoResultsFound = value; 
                    OnPropertyChanged(); 
                } 
            }
        }

        // ── Computed Visibility Properties ─────────────────────────────────

        public bool AreFavoritesVisible => IsTreeReady && Toolbar.IsFavoritesEnabled && IsWadMode && !IsBackupMode && HasFavorites;

        public bool IsFavoritesToggleVisible => IsWadMode && !IsBackupMode;
        
        public bool IsToolbarVisible => IsTreeReady || !IsWadMode;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
