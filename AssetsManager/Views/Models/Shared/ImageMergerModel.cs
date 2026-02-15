using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Explorer;
using AssetsManager.Utils.Framework;

namespace AssetsManager.Views.Models.Shared
{
    /// <summary>
    /// Representa una imagen individual dentro de la lista del mezclador.
    /// </summary>
    public class ImageMergerItem : INotifyPropertyChanged
    {
        private string _name;
        private BitmapSource _image;
        private string _path;

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); }
        }

        public BitmapSource Image
        {
            get => _image;
            set { _image = value; OnPropertyChanged(); }
        }

        public string Path
        {
            get => _path;
            set { _path = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// ViewModel principal para el Image Merger. Gestiona el estado de la herramienta y la lista de imágenes.
    /// </summary>
    public class ImageMergerModel : INotifyPropertyChanged
    {
        private int _columns = 4;
        private int _margin = 5;
        private double _zoom = 1.0;
        private BitmapSource _previewImage;
        private bool _isProcessing;

        // La colección se pasa ahora por el constructor
        public ObservableRangeCollection<ImageMergerItem> Items { get; }

        public ImageMergerModel(ObservableRangeCollection<ImageMergerItem> items)
        {
            Items = items;
        }

        public int Columns
        {
            get => _columns;
            set { _columns = value; OnPropertyChanged(); }
        }

        public int Margin
        {
            get => _margin;
            set { _margin = value; OnPropertyChanged(); }
        }

        public double Zoom
        {
            get => _zoom;
            set { _zoom = value; OnPropertyChanged(); }
        }

        public BitmapSource PreviewImage
        {
            get => _previewImage;
            set { _previewImage = value; OnPropertyChanged(); }
        }

        public bool IsProcessing
        {
            get => _isProcessing;
            set { _isProcessing = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
