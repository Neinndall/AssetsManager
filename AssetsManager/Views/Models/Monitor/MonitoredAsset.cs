using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace AssetsManager.Views.Models.Monitor
{
    public enum AssetSourceType
    {
        Game,
        Plugins
    }

    public enum AssetStatus
    {
        UpToDate,
        Updated,
        Error,
        Pending
    }

    public class MonitoredAsset : INotifyPropertyChanged
    {
        private string _alias;
        private string _assetPath;
        private string _wadName;
        private string _internalPath;
        private AssetSourceType _sourceType;
        private ulong _lastKnownHash;
        private DateTime _lastUpdated;
        private AssetStatus _status;
        private Brush _statusColor;
        private bool _hasChanges;

        public string Alias
        {
            get => _alias;
            set { if (_alias != value) { _alias = value; OnPropertyChanged(); } }
        }

        public string AssetPath
        {
            get => _assetPath;
            set { if (_assetPath != value) { _assetPath = value; OnPropertyChanged(); } }
        }

        public string WadName
        {
            get => _wadName;
            set { if (_wadName != value) { _wadName = value; OnPropertyChanged(); } }
        }

        public string InternalPath
        {
            get => _internalPath;
            set { if (_internalPath != value) { _internalPath = value; OnPropertyChanged(); } }
        }

        public AssetSourceType SourceType
        {
            get => _sourceType;
            set { if (_sourceType != value) { _sourceType = value; OnPropertyChanged(); } }
        }

        public ulong LastKnownHash
        {
            get => _lastKnownHash;
            set { if (_lastKnownHash != value) { _lastKnownHash = value; OnPropertyChanged(); } }
        }

        public DateTime LastUpdated
        {
            get => _lastUpdated;
            set { if (_lastUpdated != value) { _lastUpdated = value; OnPropertyChanged(); } }
        }

        public AssetStatus Status
        {
            get => _status;
            set { if (_status != value) { _status = value; OnPropertyChanged(); UpdateStatusInfo(); } }
        }

        public Brush StatusColor
        {
            get => _statusColor;
            set { if (_statusColor != value) { _statusColor = value; OnPropertyChanged(); } }
        }

        public bool HasChanges
        {
            get => _hasChanges;
            set { if (_hasChanges != value) { _hasChanges = value; OnPropertyChanged(); } }
        }

        public string LastCheckedInfo => $"Status: {GetStatusText()} | Last Update: {(_lastUpdated == DateTime.MinValue ? "N/A" : _lastUpdated.ToString("yyyy-MMM-dd HH:mm"))}";

        private string GetStatusText()
        {
            return Status switch
            {
                AssetStatus.UpToDate => "Up-to-date",
                AssetStatus.Updated => "Updated",
                AssetStatus.Error => "Error",
                AssetStatus.Pending => "Pending check",
                _ => "Unknown"
            };
        }

        private void UpdateStatusInfo()
        {
            OnPropertyChanged(nameof(LastCheckedInfo));
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
