using System.ComponentModel;
using System.Linq;
using System.Windows.Media;

namespace AssetsManager.Views.Models.Explorer
{
    public class FileGridViewModel : INotifyPropertyChanged
    {
        public FileSystemNodeModel Node { get; private set; }

        public bool IsFolder => Node.Type == NodeType.VirtualDirectory || Node.Type == NodeType.RealDirectory || Node.Type == NodeType.WadFile || Node.Type == NodeType.SoundBank || Node.Type == NodeType.AudioEvent;

        public int SubfolderCount => Node.Children?.Count(c => IsNodeFolder(c) && !c.Name.Equals("Loading...")) ?? 0;
        public int AssetCount => Node.Children?.Count(c => !IsNodeFolder(c) && !c.Name.Equals("Loading...")) ?? 0;

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
