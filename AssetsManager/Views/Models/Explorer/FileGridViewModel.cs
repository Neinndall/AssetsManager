using System.ComponentModel;
using System.Linq;
using System.Windows.Media;
using AssetsManager.Utils;

namespace AssetsManager.Views.Models.Explorer
{
    public class FileGridViewModel : INotifyPropertyChanged
    {
        public FileSystemNodeModel Node { get; private set; }

        public bool IsFolder => Node.Type == NodeType.VirtualDirectory || Node.Type == NodeType.RealDirectory || Node.Type == NodeType.WadFile || Node.Type == NodeType.SoundBank || Node.Type == NodeType.AudioEvent;

        public string DisplayNameShort => PathUtils.TruncateForDisplay(Node.DisplayName, 50);

        public string SubfolderCount => IsUnloadedSoundBank 
            ? "N/A" 
            : (Node.Children?.Count(c => IsNodeFolder(c) && !c.Name.Equals("Loading...")) ?? 0).ToString();

        public string AssetCount => IsUnloadedSoundBank 
            ? "N/A" 
            : (Node.Children?.Count(c => !IsNodeFolder(c) && !c.Name.Equals("Loading...")) ?? 0).ToString();

        private bool IsUnloadedSoundBank => Node.Type == NodeType.SoundBank && 
                                            Node.Children?.Count == 1 && 
                                            Node.Children[0].Name == "Loading...";

        private static bool IsNodeFolder(FileSystemNodeModel node)
        {
            return node.Type == NodeType.VirtualDirectory || node.Type == NodeType.RealDirectory || node.Type == NodeType.WadFile || node.Type == NodeType.SoundBank || node.Type == NodeType.AudioEvent;
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
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
