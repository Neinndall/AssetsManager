using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using AssetsManager.Utils;
using AssetsManager.Utils.Framework;

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
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsActionBarVisible));
                    OnPropertyChanged(nameof(SelectedCountDisplay));
                }
            }
        }

        public bool IsActionBarVisible => SelectedCount > 1;
        public string SelectedCountDisplay => $"{SelectedCount} items selected";

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
    public class FileGridViewModel : INotifyPropertyChanged
    {
        public FileSystemNodeModel Node { get; private set; }

        public bool IsFolder => Node.Type == NodeType.VirtualDirectory || Node.Type == NodeType.RealDirectory || Node.Type == NodeType.WadFile || Node.Type == NodeType.SoundBank || Node.Type == NodeType.AudioEvent;

        public string FileExtensionDisplay => IsFolder ? "DIR" : (string.IsNullOrEmpty(Node.Extension) ? "FILE" : Node.Extension.TrimStart('.').ToUpper());

        public string DisplayNameShort => PathUtils.TruncateForDisplay(Node.DisplayName, 50);

        private string _subfolderCount;
        public string SubfolderCount => _subfolderCount ?? (_subfolderCount = IsUnloadedSoundBank ? "N/A" : (Node.Children?.Count(c => IsNodeFolder(c) && !c.Name.Equals("Loading...")) ?? 0).ToString());

        private string _folderCount;
        public string FolderCount => _folderCount ?? (_folderCount = IsUnloadedSoundBank ? "0" : (Node.Children?.Count(c => IsNodeFolder(c) && !c.Name.Equals("Loading...")) ?? 0).ToString());

        private string _assetCount;
        public string AssetCount => _assetCount ?? (_assetCount = IsUnloadedSoundBank ? "N/A" : (Node.Children?.Count(c => !IsNodeFolder(c) && !c.Name.Equals("Loading...")) ?? 0).ToString());

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

        private ImageSource _imagePreview;
        public ImageSource ImagePreview
        {
            get { return _imagePreview; }
            set
            {
                if (_imagePreview != value)
                {
                    _imagePreview = value;
                    OnPropertyChanged(nameof(ImagePreview));
                }
            }
        }

        public FileGridViewModel(FileSystemNodeModel node)
        {
            Node = node;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
