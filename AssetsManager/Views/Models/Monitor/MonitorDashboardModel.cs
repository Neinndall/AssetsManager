using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Collections.Specialized;
using AssetsManager.Info;
using AssetsManager.Services;
using AssetsManager.Services.Downloads;
using AssetsManager.Services.Monitor;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Monitor;
using Material.Icons;

namespace AssetsManager.Views.Models.Monitor
{
    public class MonitorDashboardModel : INotifyPropertyChanged, IDisposable
    {
        private readonly MonitorService _monitorService;
        private readonly PbeStatusService _pbeStatusService;
        private readonly AppSettings _appSettings;
        private readonly VersionService _versionService;
        private readonly Status _statusService;
        private readonly UpdateCheckService _updateCheckService;
        private bool _isDisposed;

        // --- PBE Status ---
        private string _pbeStatusText = "Unknown";
        public string PbeStatusText
        {
            get => _pbeStatusText;
            set { _pbeStatusText = value; OnPropertyChanged(); }
        }

        private string _pbeStatusSubtitle = "Checking connection...";
        public string PbeStatusSubtitle
        {
            get => _pbeStatusSubtitle;
            set { _pbeStatusSubtitle = value; OnPropertyChanged(); }
        }

        private Brush _pbeStatusColor = Brushes.Gray;
        public Brush PbeStatusColor
        {
            get => _pbeStatusColor;
            set { _pbeStatusColor = value; OnPropertyChanged(); }
        }

        private MaterialIconKind _pbeStatusIconKind = MaterialIconKind.ServerNetwork;
        public MaterialIconKind PbeStatusIconKind
        {
            get => _pbeStatusIconKind;
            set { _pbeStatusIconKind = value; OnPropertyChanged(); }
        }

        private string _pbeLastCheck = "Not checked yet";
        public string PbeLastCheck
        {
            get => _pbeLastCheck;
            set { _pbeLastCheck = value; OnPropertyChanged(); }
        }

        // --- Asset Watcher ---
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

        private string _watcherStatusSubtitle = "Surveillance active · Local client synced";
        public string WatcherStatusSubtitle
        {
            get => _watcherStatusSubtitle;
            set { _watcherStatusSubtitle = value; OnPropertyChanged(); }
        }

