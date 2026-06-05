using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using AssetsManager.Utils.Framework;

namespace AssetsManager.Views.Models.Viewer
{
    /// <summary>
    /// Model for an individual skin/chroma entry in the selection gallery.
    /// </summary>
    public class ChromaSkinModel : INotifyPropertyChanged
    {
        private bool _isSelected;
        private string _name;
        private string _texturePath;
        private string _previewTextureName;
        private string _modelPath;
        private Color _swatchColor = Colors.Transparent;
        private ImageSource _previewImage;

        public string Name
        {
            get => _name;
            set { if (_name != value) { _name = value; OnPropertyChanged(); } }
        }

        public string TexturePath
        {
            get => _texturePath;
            set { if (_texturePath != value) { _texturePath = value; OnPropertyChanged(); } }
        }

        public string PreviewTextureName
        {
            get => _previewTextureName;
            set { if (_previewTextureName != value) { _previewTextureName = value; OnPropertyChanged(); } }
        }

        public string ModelPath
        {
            get => _modelPath;
            set { if (_modelPath != value) { _modelPath = value; OnPropertyChanged(); } }
        }

        public string TypeText { get; set; } = "SKIN";

        public Color SwatchColor
        {
            get => _swatchColor;
            set { if (_swatchColor != value) { _swatchColor = value; OnPropertyChanged(); } }
        }

        public ImageSource PreviewImage
        {
            get => _previewImage;
            set { if (_previewImage != value) { _previewImage = value; OnPropertyChanged(); } }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// Master model for the Chroma Selection Gallery. Orchestrates skin detection and selection.
    /// </summary>
    public class ChromaSelectionModel : INotifyPropertyChanged
    {
        private bool _isLoading;
        private string _statusText = "Ready to scan.";
        private ChromaSkinModel _selectedSkin;
        private string _modelPath;

        public ObservableRangeCollection<ChromaSkinModel> AvailableSkins { get; } = new ObservableRangeCollection<ChromaSkinModel>();

        public bool IsLoading
        {
            get => _isLoading;
            set { if (_isLoading != value) { _isLoading = value; OnPropertyChanged(); } }
        }

        public string StatusText
        {
            get => _statusText;
            private set { if (_statusText != value) { _statusText = value; OnPropertyChanged(); } }
        }

        public ChromaSkinModel SelectedSkin
        {
            get => _selectedSkin;
            set
            {
                if (_selectedSkin != value)
                {
                    _selectedSkin = value;
                    OnPropertyChanged();
                }
            }
        }

        public string ModelPath
        {
            get => _modelPath;
            set { if (_modelPath != value) { _modelPath = value; OnPropertyChanged(); } }
        }

        // --- State Management Methods (v3.2.2.0) ---

        public void SetScanningState(string folderName)
        {
            IsLoading = true;
            AvailableSkins.Clear();
            StatusText = $"Scanning chromas in: {folderName.ToUpper()}";
        }

        public void SetEmptyState()
        {
            IsLoading = false;
            StatusText = "No chromas or textures found in this directory.";
        }

        public void SetSuccessState(int count)
        {
            IsLoading = false;
            StatusText = $"Found {count} available chromas.";
        }

        public void SetErrorState(string message)
        {
            IsLoading = false;
            StatusText = $"Error: {message}";
        }

        public void Reset()
        {
            AvailableSkins.Clear();
            SelectedSkin = null;
            ModelPath = null;
            StatusText = "Ready to scan.";
            IsLoading = false;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
