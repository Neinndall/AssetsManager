using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using AssetsManager.Services.Monitor;
using AssetsManager.Services;
using AssetsManager.Info;
using AssetsManager.Utils;

namespace AssetsManager.Views.Models.Monitor
{
    public class MonitorDashboardModel : INotifyPropertyChanged
    {
        private readonly MonitorService _monitorService;
        private readonly PbeStatusService _pbeStatusService;
        private readonly AppSettings _appSettings;
        private readonly VersionService _versionService; // To check Hash/App status if needed

        // --- PBE Status ---
        private string _pbeStatusText = "Unknown";
        public string PbeStatusText
        {
            get => _pbeStatusText;
            set { _pbeStatusText = value; OnPropertyChanged(); }
        }

        private Brush _pbeStatusColor = Brushes.Gray;
        public Brush PbeStatusColor
        {
            get => _pbeStatusColor;
            set { _pbeStatusColor = value; OnPropertyChanged(); }
        }

        private string _pbeLastCheck = "Not checked yet";
        public string PbeLastCheck
        {
            get => _pbeLastCheck;
            set { _pbeLastCheck = value; OnPropertyChanged(); }
        }

        // --- File Watcher ---
        private int _monitoredFilesCount;
        public int MonitoredFilesCount
        {
            get => _monitoredFilesCount;
            set { _monitoredFilesCount = value; OnPropertyChanged(); }
        }

        private int _monitoredFilesChangedCount;
        public int MonitoredFilesChangedCount
        {
            get => _monitoredFilesChangedCount;
            set { _monitoredFilesChangedCount = value; OnPropertyChanged(); }
        }

        private string _watcherLastUpdate = "N/A";
        public string WatcherLastUpdate
        {
            get => _watcherLastUpdate;
            set { _watcherLastUpdate = value; OnPropertyChanged(); }
        }

        // --- Asset Tracker ---
        private string _assetTrackerStatus = "Idle";
        public string AssetTrackerStatus
        {
            get => _assetTrackerStatus;
            set { _assetTrackerStatus = value; OnPropertyChanged(); }
        }

        private int _assetTrackerTotalFound;
        public int AssetTrackerTotalFound
        {
            get => _assetTrackerTotalFound;
            set { _assetTrackerTotalFound = value; OnPropertyChanged(); }
        }

        private int _assetTrackerCategoriesCount;
        public int AssetTrackerCategoriesCount
        {
            get => _assetTrackerCategoriesCount;
            set { _assetTrackerCategoriesCount = value; OnPropertyChanged(); }
        }

        // --- System / Hashes ---
        private string _hashesStatus = "Synced";
        public string HashesStatus
        {
            get => _hashesStatus;
            set { _hashesStatus = value; OnPropertyChanged(); }
        }

        private string _appLastUpdated;
        public string AppLastUpdated
        {
            get => _appLastUpdated;
            set { _appLastUpdated = value; OnPropertyChanged(); }
        }

        public MonitorDashboardModel(MonitorService monitorService, PbeStatusService pbeStatusService, AppSettings appSettings, VersionService versionService)
        {
            _monitorService = monitorService;
            _pbeStatusService = pbeStatusService;
            _appSettings = appSettings;
            _versionService = versionService;

            // Get the last write time of the assembly to represent the "Last Updated" date
            try
            {
                var assemblyLocation = System.Reflection.Assembly.GetExecutingAssembly().Location;
                var lastWriteTime = System.IO.File.GetLastWriteTime(assemblyLocation);
                AppLastUpdated = lastWriteTime.ToString("yyyy-MMM-dd");
            }
            catch
            {
                AppLastUpdated = "Unknown";
            }

            // Initial Loads
            RefreshPbeData();
            RefreshFileWatcherData();
            RefreshAssetTrackerData();

            // Subscriptions
            _monitorService.MonitoredItems.CollectionChanged += MonitoredItems_CollectionChanged;
            
            // Subscribe to PropertyChanged for existing items
            foreach (var item in _monitorService.MonitoredItems)
            {
                item.PropertyChanged += MonitoredItem_PropertyChanged;
            }

            _monitorService.CategoryCheckCompleted += (category) => 
            {
                // Race condition fix: When this event fires, 'category.Status' might still be 'Checking'.
                // We check if ANY OTHER category is checking. If not, we are effectively Idle.
                bool anyOtherChecking = _monitorService.AssetCategories
                    .Where(c => c != category)
                    .Any(c => c.Status == CategoryStatus.Checking);

                if (!anyOtherChecking)
                {
                    AssetTrackerStatus = "Idle";
                }

                RefreshAssetTrackerData();
            };
            _monitorService.CategoryCheckStarted += (category) => AssetTrackerStatus = $"Checking {category.Name}...";

            _pbeStatusService.StatusChecked += () => 
            {
                System.Windows.Application.Current.Dispatcher.Invoke(RefreshPbeData);
            };
        }

        private void MonitoredItems_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (MonitoredUrl item in e.NewItems)
                {
                    item.PropertyChanged += MonitoredItem_PropertyChanged;
                }
            }

            if (e.OldItems != null)
            {
                foreach (MonitoredUrl item in e.OldItems)
                {
                    item.PropertyChanged -= MonitoredItem_PropertyChanged;
                }
            }
            RefreshFileWatcherData();
        }

        private void MonitoredItem_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MonitoredUrl.HasChanges) || e.PropertyName == nameof(MonitoredUrl.LastChecked))
            {
                RefreshFileWatcherData();
            }
        }

        public void RefreshPbeData()
        {
            // Since PbeStatusService stores the msg in AppSettings, we read it.
            string status = _appSettings.LastPbeStatusMessage;
            if (string.IsNullOrEmpty(status))
            {
                PbeStatusText = "No issues detected";
                PbeStatusColor = new SolidColorBrush(Color.FromRgb(46, 204, 113)); // Green
            }
            else
            {
                PbeStatusText = status.Replace("PBE Status: ", "");
                PbeStatusColor = new SolidColorBrush(Color.FromRgb(231, 76, 60)); // Red
            }
            PbeLastCheck = DateTime.Now.ToString("HH:mm"); 
        }

        public void RefreshFileWatcherData()
        {
            if (_monitorService.MonitoredItems == null) return;

            MonitoredFilesCount = _monitorService.MonitoredItems.Count;
            MonitoredFilesChangedCount = _monitorService.MonitoredItems.Count(x => x.HasChanges);
            
            var lastItem = _monitorService.MonitoredItems.OrderByDescending(x => x.LastChecked).FirstOrDefault();
            WatcherLastUpdate = lastItem != null && lastItem.LastChecked != "N/A" 
                ? lastItem.LastChecked.Replace("Last Update: ", "") 
                : "Never";
        }

        public void RefreshAssetTrackerData()
        {
            if (!_monitorService.AssetCategories.Any()) _monitorService.LoadAssetCategories();

            AssetTrackerCategoriesCount = _monitorService.AssetCategories.Count;
            AssetTrackerTotalFound = _monitorService.AssetCategories.Sum(c => c.FoundUrls.Count);
            
            // If all idle
            if (_monitorService.AssetCategories.All(c => c.Status == CategoryStatus.Idle || c.Status == CategoryStatus.CompletedSuccess))
            {
                AssetTrackerStatus = "Idle";
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
