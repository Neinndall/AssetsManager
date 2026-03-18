using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AssetsManager.Views.Models.Viewer
{
    /// <summary>
    /// ViewModel for the Viewer Window (Container).
    /// Manages the overall state of the viewer module.
    /// </summary>
    public class ViewerWindowModel : INotifyPropertyChanged
    {
        private bool _isLoadingVisible = false;
        private string _loadingTitle = "Loading...";
        private string _loadingDescription = "Please wait.";

        public bool IsLoadingVisible
        {
            get => _isLoadingVisible;
            set { if (_isLoadingVisible != value) { _isLoadingVisible = value; OnPropertyChanged(); } }
        }

        public string LoadingTitle
        {
            get => _loadingTitle;
            set { if (_loadingTitle != value) { _loadingTitle = value; OnPropertyChanged(); } }
        }

        public string LoadingDescription
        {
            get => _loadingDescription;
            set { if (_loadingDescription != value) { _loadingDescription = value; OnPropertyChanged(); } }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
