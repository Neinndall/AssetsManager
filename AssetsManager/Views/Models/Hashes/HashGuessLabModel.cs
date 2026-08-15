using System.ComponentModel;
using System.Runtime.CompilerServices;
using AssetsManager.Utils.Framework;

namespace AssetsManager.Views.Models.Hashes
{
    public class HashGuessLabModel : INotifyPropertyChanged
    {
        private bool _isRunning;
        private string _statusText = "Ready to scan local WADs.";
        private double _progressValue;
        private bool _isProgressIndeterminate;
        private string _progressText = "0%";

        private HashMethodItemModel _selectedMethod;
        private string _searchQuery = string.Empty;
        private string _selectedPluginFilter = "All";
        private string _selectedExtensionFilter = "All";

        public ObservableRangeCollection<HashMethodItemModel> AvailableMethods { get; } = new();
        public ObservableRangeCollection<object> Matches { get; } = new();

        public HashMethodItemModel SelectedMethod { get => _selectedMethod; set { _selectedMethod = value; OnPropertyChanged(); } }
        public string SearchQuery { get => _searchQuery; set { _searchQuery = value; OnPropertyChanged(); } }
        public string SelectedPluginFilter { get => _selectedPluginFilter; set { _selectedPluginFilter = value; OnPropertyChanged(); } }
        public string SelectedExtensionFilter { get => _selectedExtensionFilter; set { _selectedExtensionFilter = value; OnPropertyChanged(); } }

        public bool IsRunning { get => _isRunning; set { _isRunning = value; OnPropertyChanged(); } }
        public string StatusText { get => _statusText; set { _statusText = value; OnPropertyChanged(); } }
        public double ProgressValue { get => _progressValue; set { _progressValue = value; OnPropertyChanged(); } }
        public bool IsProgressIndeterminate { get => _isProgressIndeterminate; set { _isProgressIndeterminate = value; OnPropertyChanged(); } }
        public string ProgressText { get => _progressText; set { _progressText = value; OnPropertyChanged(); } }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
