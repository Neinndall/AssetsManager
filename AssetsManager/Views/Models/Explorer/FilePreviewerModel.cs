using AssetsManager.Views.Models.Wad;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AssetsManager.Views.Models.Explorer
{
    public class FilePreviewerModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private bool _isGridMode;
        private bool _isBreadcrumbToggleOn = true;
        private PinnedFilesManager _pinnedFilesManager;

        private bool _hasSelectedNode;
        private bool _isSelectedNodeContainer;

        private SerializableChunkDiff _renamedDiffDetails;
        public SerializableChunkDiff RenamedDiffDetails
        {
            get => _renamedDiffDetails;
            set { _renamedDiffDetails = value; OnPropertyChanged(); }
        }

        private bool _isRenamedDetailsTabVisible;
        public bool IsRenamedDetailsTabVisible
        {
            get => _isRenamedDetailsTabVisible;
            set 
            { 
                if (_isRenamedDetailsTabVisible != value)
                {
                    _isRenamedDetailsTabVisible = value; 
                    OnPropertyChanged(); 
                    OnPropertyChanged(nameof(AreTabsVisible));
                }
            }
        }

        public bool AreTabsVisible => PinnedFilesManager.PinnedFiles.Count > 0 || IsRenamedDetailsTabVisible;

        private bool _isDetailsTabSelected;
        public bool IsDetailsTabSelected
        {
            get => _isDetailsTabSelected;
            set { _isDetailsTabSelected = value; OnPropertyChanged(); }
        }

        public FilePreviewerModel()
        {
            PinnedFilesManager = new PinnedFilesManager();
            PinnedFilesManager.PinnedFiles.CollectionChanged += (s, e) => OnPropertyChanged(nameof(AreTabsVisible));
        }

        public PinnedFilesManager PinnedFilesManager
        {
            get => _pinnedFilesManager;
            set { _pinnedFilesManager = value; OnPropertyChanged(); }
        }

        public bool IsGridMode
        {
            get => _isGridMode;
            set
            {
                if (_isGridMode != value)
                {
                    _isGridMode = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsGridVisible));
                    OnPropertyChanged(nameof(IsPreviewVisible));
                }
            }
        }

        public bool IsSelectedNodeContainer
        {
            get => _isSelectedNodeContainer;
            set
            {
                if (_isSelectedNodeContainer != value)
                {
                    _isSelectedNodeContainer = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsGridVisible));
                    OnPropertyChanged(nameof(IsPreviewVisible));
                }
            }
        }

        public bool IsGridVisible => IsGridMode && HasSelectedNode && IsSelectedNodeContainer;
        public bool IsPreviewVisible => !IsGridVisible;

        public bool HasSelectedNode
        {
            get => _hasSelectedNode;
            set
            {
                if (_hasSelectedNode != value)
                {
                    _hasSelectedNode = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(AreBreadcrumbsVisible));
                    OnPropertyChanged(nameof(IsGridVisible));
                    OnPropertyChanged(nameof(IsPreviewVisible));
                }
            }
        }

        public bool IsBreadcrumbToggleOn
        {
            get => _isBreadcrumbToggleOn;
            set
            {
                if (_isBreadcrumbToggleOn != value)
                {
                    _isBreadcrumbToggleOn = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(AreBreadcrumbsVisible));
                }
            }
        }

        public bool AreBreadcrumbsVisible => IsBreadcrumbToggleOn && HasSelectedNode;

        private bool _isDetailsVisible;
        public bool IsDetailsVisible
        {
            get => _isDetailsVisible;
            set { _isDetailsVisible = value; OnPropertyChanged(); }
        }

        private bool _isFindVisible;
        public bool IsFindVisible
        {
            get => _isFindVisible;
            set { _isFindVisible = value; OnPropertyChanged(); }
        }

        private bool _isPlaceholderVisible = true;
        public bool IsPlaceholderVisible
        {
            get => _isPlaceholderVisible;
            set { _isPlaceholderVisible = value; OnPropertyChanged(); }
        }

        private bool _isImageVisible;
        public bool IsImageVisible
        {
            get => _isImageVisible;
            set { _isImageVisible = value; OnPropertyChanged(); }
        }

        private bool _isTextVisible;
        public bool IsTextVisible
        {
            get => _isTextVisible;
            set { _isTextVisible = value; OnPropertyChanged(); }
        }

        private bool _isWebVisible;
        public bool IsWebVisible
        {
            get => _isWebVisible;
            set { _isWebVisible = value; OnPropertyChanged(); }
        }

        private bool _isSelectFileMessageVisible = true;
        public bool IsSelectFileMessageVisible
        {
            get => _isSelectFileMessageVisible;
            set { _isSelectFileMessageVisible = value; OnPropertyChanged(); }
        }

        private bool _isUnsupportedFileMessageVisible;
        public bool IsUnsupportedFileMessageVisible
        {
            get => _isUnsupportedFileMessageVisible;
            set { _isUnsupportedFileMessageVisible = value; OnPropertyChanged(); }
        }

        private bool _canScrollLeft;
        public bool CanScrollLeft
        {
            get => _canScrollLeft;
            set { _canScrollLeft = value; OnPropertyChanged(); }
        }

        private bool _canScrollRight;
        public bool CanScrollRight
        {
            get => _canScrollRight;
            set { _canScrollRight = value; OnPropertyChanged(); }
        }

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}