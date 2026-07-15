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

        public ObservableRangeCollection<object> Matches { get; } = new();

        public bool IsRunning { get => _isRunning; set { _isRunning = value; OnPropertyChanged(); } }
        public string StatusText { get => _statusText; set { _statusText = value; OnPropertyChanged(); } }
        public double ProgressValue { get => _progressValue; set { _progressValue = value; OnPropertyChanged(); } }
        public bool IsProgressIndeterminate { get => _isProgressIndeterminate; set { _isProgressIndeterminate = value; OnPropertyChanged(); } }
        public string ProgressText { get => _progressText; set { _progressText = value; OnPropertyChanged(); } }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
