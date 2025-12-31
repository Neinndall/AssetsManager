using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AssetsManager.Views.Models.Help
{
    public class UpdatesModel : INotifyPropertyChanged
    {
        private string _currentVersion;
        public string CurrentVersion
        {
            get => _currentVersion;
            set { _currentVersion = value; OnPropertyChanged(); }
        }

        private string _availableVersion;
        public string AvailableVersion
        {
            get => _availableVersion;
            set { _availableVersion = value; OnPropertyChanged(); }
        }

        private bool _isUpdateAvailable;
        public bool IsUpdateAvailable
        {
            get => _isUpdateAvailable;
            set { _isUpdateAvailable = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
