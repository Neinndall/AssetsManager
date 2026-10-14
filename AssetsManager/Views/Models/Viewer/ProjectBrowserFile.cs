using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using AssetsManager.Views.Helpers;
using Material.Icons;

namespace AssetsManager.Views.Models.Viewer
{
    public sealed class ProjectBrowserFile(string fullPath, string relativePath, bool isDirectory) : INotifyPropertyChanged, IMultiSelectable
    {
        public string FullPath { get; } = fullPath;
        public string RelativePath { get; } = relativePath;
        public bool IsDirectory { get; } = isDirectory;
        public string Name => System.IO.Path.GetFileName(FullPath);
        public MaterialIconKind IconKind => IsDirectory ? MaterialIconKind.FolderOutline :
            Utils.SupportedFileTypes.IsImage(FullPath) ? MaterialIconKind.ImageOutline :
            System.IO.Path.GetExtension(FullPath).Equals(".anm", System.StringComparison.OrdinalIgnoreCase) ? MaterialIconKind.AnimationPlay :
            Utils.SupportedFileTypes.Is3D(FullPath) ? MaterialIconKind.CubeOutline : MaterialIconKind.FileOutline;
        public string IconColor => IsDirectory ? "#FFC107" : Utils.SupportedFileTypes.IsImage(FullPath) ? "#BA36CC" :
            Utils.SupportedFileTypes.Is3D(FullPath) ? "#00D7A0" : "#6C8AFF";
        private BitmapSource _thumbnail;
        public BitmapSource Thumbnail
        {
            get => _thumbnail;
            internal set { _thumbnail = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasThumbnail)); }
        }
        public bool HasThumbnail => Thumbnail != null;
        internal bool IsThumbnailLoading { get; set; }
        private bool _isSelected, _isMultiSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { if (_isSelected == value) return; _isSelected = value; OnPropertyChanged(); }
        }
        public bool IsMultiSelected
        {
            get => _isMultiSelected;
            set { if (_isMultiSelected == value) return; _isMultiSelected = value; OnPropertyChanged(); }
        }
        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new(name));
    }
}
