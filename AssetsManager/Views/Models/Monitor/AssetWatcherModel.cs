using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using AssetsManager.Utils.Framework;

namespace AssetsManager.Views.Models.Monitor
{
    /// <summary>
    /// MAIN MODEL: State of the Asset Watcher Control
    /// </summary>
    public class AssetWatcherModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private ObservableRangeCollection<MonitoredAsset> _monitoredAssets;
        private bool _isBusy;

        public AssetWatcherModel()
        {
            MonitoredAssets = new ObservableRangeCollection<MonitoredAsset>();
        }

        public ObservableRangeCollection<MonitoredAsset> MonitoredAssets
        {
            get => _monitoredAssets;
            set { _monitoredAssets = value; OnPropertyChanged(); }
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
}
