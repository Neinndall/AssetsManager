using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AssetsManager.Views.Models.Viewer
{
    /// <summary>
    /// Audit item for textures referenced by an effect system.
    /// </summary>
    public class VfxTextureDiagnosticItem : INotifyPropertyChanged
    {
        private string _authoredPath;
        private string _resolvedPath;
        private string _status;
        private Brush _statusBrush;
        private int _width;
        private int _height;
        private BitmapSource _imagePreview;
        private string _texDiv;

        public string AuthoredPath
        {
            get => _authoredPath;
            set { _authoredPath = value; OnPropertyChanged(); }
        }

        public string ResolvedPath
        {
            get => _resolvedPath;
            set { _resolvedPath = value; OnPropertyChanged(); }
        }

        public string Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(); }
        }

        public Brush StatusBrush
        {
            get => _statusBrush;
            set { _statusBrush = value; OnPropertyChanged(); }
        }

        public int Width
        {
            get => _width;
            set { _width = value; OnPropertyChanged(); }
        }

        public int Height
        {
            get => _height;
            set { _height = value; OnPropertyChanged(); }
        }

        public BitmapSource ImagePreview
        {
            get => _imagePreview;
            set { _imagePreview = value; OnPropertyChanged(); }
        }

        public string TexDiv
        {
            get => _texDiv;
            set { _texDiv = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string prop = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}