        private string _lastChangedFileName = "None";
        public string LastChangedFileName
        {
            get => _lastChangedFileName;
            set { _lastChangedFileName = value; OnPropertyChanged(); }
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

        private string _lastDiscoveredCategory = "None";
        public string LastDiscoveredCategory
        {
            get => _lastDiscoveredCategory;
            set { _lastDiscoveredCategory = value; OnPropertyChanged(); }
        }

        private string _lastDiscoveredAssetId = "N/A";
        public string LastDiscoveredAssetId
        {
            get => _lastDiscoveredAssetId;
            set { _lastDiscoveredAssetId = value; OnPropertyChanged(); }
        }

        // --- System / Hashes ---
        private string _hashesStatus = "Synced";
        public string HashesStatus
        {
            get => _hashesStatus;
            set 
            { 
                _hashesStatus = value; 
                OnPropertyChanged();
                UpdateHashesIndicatorColor();
            }
        }

        private Brush _hashesIndicatorColor = Brushes.Gray;
        public Brush HashesIndicatorColor
        {
            get => _hashesIndicatorColor;
            set { _hashesIndicatorColor = value; OnPropertyChanged(); }
        }

        private string _appVersionText;
        public string AppVersionText
        {
            get => _appVersionText;
            set { _appVersionText = value; OnPropertyChanged(); }
        }

        public string BuildType => ApplicationInfos.BuildType;
        public string BuildChannel => ApplicationInfos.IsQA ? "QA / EXPERIMENTAL" : "PRODUCTION / STABLE";
        public string BuildSha => ApplicationInfos.IsQA ? ApplicationInfos.Version.Split('-').Last() : "N/A";

        private Brush _appVersionColor;
        public Brush AppVersionColor
        {
            get => _appVersionColor;
            set { _appVersionColor = value; OnPropertyChanged(); }
        }

        private MaterialIconKind _appVersionIconKind;
        public MaterialIconKind AppVersionIconKind
        {
            get => _appVersionIconKind;
            set { _appVersionIconKind = value; OnPropertyChanged(); }
        }

        // --- Global System Status ---
        private string _globalStatusText = "System Nominal";
        public string GlobalStatusText
        {
            get => _globalStatusText;
            set { _globalStatusText = value; OnPropertyChanged(); }
        }

        private Brush _globalStatusColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2ECC71")); // Green
        public Brush GlobalStatusColor
        {
            get => _globalStatusColor;
            set { _globalStatusColor = value; OnPropertyChanged(); }
        }

        private MaterialIconKind _globalStatusIconKind = MaterialIconKind.ShieldCheckOutline;
        public MaterialIconKind GlobalStatusIconKind
        {
            get => _globalStatusIconKind;
            set { _globalStatusIconKind = value; OnPropertyChanged(); }
        }

        // --- Data Status Color (Independent for Telemetry) ---
        private Brush _dataStatusColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2ECC71")); // Default Green
        public Brush DataStatusColor
        {
            get => _dataStatusColor;
            set { _dataStatusColor = value; OnPropertyChanged(); }
        }

        // --- System Health Footer Status ---
        private string _systemHealthFooterText = "Self-Check Passed";
        public string SystemHealthFooterText
        {
            get => _systemHealthFooterText;
            set { _systemHealthFooterText = value; OnPropertyChanged(); }
        }

        private Brush _systemHealthFooterColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2ECC71")); // Green
        public Brush SystemHealthFooterColor
        {
            get => _systemHealthFooterColor;
            set { _systemHealthFooterColor = value; OnPropertyChanged(); }
        }

        private MaterialIconKind _systemHealthFooterIconKind = MaterialIconKind.CheckAll;
        public MaterialIconKind SystemHealthFooterIconKind
        {
            get => _systemHealthFooterIconKind;
            set { _systemHealthFooterIconKind = value; OnPropertyChanged(); }
        }

        private string _appVersionFooterText = "Channel: Up to date";
        public string AppVersionFooterText
        {
            get => _appVersionFooterText;
            set { _appVersionFooterText = value; OnPropertyChanged(); }
        }

        private void UpdateAppVersionFooter()
        {
            AppVersionFooterText = $"Channel: {ApplicationInfos.BuildType}";
        }

        public MonitorDashboardModel(MonitorService monitorService, PbeStatusService pbeStatusService, AppSettings appSettings, VersionService versionService, Status statusService, UpdateCheckService updateCheckService)
        {
            _monitorService = monitorService;
            _pbeStatusService = pbeStatusService;
            _appSettings = appSettings;
            _versionService = versionService;
            _statusService = statusService;
            _updateCheckService = updateCheckService;

            // Set Initial App Version State based on Build Type (Stable vs Experimental)
            AppVersionText = ApplicationInfos.Version;

            // Map ApplicationInfos color and icon dynamically
            AppVersionColor = (Brush)Application.Current.FindResource(ApplicationInfos.BuildColorKey);
            AppVersionIconKind = ApplicationInfos.BuildIcon;

            // Check if an update was already found (Higher priority than Build Type)
            if (!string.IsNullOrEmpty(_updateCheckService.AvailableVersion))
            {
                AppVersionText = $"{_updateCheckService.AvailableVersion} available!";
                AppVersionColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F39C12")); // Unified Orange
                AppVersionIconKind = MaterialIconKind.CloudDownload;
            }

            // Subscribe to services using named methods to avoid leaks
            _updateCheckService.UpdatesFound += OnUpdatesFound;

            // Initial PBE Load
            RefreshPbeData();

            // Initial Loads
            RefreshFileWatcherData();
            RefreshAssetTrackerData();
            UpdateAppVersionFooter();
            UpdateGlobalStatus(); // Initial global check
            UpdateSystemHealthFooter(); // Initial footer check

            // Set initial Hashes status based on whether a sync is already in progress
            if (_statusService.IsSyncing)
            {
                HashesStatus = "Updating...";
                UpdateSystemHealthFooter();
            }

            // Subscriptions
            _monitorService.MonitoredAssets.CollectionChanged += MonitoredItems_CollectionChanged;

            // Subscribe to PropertyChanged for existing items
            foreach (var item in _monitorService.MonitoredAssets)
            {
                item.PropertyChanged += MonitoredItem_PropertyChanged;
            }

            _monitorService.CategoryCheckCompleted += OnCategoryCheckCompleted;
            _monitorService.CategoryCheckStarted += OnCategoryCheckStarted;
            _pbeStatusService.StatusChecked += OnPbeStatusChecked;
            _statusService.HashSyncStarted += OnHashSyncStarted;
            _statusService.HashSyncCompleted += OnHashSyncCompleted;

            // Initial Indicator Color
            UpdateHashesIndicatorColor();
        }

        private void UpdateHashesIndicatorColor()
        {
            if (HashesStatus == "Updating...")
            {
                HashesIndicatorColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3498DB")); // Blue
            }
            else if (HashesStatus == "Synced")
            {
                HashesIndicatorColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2ECC71")); // Green
            }
            else
            {
                HashesIndicatorColor = Brushes.Gray;
            }
        }

        private void UpdateGlobalStatus()
        {
            // Priority 1: Critical (Maintenance started - RED)
            if (PbeStatusText == "Under Maintenance")
            {
                GlobalStatusText = "System Alert";
                GlobalStatusColor = new SolidColorBrush(Color.FromRgb(231, 76, 60)); // Red
                GlobalStatusIconKind = MaterialIconKind.AlertCircleOutline;
                return;
            }

            // Priority 2: Warning (Service Alerts, File Changes, or App Updates - ORANGE)
            if (PbeStatusText == "Service Alert" || MonitoredFilesChangedCount > 0 || (AppVersionText != null && AppVersionText.Contains("available!")))
            {
                GlobalStatusText = "Action Required";
                GlobalStatusColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F39C12")); // Orange
                GlobalStatusIconKind = MaterialIconKind.AlertOctagonOutline;
                return;
            }

            // Priority 3: Normal (Nominal - GREEN)
            GlobalStatusText = "System Nominal";
            GlobalStatusColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2ECC71")); // Green
            GlobalStatusIconKind = MaterialIconKind.ShieldCheckOutline;
        }

        private void UpdateDataStatus()
        {
            // Check if there are changed files or new assets
            if (MonitoredFilesChangedCount > 0)
            {
                DataStatusColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F39C12")); // Orange
            }
            else
            {
                DataStatusColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2ECC71")); // Green
            }
        }

        private void UpdateSystemHealthFooter()
        {
            // Case 1: Hashes Updating
            if (HashesStatus == "Updating...")
            {
                SystemHealthFooterText = "Syncing Databases...";
                SystemHealthFooterColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3498DB")); // Blue
                SystemHealthFooterIconKind = MaterialIconKind.Sync;
                return;
            }

            // Case 2: All Good (Hash integrity verified)
            SystemHealthFooterText = "Self-Check Passed";
            SystemHealthFooterColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2ECC71")); // Green
            SystemHealthFooterIconKind = MaterialIconKind.CheckAll;
        }

        private void MonitoredItems_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (MonitoredAsset item in e.NewItems)
                {
                    item.PropertyChanged += MonitoredItem_PropertyChanged;
                }
            }

