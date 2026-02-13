using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AssetsManager.Views.Models.Explorer
{
    public class ExplorerToolbarModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private bool _isWadMode = true;
        private bool _isGridMode = false;
        private bool _isBreadcrumbVisible = true;
        private bool _isFavoritesEnabled = false;
        private string _searchText = string.Empty;
        private bool _isSortButtonVisible = false;

        public bool IsWadMode
        {
            get => _isWadMode;
            set { if (_isWadMode != value) { _isWadMode = value; OnPropertyChanged(); } }
        }

        public bool IsGridMode
        {
            get => _isGridMode;
            set { if (_isGridMode != value) { _isGridMode = value; OnPropertyChanged(); } }
        }

        public bool IsBreadcrumbVisible
        {
            get => _isBreadcrumbVisible;
            set { if (_isBreadcrumbVisible != value) { _isBreadcrumbVisible = value; OnPropertyChanged(); } }
        }

        public bool IsFavoritesEnabled
        {
            get => _isFavoritesEnabled;
            set { if (_isFavoritesEnabled != value) { _isFavoritesEnabled = value; OnPropertyChanged(); } }
        }

        public string SearchText
        {
            get => _searchText;
            set { if (_searchText != value) { _searchText = value; OnPropertyChanged(); } }
        }

        public bool IsSortButtonVisible
        {
            get => _isSortButtonVisible;
            set { if (_isSortButtonVisible != value) { _isSortButtonVisible = value; OnPropertyChanged(); } }
        }

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
