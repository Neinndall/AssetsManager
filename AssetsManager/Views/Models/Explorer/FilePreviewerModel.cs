using AssetsManager.Utils;
using AssetsManager.Views.Models.Wad;
using System.ComponentModel;
using System.Linq;
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
            set 
            { 
                _renamedDiffDetails = value; 
                OnPropertyChanged(); 
                OnPropertyChanged(nameof(IsRenamedInfoVisible));
            }
        }

        public bool IsRenamedInfoVisible => RenamedDiffDetails != null && !string.IsNullOrEmpty(RenamedDiffDetails.OldPath) && RenamedDiffDetails.OldPath != RenamedDiffDetails.NewPath;

        public bool AreTabsVisible => PinnedFilesManager.PinnedFiles.Count > 0;

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

        private bool _isWelcomeVisible = true;
        public bool IsWelcomeVisible
        {
            get => _isWelcomeVisible;
            set { _isWelcomeVisible = value; OnPropertyChanged(); }
        }

        private bool _isUnsupportedVisible;
        public bool IsUnsupportedVisible
        {
            get => _isUnsupportedVisible;
            set { _isUnsupportedVisible = value; OnPropertyChanged(); }
        }

        private bool _isImageVisible;
        public bool IsImageVisible
        {
            get => _isImageVisible;
            set { _isImageVisible = value; OnPropertyChanged(); }
        }

        private bool _isContentVisible;
        public bool IsContentVisible
        {
            get => _isContentVisible;
            set { _isContentVisible = value; OnPropertyChanged(); }
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

        private bool _isFindVisible;
        public bool IsFindVisible
        {
            get => _isFindVisible;
            set { _isFindVisible = value; OnPropertyChanged(); }
        }

        private bool _hasEverPreviewedAFile;
        public bool HasEverPreviewedAFile
        {
            get => _hasEverPreviewedAFile;
            set { _hasEverPreviewedAFile = value; OnPropertyChanged(); }
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

        public void PrepareSlotForFile(FileSystemNodeModel node)
        {
            if (node == null) return;

            // Step 1: Always hide status panels when a real file is about to be shown
            IsWelcomeVisible = false;
            IsUnsupportedVisible = false;
            HasEverPreviewedAFile = true;

            // Step 2: Determine category
            bool isImage = SupportedFileTypes.Images.Contains(node.Extension) || 
                           SupportedFileTypes.Textures.Contains(node.Extension) || 
                           SupportedFileTypes.VectorImages.Contains(node.Extension);

            if (isImage)
            {
                IsImageVisible = true;
            }
            else
            {
                IsContentVisible = true;
            }
        }

        public void ClosePanelByCategory(FileSystemNodeModel node)
        {
            if (node == null) return;

            bool isImage = SupportedFileTypes.Images.Contains(node.Extension) || 
                           SupportedFileTypes.Textures.Contains(node.Extension) || 
                           SupportedFileTypes.VectorImages.Contains(node.Extension);

            if (isImage)
            {
                IsImageVisible = false;
            }
            else
            {
                IsContentVisible = false;
                IsTextVisible = false;
                IsWebVisible = false;
                IsUnsupportedVisible = false;
            }

            // If absolutely nothing is visible after closing, we show Welcome again
            if (!IsImageVisible && !IsContentVisible)
            {
                HasEverPreviewedAFile = false; // Reset state to allow Welcome message
                IsWelcomeVisible = true;
            }
        }

        public void ResetAllVisibility()
        {
            IsWelcomeVisible = true;
            IsUnsupportedVisible = false;
            IsImageVisible = false;
            IsContentVisible = false;
            IsTextVisible = false;
            IsWebVisible = false;
            IsFindVisible = false;
            HasEverPreviewedAFile = false;
        }

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}