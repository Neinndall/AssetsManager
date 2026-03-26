using System.ComponentModel;
using System.Runtime.CompilerServices;
using AssetsManager.Views.Controls.Explorer;
using Material.Icons;

namespace AssetsManager.Views.Models.Explorer
{
    public class ExplorerToolbarModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        public FileExplorerControl ParentExplorer { get; set; } // Reference back for direct actions

        private bool _isWadMode = true;
        private bool _isGridMode = false;
        private bool _isBreadcrumbVisible = true;
        private bool _isFavoritesEnabled = false;
        private string _searchText = string.Empty;
        private bool _isGroupingEnabled;
        private bool _isBackupMode;
        private bool _isSearchVisible;
        private bool _isToolsExpanded;

        public bool IsBackupMode
        {
            get => _isBackupMode;
            set { if (_isBackupMode != value) { _isBackupMode = value; OnPropertyChanged(); } }
        }

        public bool IsGroupingEnabled
        {
            get => _isGroupingEnabled;
            set 
            { 
                if (_isGroupingEnabled != value) 
                { 
                    _isGroupingEnabled = value; 
                    OnPropertyChanged(); 
                    OnPropertyChanged(nameof(GroupingText));
                    OnPropertyChanged(nameof(GroupingIcon));
                } 
            }
        }

        // Action-Oriented: Show what happens when clicked
        public string GroupingText => IsGroupingEnabled ? "To Paths" : "Categories";
        public MaterialIconKind GroupingIcon => IsGroupingEnabled ? MaterialIconKind.Sitemap : MaterialIconKind.FormatListBulleted;

        public bool IsToolsExpanded
        {
            get => _isToolsExpanded;
            set { if (_isToolsExpanded != value) { _isToolsExpanded = value; OnPropertyChanged(); } }
        }

        public bool IsSearchVisible
        {
            get => _isSearchVisible;
            set { if (_isSearchVisible != value) { _isSearchVisible = value; OnPropertyChanged(); } }
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
                    OnPropertyChanged(nameof(SwitchModeText));
                    OnPropertyChanged(nameof(SwitchModeIcon));
                } 
            }
        }

        // Action-Oriented: Show destination mode
        public string SwitchModeText => IsWadMode ? "Local Dir" : "WAD Mode";
        public MaterialIconKind SwitchModeIcon => IsWadMode ? MaterialIconKind.FolderOutline : MaterialIconKind.ArchiveOutline;

        public bool IsGridMode
        {
            get => _isGridMode;
            set { if (_isGridMode != value) { _isGridMode = value; OnPropertyChanged(); } }
        }

        public bool IsBreadcrumbVisible
        {
            get => _isBreadcrumbVisible;
            set { if (_isBreadcrumbVisible != value) { _isBreadcrumbVisible = value; OnPropertyChanged(); } }
        }

        public bool IsFavoritesEnabled
        {
            get => _isFavoritesEnabled;
            set { if (_isFavoritesEnabled != value) { _isFavoritesEnabled = value; OnPropertyChanged(); } }
        }

        public string SearchText
        {
            get => _searchText;
            set { if (_searchText != value) { _searchText = value; OnPropertyChanged(); } }
        }

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
