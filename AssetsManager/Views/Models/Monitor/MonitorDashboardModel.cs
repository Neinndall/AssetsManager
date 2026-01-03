using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Collections.Specialized;
using AssetsManager.Info;
using AssetsManager.Services;
using AssetsManager.Services.Downloads;
using AssetsManager.Services.Monitor;
using AssetsManager.Services.Core;
using AssetsManager.Utils;

namespace AssetsManager.Views.Models.Monitor
{
    public class MonitorDashboardModel : INotifyPropertyChanged
    {
        private readonly MonitorService _monitorService;
        private readonly PbeStatusService _pbeStatusService;
        private readonly AppSettings _appSettings;
        private readonly VersionService _versionService;
        private readonly Status _statusService;
        private readonly UpdateCheckService _updateCheckService;

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

        private string _appVersionText;
        public string AppVersionText
        {
            get => _appVersionText;
            set { _appVersionText = value; OnPropertyChanged(); }
        }

        private Brush _appVersionColor;
        public Brush AppVersionColor
        {
            get => _appVersionColor;
            set { _appVersionColor = value; OnPropertyChanged(); }
        }

        private Material.Icons.MaterialIconKind _appVersionIconKind;
        public Material.Icons.MaterialIconKind AppVersionIconKind
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

        private Material.Icons.MaterialIconKind _globalStatusIconKind = Material.Icons.MaterialIconKind.ShieldCheckOutline;
        public Material.Icons.MaterialIconKind GlobalStatusIconKind
        {
            get => _globalStatusIconKind;
            set { _globalStatusIconKind = value; OnPropertyChanged(); }
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

        private Material.Icons.MaterialIconKind _systemHealthFooterIconKind = Material.Icons.MaterialIconKind.CheckAll;
        public Material.Icons.MaterialIconKind SystemHealthFooterIconKind
        {
            get => _systemHealthFooterIconKind;
            set { _systemHealthFooterIconKind = value; OnPropertyChanged(); }
        }

        public MonitorDashboardModel(MonitorService monitorService, PbeStatusService pbeStatusService, AppSettings appSettings, VersionService versionService, Status statusService, UpdateCheckService updateCheckService)
        {
            _monitorService = monitorService;
            _pbeStatusService = pbeStatusService;
            _appSettings = appSettings;
            _versionService = versionService;
            _statusService = statusService;
            _updateCheckService = updateCheckService;

            // Set Initial App Version State (Normal)
            AppVersionText = ApplicationInfos.Version;
            AppVersionColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2ECC71")); // Green
            AppVersionIconKind = Material.Icons.MaterialIconKind.TagOutline;

            // Check if an update was already found before this model was created
            if (!string.IsNullOrEmpty(_updateCheckService.AvailableVersion))
            {
                AppVersionText = $"{_updateCheckService.AvailableVersion} available!";
                AppVersionColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F39C12")); // Orange
                AppVersionIconKind = Material.Icons.MaterialIconKind.CloudDownload;
            }

            // Subscribe to UpdateCheckService for future updates
            _updateCheckService.UpdatesFound += (message, latestVersion) =>
            {
                // Verify if it's an app update notification
                if (!string.IsNullOrEmpty(latestVersion))
                {
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        AppVersionText = $"v{latestVersion} available!";
                        AppVersionColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F39C12")); // Orange
                        AppVersionIconKind = Material.Icons.MaterialIconKind.CloudDownload;
                        UpdateGlobalStatus();
                        UpdateSystemHealthFooter();
                    });
                }
            };

            // Initialize PBE status with last known message and check time
            if (_appSettings.LastPbeStatusMessage == "ONLINE")
            {
                PbeStatusText = "No issues detected";
                PbeStatusColor = new SolidColorBrush(Color.FromRgb(46, 204, 113)); // Green
            }
            else if (!string.IsNullOrEmpty(_appSettings.LastPbeStatusMessage))
            {
                PbeStatusText = _appSettings.LastPbeStatusMessage;
                PbeStatusColor = new SolidColorBrush(Color.FromRgb(231, 76, 60)); // Red
            }
            else // Default if status is null or empty in settings
            {
                PbeStatusText = "No issues detected";
                PbeStatusColor = new SolidColorBrush(Color.FromRgb(46, 204, 113)); // Green
            }
            PbeLastCheck = !string.IsNullOrEmpty(_appSettings.LastPbeCheckTime) ? _appSettings.LastPbeCheckTime : "N/A";

