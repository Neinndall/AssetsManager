using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using AssetsManager.Utils.Framework;

namespace AssetsManager.Views.Models.Monitor
{
    /// <summary>
    /// MAIN MODEL: State of the File Watcher Control
    /// </summary>
    public class FileWatcherModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private ObservableRangeCollection<MonitoredUrl> _monitoredUrls;
        private bool _isBusy;

        public FileWatcherModel()
        {
            MonitoredUrls = new ObservableRangeCollection<MonitoredUrl>();
        }

        public ObservableRangeCollection<MonitoredUrl> MonitoredUrls
        {
            get => _monitoredUrls;
            set { _monitoredUrls = value; OnPropertyChanged(); }
        }

        public bool IsBusy
        {
            get => _isBusy;
            set { if (_isBusy != value) { _isBusy = value; OnPropertyChanged(); } }
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// SUB-MODEL: Individual URL being monitored
    /// </summary>
    public class MonitoredUrl : INotifyPropertyChanged
    {
        private string _alias;
        private string _url;
        private string _statusText;
        private Brush _statusColor;
        private string _lastChecked;
        private bool _hasChanges;

        public string Alias
        {
            get => _alias;
            set { if (_alias != value) { _alias = value; OnPropertyChanged(); } }
        }

        public string Url
        {
            get => _url;
            set { if (_url != value) { _url = value; OnPropertyChanged(); } }
        }

        public string StatusText
        {
            get => _statusText;
            set 
            { 
                if (_statusText != value) 
                { 
                    _statusText = value; 
                    OnPropertyChanged(); 
                    OnPropertyChanged(nameof(CombinedStatusAndDate)); 
                } 
            }
        }

        public Brush StatusColor
        {
            get => _statusColor;
            set { if (_statusColor != value) { _statusColor = value; OnPropertyChanged(); } }
        }

        public string LastChecked
        {
            get => _lastChecked;
            set 
            { 
                if (_lastChecked != value) 
                { 
                    _lastChecked = value; 
                    OnPropertyChanged(); 
                    OnPropertyChanged(nameof(CombinedStatusAndDate)); 
                } 
            }
        }

        public string CombinedStatusAndDate => $"Status: {StatusText} | {LastChecked}";

        public bool HasChanges
        {
            get => _hasChanges;
            set { if (_hasChanges != value) { _hasChanges = value; OnPropertyChanged(); } }
        }

        public string OldFilePath { get; set; }
        public string NewFilePath { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
