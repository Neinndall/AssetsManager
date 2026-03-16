using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AssetsManager.Views.Models.Viewer
{
    /// <summary>
    /// ViewModel for the 3D Viewport Control.
    /// Manages the state of the viewport tools and layout.
    /// </summary>
    public class ViewerViewportModel : INotifyPropertyChanged
    {
        private bool _isFpsVisible = false;
        private bool _isAutoRotateActive = false;
        private bool _isMaximized = false;
        private bool _isToolbarVisible = false;

        public bool IsFpsVisible
        {
            get => _isFpsVisible;
            set { if (_isFpsVisible != value) { _isFpsVisible = value; OnPropertyChanged(); } }
        }

        public bool IsAutoRotateActive
        {
            get => _isAutoRotateActive;
            set { if (_isAutoRotateActive != value) { _isAutoRotateActive = value; OnPropertyChanged(); } }
        }

        public bool IsMaximized
        {
            get => _isMaximized;
            set { if (_isMaximized != value) { _isMaximized = value; OnPropertyChanged(); } }
        }

        public bool IsToolbarVisible
        {
            get => _isToolbarVisible;
            set { if (_isToolbarVisible != value) { _isToolbarVisible = value; OnPropertyChanged(); } }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
