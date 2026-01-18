using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AssetsManager.Views.Models.Explorer
{
    public class FileExplorerModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private bool _isBusy;
        private bool _isTreeReady;
        private bool _isEmptyState;
        private bool _isFavoritesEnabled = true; // Default toggle state
        private ObservableCollection<FileSystemNodeModel> _rootNodes;

        public FileExplorerModel()
        {
            RootNodes = new ObservableCollection<FileSystemNodeModel>();
            IsBusy = false;
            IsTreeReady = false;
            IsEmptyState = true; // Start empty
        }

        public ObservableCollection<FileSystemNodeModel> RootNodes
        {
            get => _rootNodes;
            set { _rootNodes = value; OnPropertyChanged(); }
        }

        public bool IsBusy
        {
            get => _isBusy;
            set { if (_isBusy != value) { _isBusy = value; OnPropertyChanged(); } }
        }

        public bool IsTreeReady
        {
            get => _isTreeReady;
            set 
            { 
                if (_isTreeReady != value) 
                { 
                    _isTreeReady = value; 
                    OnPropertyChanged(); 
                    OnPropertyChanged(nameof(AreFavoritesVisible));
                } 
            }
        }

        public bool IsEmptyState
        {
            get => _isEmptyState;
            set { if (_isEmptyState != value) { _isEmptyState = value; OnPropertyChanged(); } }
        }

        public bool IsFavoritesEnabled
        {
            get => _isFavoritesEnabled;
            set
            {
                if (_isFavoritesEnabled != value)
                {
                    _isFavoritesEnabled = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(AreFavoritesVisible));
                }
            }
        }

        // Computed property for visibility
        public bool AreFavoritesVisible => IsTreeReady && IsFavoritesEnabled;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