            // Initial Loads
            RefreshFileWatcherData();
            RefreshAssetTrackerData();
            UpdateGlobalStatus(); // Initial global check
            UpdateSystemHealthFooter(); // Initial footer check

            // Set initial Hashes status based on whether a sync is already in progress
            if (_statusService.IsSyncing)
            {
                HashesStatus = "Updating...";
                UpdateSystemHealthFooter();
            }

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
                    AssetTrackerStatus = "Service Idle - Waiting...";
                }

                RefreshAssetTrackerData();
            };
            _monitorService.CategoryCheckStarted += (category) => AssetTrackerStatus = $"Category: {category.Name} - Checking...";

            _pbeStatusService.StatusChecked += () =>
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() => {
                    RefreshPbeData();
                    PbeLastCheck = DateTime.Now.ToString("HH:mm");
                });
            };

            _statusService.HashSyncStarted += () =>
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    HashesStatus = "Updating...";
                    UpdateSystemHealthFooter();
                });
            };

            _statusService.HashSyncCompleted += () =>
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    HashesStatus = "Synced";
                    UpdateSystemHealthFooter();
                });
            };
        }

        private void UpdateGlobalStatus()
        {
            // Priority 1: Critical (PBE Down)
            if (PbeStatusText != "No issues detected")
            {
                GlobalStatusText = "System Alert";
                GlobalStatusColor = new SolidColorBrush(Color.FromRgb(231, 76, 60)); // Red
                GlobalStatusIconKind = Material.Icons.MaterialIconKind.AlertCircleOutline;
                return;
            }

            // Priority 2: Warning (Updates Pending - Files or App)
            if (MonitoredFilesChangedCount > 0 || (AppVersionText != null && AppVersionText.Contains("available")))
            {
                GlobalStatusText = "Action Required";
                GlobalStatusColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F39C12")); // Orange
                GlobalStatusIconKind = Material.Icons.MaterialIconKind.AlertOctagonOutline;
                return;
            }

            // Priority 3: Normal
            GlobalStatusText = "System Nominal";
            GlobalStatusColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2ECC71")); // Green
            GlobalStatusIconKind = Material.Icons.MaterialIconKind.ShieldCheckOutline;
        }

        private void UpdateSystemHealthFooter()
        {
            // Case 1: Hashes Updating
            if (HashesStatus == "Updating...")
            {
                SystemHealthFooterText = "Syncing Databases...";
                SystemHealthFooterColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3498DB")); // Blue
                SystemHealthFooterIconKind = Material.Icons.MaterialIconKind.Sync;
                return;
            }

            // Case 2: Update Available
            if (AppVersionText != null && AppVersionText.Contains("available"))
            {
                SystemHealthFooterText = "Update Recommended";
                SystemHealthFooterColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F39C12")); // Orange
                SystemHealthFooterIconKind = Material.Icons.MaterialIconKind.CloudDownload;
                return;
            }

            // Case 3: All Good
            SystemHealthFooterText = "Self-Check Passed";
            SystemHealthFooterColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2ECC71")); // Green
            SystemHealthFooterIconKind = Material.Icons.MaterialIconKind.CheckAll;
        }

        private void MonitoredItems_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
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
            if (status == "ONLINE") // Use the specific "code" from PbeStatusService
            {
                PbeStatusText = "No issues detected";
                PbeStatusColor = new SolidColorBrush(Color.FromRgb(46, 204, 113)); // Green
            }
            else if (!string.IsNullOrEmpty(status))
            {
                PbeStatusText = status; // Display the concise maintenance message directly
                PbeStatusColor = new SolidColorBrush(Color.FromRgb(231, 76, 60)); // Red
            }
            else // Default if status is null or empty
            {
                PbeStatusText = "No issues detected";
                PbeStatusColor = new SolidColorBrush(Color.FromRgb(46, 204, 113)); // Green
            }
            UpdateGlobalStatus();
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

            UpdateGlobalStatus();
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