using AssetsManager.Utils;
using AssetsManager.Views.Models.Wad;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace AssetsManager.Views.Models.Explorer
{
    public enum PreviewState
    {
        Empty,
        Loading,
        Image,
        Text,
        Media,
        Unsupported,
        Error
    }

    public class FilePreviewerModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private bool _isGridMode;
        private bool _isBreadcrumbToggleOn = true;
        private PinnedFilesManager _pinnedFilesManager;
        private FileGridModel _gridModel;

        private bool _hasSelectedNode;
        private bool _isSelectedNodeContainer;

        private SerializableChunkDiff _renamedDiffDetails;
        private bool _isRenamedInfoVisible;
        private bool _isRenamedInfoVisibleComputed;
        public SerializableChunkDiff RenamedDiffDetails
        {
            get => _renamedDiffDetails;
            set 
            { 
                _renamedDiffDetails = value; 
                _isRenamedInfoVisibleComputed = false;
                OnPropertyChanged(); 
                OnPropertyChanged(nameof(IsRenamedInfoVisible));
            }
        }

        private NarrativeMetadata _narrativeMetadata;
        public NarrativeMetadata NarrativeMetadata
        {
            get => _narrativeMetadata;
            set { _narrativeMetadata = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsNarrativeMetadataVisible)); }
        }

        public bool IsNarrativeMetadataVisible => NarrativeMetadata != null;

        public bool IsRenamedInfoVisible
        {
            get
            {
                if (!_isRenamedInfoVisibleComputed)
                {
                    _isRenamedInfoVisibleComputed = true;
                    _isRenamedInfoVisible = RenamedDiffDetails != null && RenamedDiffDetails.Type == ChunkDiffType.Renamed && !string.IsNullOrEmpty(RenamedDiffDetails.OldPath) && RenamedDiffDetails.OldPath != RenamedDiffDetails.NewPath;
                }
                return _isRenamedInfoVisible;
            }
        }

        public bool AreTabsVisible => PinnedFilesManager.PinnedFiles.Count > 0;

        public FilePreviewerModel()
        {
            PinnedFilesManager = new PinnedFilesManager();
            _gridModel = new FileGridModel();
            PinnedFilesManager.PinnedFiles.CollectionChanged += (s, e) => OnPropertyChanged(nameof(AreTabsVisible));
        }

        public FileGridModel GridModel
        {
            get => _gridModel;
            set { _gridModel = value; OnPropertyChanged(); }
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

        public bool IsGridVisible => IsGridMode && IsSelectedNodeContainer;
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

        private string _welcomeTitle = "Select a file";
        public string WelcomeTitle
        {
            get => _welcomeTitle;
            set { _welcomeTitle = value; OnPropertyChanged(); }
        }

        private string _welcomeDescription = "Select a file from the explorer to preview its content";
        public string WelcomeDescription
        {
            get => _welcomeDescription;
            set { _welcomeDescription = value; OnPropertyChanged(); }
        }

        private bool _isWelcomeVisible = true;
        public bool IsWelcomeVisible
        {
            get => _isWelcomeVisible;
            set { _isWelcomeVisible = value; OnPropertyChanged(); }
        }

        private PreviewState _contentPreviewState;
        public PreviewState ContentPreviewState
        {
            get => _contentPreviewState;
            private set
            {
                if (_contentPreviewState == value) return;

                _contentPreviewState = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsContentPreviewVisible));
                OnPropertyChanged(nameof(IsContentStatusVisible));
                OnPropertyChanged(nameof(IsDualView));
            }
        }

        private PreviewState _imagePreviewState;
        public PreviewState ImagePreviewState
        {
            get => _imagePreviewState;
            private set
            {
                if (_imagePreviewState == value) return;

                _imagePreviewState = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsImagePreviewVisible));
                OnPropertyChanged(nameof(IsImageStatusVisible));
                OnPropertyChanged(nameof(IsDualView));
            }
        }

        private string _contentPreviewTitle;
        public string ContentPreviewTitle
        {
            get => _contentPreviewTitle;
            private set { _contentPreviewTitle = value; OnPropertyChanged(); }
        }

        private string _contentPreviewMessage;
        public string ContentPreviewMessage
        {
            get => _contentPreviewMessage;
            private set { _contentPreviewMessage = value; OnPropertyChanged(); }
        }

        private string _imagePreviewTitle;
        public string ImagePreviewTitle
        {
            get => _imagePreviewTitle;
            private set { _imagePreviewTitle = value; OnPropertyChanged(); }
        }

        private string _imagePreviewMessage;
        public string ImagePreviewMessage
        {
            get => _imagePreviewMessage;
            private set { _imagePreviewMessage = value; OnPropertyChanged(); }
        }

        public bool IsContentPreviewVisible => ContentPreviewState != PreviewState.Empty;
        public bool IsImagePreviewVisible => ImagePreviewState != PreviewState.Empty;
        public bool IsContentStatusVisible => IsStatus(ContentPreviewState);
        public bool IsImageStatusVisible => IsStatus(ImagePreviewState);
        public bool IsDualView => IsContentPreviewVisible && IsImagePreviewVisible;

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

        public void UnloadSlotByCategory(bool isImage, bool hasMoreOfSameCategory)
        {
            if (!hasMoreOfSameCategory)
            {
                if (isImage)
                {
                    ClearImagePreview();
                }
                else
                {
                    ClearContentPreview();
                }
            }

            if (!IsImagePreviewVisible && !IsContentPreviewVisible && PinnedFilesManager.PinnedFiles.Count <= 1)
            {
                HasEverPreviewedAFile = false;
                IsWelcomeVisible = true;
            }
        }

        public void ResetAllVisibility()
        {
            IsWelcomeVisible = true;
            ClearContentPreview();
            ClearImagePreview();
            IsFindVisible = false;
            HasEverPreviewedAFile = false;
        }

        public void BeginContentLoading(bool showPlaceholderWhenEmpty)
        {
            if (showPlaceholderWhenEmpty &&
                (ContentPreviewState == PreviewState.Empty || IsContentStatusVisible))
            {
                ContentPreviewState = PreviewState.Loading;
            }
        }

        public void ShowContentLoading()
        {
            ContentPreviewState = PreviewState.Loading;
        }

        public void BeginImageLoading()
        {
            if (ImagePreviewState != PreviewState.Empty)
            {
                ImagePreviewTitle = null;
                ImagePreviewMessage = null;
                ImagePreviewState = PreviewState.Loading;
            }
        }

        public void ShowContentPreview(PreviewState state)
        {
            if (state != PreviewState.Text && state != PreviewState.Media)
            {
                throw new System.ArgumentOutOfRangeException(nameof(state));
            }

            ContentPreviewTitle = null;
            ContentPreviewMessage = null;
            ContentPreviewState = state;
        }

        public void ShowImagePreview()
        {
            ImagePreviewTitle = null;
            ImagePreviewMessage = null;
            ImagePreviewState = PreviewState.Image;
        }

        public void ShowContentUnsupported(string extension)
        {
            SetUnsupportedStatus(extension, false);
            ContentPreviewState = PreviewState.Unsupported;
        }

        public void ShowImageUnsupported(string extension)
        {
            SetUnsupportedStatus(extension, true);
            ImagePreviewState = PreviewState.Unsupported;
        }

        public void ShowContentError(string extension)
        {
            ContentPreviewTitle = "Preview error";
            ContentPreviewMessage = GetPreviewErrorMessage(extension);
            ContentPreviewState = PreviewState.Error;
        }

        public void ShowImageError(string extension)
        {
            ImagePreviewTitle = "Preview error";
            ImagePreviewMessage = GetPreviewErrorMessage(extension);
            ImagePreviewState = PreviewState.Error;
        }

        public void ClearContentPreview()
        {
            ContentPreviewTitle = null;
            ContentPreviewMessage = null;
            ContentPreviewState = PreviewState.Empty;
        }

        public void ClearImagePreview()
        {
            ImagePreviewTitle = null;
            ImagePreviewMessage = null;
            ImagePreviewState = PreviewState.Empty;
        }

        private void SetUnsupportedStatus(string extension, bool isImage)
        {
            bool hasExtension = !string.IsNullOrWhiteSpace(extension) && extension != ".";
            string title = hasExtension ? "Format not supported" : "Preview not available";
            string message = hasExtension
                ? $"The {extension} format is not supported to preview it"
                : "This file format is not supported to preview it";

            if (isImage)
            {
                ImagePreviewTitle = title;
                ImagePreviewMessage = message;
            }
            else
            {
                ContentPreviewTitle = title;
                ContentPreviewMessage = message;
            }
        }

        private static string GetPreviewErrorMessage(string extension)
        {
            return string.IsNullOrWhiteSpace(extension) || extension == "."
                ? "The file could not be loaded for preview."
                : $"The {extension} file could not be loaded for preview.";
        }

        private static bool IsStatus(PreviewState state)
        {
            return state == PreviewState.Unsupported || state == PreviewState.Error;
        }

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
