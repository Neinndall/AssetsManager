using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AssetsManager.Views.Models.Shared;

namespace AssetsManager.Views.Models.Monitor
{
    /// <summary>
    /// MAIN MODEL: State of the Asset Watcher Control
    /// </summary>
    public class AssetWatcherModel : INotifyPropertyChanged
    {
        public PaginationModel<MonitoredAsset> Paginator { get; }
        private bool _isBusy;

        public event PropertyChangedEventHandler PropertyChanged;

        public AssetWatcherModel()
        {
            Paginator = new PaginationModel<MonitoredAsset> { PageSize = 5 };
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
