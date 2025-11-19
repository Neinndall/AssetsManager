using System.ComponentModel;
using System.Windows.Media;

namespace AssetsManager.Views.Models.Monitor
{
    public class MonitoredUrl : INotifyPropertyChanged
    {
        private string _alias;
        public string Alias
        {
            get => _alias;
            set
            {
                if (_alias != value)
                {
                    _alias = value;
                    OnPropertyChanged(nameof(Alias));
                }
            }
        }

        private string _url;
        public string Url
        {
            get => _url;
            set
            {
                if (_url != value)
                {
                    _url = value;
                    OnPropertyChanged(nameof(Url));
                }
            }
        }

        private string _statusText;
        public string StatusText
        {
            get => _statusText;
            set
            {
                if (_statusText != value)
                {
                    _statusText = value;
                    OnPropertyChanged(nameof(StatusText));
                    OnPropertyChanged(nameof(CombinedStatusAndDate)); // Notify CombinedStatusAndDate
                }
            }
        }

        private Brush _statusColor;
        public Brush StatusColor
        {
            get => _statusColor;
            set
            {
                if (_statusColor != value)
                {
                    _statusColor = value;
                    OnPropertyChanged(nameof(StatusColor));
                }
            }
        }

        private string _lastChecked;
        public string LastChecked
        {
            get => _lastChecked;
            set
            {
                if (_lastChecked != value)
                {
                    _lastChecked = value;
                    OnPropertyChanged(nameof(LastChecked));
                    OnPropertyChanged(nameof(CombinedStatusAndDate)); // Notify CombinedStatusAndDate
                }
            }
        }

        // New property to combine StatusText and LastChecked for display
        public string CombinedStatusAndDate => $"Status: {StatusText} | {LastChecked}";

        private bool _hasChanges;
        public bool HasChanges
        {
            get => _hasChanges;
            set
            {
                if (_hasChanges != value)
                {
                    _hasChanges = value;
                    OnPropertyChanged(nameof(HasChanges));
                }
            }
        }

        public string OldFilePath { get; set; }
        public string NewFilePath { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
