using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AssetsManager.Views.Models
{
    public enum CategoryStatus { Idle, Checking, CompletedSuccess }

    public class AssetCategory : INotifyPropertyChanged
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string BaseUrl { get; set; }
        public string Extension { get; set; }
        public long Start { get; set; }
        public long LastValid { get; set; }
        public List<long> FoundUrls { get; set; } = new List<long>();
        public List<long> FailedUrls { get; set; } = new List<long>();
        public List<long> UserRemovedUrls { get; set; } = new List<long>();
        public Dictionary<long, string> FoundUrlOverrides { get; set; } = new Dictionary<long, string>();

        private bool _hasNewAssets;
        public bool HasNewAssets
        {
            get => _hasNewAssets;
            set
            {
                if (_hasNewAssets == value) return;
                _hasNewAssets = value;
                OnPropertyChanged();
            }
        }

        private CategoryStatus _status;
        public CategoryStatus Status
        {
            get => _status;
            set
            {
                if (_status == value) return;
                _status = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
