using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using AssetsManager.Utils;
using AssetsManager.Utils.Framework;
using AssetsManager.Views.Helpers;

namespace AssetsManager.Views.Models.Explorer
{
    /// <summary>
    /// Model for the Grid Control State (Data/Info)
    /// </summary>
    public class FileGridModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private int _selectedCount;
        public int SelectedCount
        {
            get => _selectedCount;
            set
            {
                if (_selectedCount != value)
                {
                    _selectedCount = value;
                    _selectedCountDisplay = null;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsActionBarVisible));
                    OnPropertyChanged(nameof(SelectedCountDisplay));
                }
            }
        }

        public bool IsActionBarVisible => SelectedCount > 1;
        private string _selectedCountDisplay;
        public string SelectedCountDisplay => _selectedCountDisplay ??= $"{SelectedCount} items selected";

        private string _currentFilter = "All";
        public string CurrentFilter
        {
            get => _currentFilter;
            set { if (_currentFilter != value) { _currentFilter = value; OnPropertyChanged(); } }
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// Model for each individual item in the Grid
    /// </summary>
    public class FileGridViewModel : INotifyPropertyChanged, IMultiSelectable, IDisposable
    {
        public FileSystemNodeModel Node { get; private set; }

        public bool IsFolder => Node.Type == NodeType.VirtualDirectory || Node.Type == NodeType.RealDirectory || Node.Type == NodeType.WadFile || Node.Type == NodeType.SoundBank || Node.Type == NodeType.AudioEvent;

        public string FileExtensionDisplay => IsFolder ? "DIR" : (string.IsNullOrEmpty(Node.Extension) ? "FILE" : Node.Extension.TrimStart('.').ToUpperInvariant());

        public string DisplayNameShort => PathUtils.TruncateForDisplay(Node.DisplayName, 50);

        public string SubfolderCount => IsUnloadedSoundBank ? "N/A" : (Node.Children?.Count(c => IsNodeFolder(c) && !c.Name.Equals("Loading...")) ?? 0).ToString();

        public string FolderCount => IsUnloadedSoundBank ? "0" : (Node.Children?.Count(c => IsNodeFolder(c) && !c.Name.Equals("Loading...")) ?? 0).ToString();

        public string AssetCount => IsUnloadedSoundBank ? "N/A" : (Node.Children?.Count(c => !IsNodeFolder(c) && !c.Name.Equals("Loading...")) ?? 0).ToString();

        private bool IsUnloadedSoundBank => Node.Type == NodeType.SoundBank && 
                                            Node.Children?.Count == 1 && 
                                            Node.Children[0].Name == "Loading...";

        private static bool IsNodeFolder(FileSystemNodeModel node)
        {
            return node.Type == NodeType.VirtualDirectory || node.Type == NodeType.RealDirectory || node.Type == NodeType.WadFile || node.Type == NodeType.SoundBank || node.Type == NodeType.AudioEvent;
        }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); } }
        }

        public bool IsMultiSelected
        {
            get => Node.IsMultiSelected;
            set
            {
                if (Node.IsMultiSelected != value)
                {
                    Node.IsMultiSelected = value;
                    OnPropertyChanged(nameof(IsMultiSelected));
                }
            }
        }

        private readonly Func<FileSystemNodeModel, CancellationToken, Task<ImageSource>> _imageLoader;
        private readonly Action<Exception> _logError;
        private readonly CancellationToken _cancellationToken;
        private bool _isImageLoading = false;

        private ImageSource _imagePreview;
        public ImageSource ImagePreview
        {
            get
            {
                if (_imagePreview == null && !_isImageLoading && _imageLoader != null)
                {
                    _isImageLoading = true;
                    _ = LoadPreviewAsync(_cancellationToken);
                }
                return _imagePreview;
            }
            set
            {
                if (_imagePreview != value)
                {
                    _imagePreview = value;
                    OnPropertyChanged(nameof(ImagePreview));
                }
            }
        }

        private async Task LoadPreviewAsync(CancellationToken cancellationToken)
        {
            try
            {
                var image = await _imageLoader(Node, cancellationToken);
                if (!cancellationToken.IsCancellationRequested && image != null)
                {
                    ImagePreview = image;
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when the grid is refreshed or unloaded.
            }
            catch (Exception ex)
            {
                _logError?.Invoke(ex);
            }
        }

        public FileGridViewModel(FileSystemNodeModel node, Func<FileSystemNodeModel, CancellationToken, Task<ImageSource>> imageLoader = null, CancellationToken cancellationToken = default, Action<Exception> logError = null)
        {
            Node = node;
            _imageLoader = imageLoader;
            _cancellationToken = cancellationToken;
            _logError = logError;

            if (Node != null)
            {
                PropertyChangedEventManager.AddHandler(Node, Node_PropertyChanged, nameof(FileSystemNodeModel.IsMultiSelected));
            }
        }

        private void Node_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(FileSystemNodeModel.IsMultiSelected))
            {
                OnPropertyChanged(nameof(IsMultiSelected));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public void Dispose()
        {
            if (Node != null)
            {
                PropertyChangedEventManager.RemoveHandler(Node, Node_PropertyChanged, nameof(FileSystemNodeModel.IsMultiSelected));
            }

            _imagePreview = null;
            PropertyChanged = null;
        }
    }
}