            if (e.OldItems != null)
            {
                foreach (MonitoredAsset item in e.OldItems)
                {
                    item.PropertyChanged -= MonitoredItem_PropertyChanged;
                }
            }
            RefreshFileWatcherData();
        }

        private void MonitoredItem_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MonitoredAsset.HasChanges) || e.PropertyName == nameof(MonitoredAsset.LastUpdated))
            {
                Application.Current.Dispatcher.InvokeAsync(RefreshFileWatcherData);
            }
        }

        public void RefreshPbeData()
        {
            string status = _appSettings.LastPbeStatusMessage;
            
            if (status == "ONLINE")
            {
                PbeStatusText = "Online";
                PbeStatusSubtitle = "Connection Verified";
                PbeStatusColor = new SolidColorBrush(Color.FromRgb(46, 204, 113)); // Green
                PbeStatusIconKind = MaterialIconKind.ServerNetwork;
            }

            else if (!string.IsNullOrEmpty(status))
            {
                if (status.Contains("Maintenance started at"))
                {
                    PbeStatusText = "Maintenance";
                    PbeStatusSubtitle = status.Replace("Maintenance ", ""); // "Started at HH:mm"
                    PbeStatusColor = new SolidColorBrush(Color.FromRgb(231, 76, 60)); // Red
                    PbeStatusIconKind = MaterialIconKind.Wrench;
                }
                else
                {
                    PbeStatusText = "Service Alert";
                    PbeStatusSubtitle = status;
                    PbeStatusColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F39C12")); // Orange
                    PbeStatusIconKind = MaterialIconKind.ServerNetworkOff;
                }
            }
            else
            {
                PbeStatusText = "Online";
                PbeStatusSubtitle = "Connection: Verified";
                PbeStatusColor = new SolidColorBrush(Color.FromRgb(46, 204, 113)); // Green
                PbeStatusIconKind = MaterialIconKind.ServerNetwork;
            }

            PbeLastCheck = FormatLastCheckTime(_appSettings.LastPbeCheckTime);
            UpdateGlobalStatus();
        }

        public void RefreshFileWatcherData()
        {
            if (_monitorService.MonitoredAssets == null) return;

            MonitoredFilesCount = _monitorService.MonitoredAssets.Count;
            MonitoredFilesChangedCount = _monitorService.MonitoredAssets.Count(x => x.HasChanges);

            var changedAssets = _monitorService.MonitoredAssets.Where(x => x.HasChanges).OrderByDescending(x => x.LastUpdated).ToList();
            LastChangedFileName = changedAssets.FirstOrDefault()?.Alias ?? "None";

            var lastItem = _monitorService.MonitoredAssets.OrderByDescending(x => x.LastUpdated).FirstOrDefault();
            WatcherLastUpdate = lastItem != null && lastItem.LastUpdated != DateTime.MinValue
                ? (lastItem.LastUpdated.Date == DateTime.Today ? lastItem.LastUpdated.ToString("HH:mm") : lastItem.LastUpdated.ToString("dd/MM HH:mm"))
                : "Never";

            // Dynamic Subtitle
            if (MonitoredFilesChangedCount > 0)
            {
                WatcherStatusSubtitle = $"Integrity alert · {MonitoredFilesChangedCount} changes detected";
            }
            else
            {
                WatcherStatusSubtitle = "Surveillance active · Local client synced";
            }

            UpdateGlobalStatus();
            UpdateDataStatus();
        }

        public void RefreshAssetTrackerData()
        {
            if (!_monitorService.AssetCategories.Any()) _monitorService.LoadAssetCategories();

            AssetTrackerCategoriesCount = _monitorService.AssetCategories.Count;
            AssetTrackerTotalFound = _monitorService.AssetCategories.Sum(c => c.FoundUrls?.Count ?? 0);

            var lastActiveCategory = _monitorService.AssetCategories
                .Where(c => c.FoundUrls != null && c.FoundUrls.Any())
                .OrderByDescending(c => c.FoundUrls.Max())
                .FirstOrDefault();

            if (lastActiveCategory != null)
            {
                LastDiscoveredCategory = lastActiveCategory.Name;
                LastDiscoveredAssetId = lastActiveCategory.FoundUrls.Max().ToString();
            }
            else
            {
                LastDiscoveredCategory = "None";
                LastDiscoveredAssetId = "N/A";
            }

            if (_monitorService.AssetCategories.All(c => c.Status == CategoryStatus.Idle || c.Status == CategoryStatus.CompletedSuccess))
            {
                AssetTrackerStatus = "Idle";
            }
            UpdateDataStatus();
        }

        private string FormatLastCheckTime(string timeStr)
        {
            if (string.IsNullOrEmpty(timeStr) || timeStr == "N/A") return "N/A";

            if (DateTime.TryParseExact(timeStr, "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTime dt))
            {
                return dt.Date == DateTime.Today ? dt.ToString("HH:mm") : dt.ToString("yyyy-MM-dd HH:mm");
            }
            return timeStr;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private void OnUpdatesFound(string message, string latestVersion)
        {
            if (!string.IsNullOrEmpty(latestVersion))
            {
                Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    AppVersionText = $"{latestVersion} available!";
                    AppVersionColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F39C12")); // Orange
                    AppVersionIconKind = MaterialIconKind.CloudDownload;
                    UpdateAppVersionFooter();
                    UpdateGlobalStatus();
                    UpdateSystemHealthFooter();
                });
            }
        }

        private void OnCategoryCheckCompleted(AssetCategory category)
        {
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                bool anyOtherChecking = _monitorService.AssetCategories
                    .Where(c => c != category)
                    .Any(c => c.Status == CategoryStatus.Checking);

                if (!anyOtherChecking)
                {
                    AssetTrackerStatus = "Service Idle - Waiting...";
                }

                RefreshAssetTrackerData();
            });
        }

        private void OnCategoryCheckStarted(AssetCategory category)
        {
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                AssetTrackerStatus = $"Category: {category.Name} - Checking...";
            });
        }

        private void OnPbeStatusChecked()
        {
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                RefreshPbeData();
                PbeLastCheck = DateTime.Now.ToString("HH:mm");
            });
        }

        private void OnHashSyncStarted()
        {
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                HashesStatus = "Updating...";
                UpdateSystemHealthFooter();
            });
        }

        private void OnHashSyncCompleted()
        {
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                HashesStatus = "Synced";
                UpdateSystemHealthFooter();
            });
        }

        public void Dispose()
        {
            if (_isDisposed) return;

            if (_updateCheckService != null)
            {
                _updateCheckService.UpdatesFound -= OnUpdatesFound;
            }

            if (_monitorService != null)
            {
                _monitorService.MonitoredAssets.CollectionChanged -= MonitoredItems_CollectionChanged;
                foreach (var item in _monitorService.MonitoredAssets)
                {
                    item.PropertyChanged -= MonitoredItem_PropertyChanged;
                }
                _monitorService.CategoryCheckCompleted -= OnCategoryCheckCompleted;
                _monitorService.CategoryCheckStarted -= OnCategoryCheckStarted;
            }

            if (_pbeStatusService != null)
            {
                _pbeStatusService.StatusChecked -= OnPbeStatusChecked;
            }

            if (_statusService != null)
            {
                _statusService.HashSyncStarted -= OnHashSyncStarted;
                _statusService.HashSyncCompleted -= OnHashSyncCompleted;
            }

            _isDisposed = true;
        }
    }
}
