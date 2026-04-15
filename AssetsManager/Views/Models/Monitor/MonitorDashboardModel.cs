using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows;
using AssetsManager.Services.Monitor;
using AssetsManager.Services.Core;
using AssetsManager.Services.Downloads;
using AssetsManager.Views.Models.Shared;
using AssetsManager.Utils;
using AssetsManager.Utils.Framework;
using AssetsManager.Info;
using Material.Icons;

namespace AssetsManager.Views.Models.Monitor
{
    public class MonitorDashboardModel : INotifyPropertyChanged
    {
        private readonly MonitorService _monitorService;
        private readonly PbeStatusService _pbeStatusService;
        private readonly VersionService _versionService;
        private readonly AppSettings _appSettings;
        private readonly Status _statusService;
        private readonly UpdateCheckService _updateCheckService;

        private bool _isBusy;

        public MonitorDashboardModel(
            MonitorService monitorService,
            PbeStatusService pbeStatusService,
            VersionService versionService,
            AppSettings appSettings,
            Status statusService,
            UpdateCheckService updateCheckService)
        {
            _monitorService = monitorService;
            _pbeStatusService = pbeStatusService;
            _versionService = versionService;
            _appSettings = appSettings;
            _statusService = statusService;
            _updateCheckService = updateCheckService;

            InitializeDefaultValues();
            RefreshAllAsync().ConfigureAwait(false);
        }

        private void InitializeDefaultValues()
        {
            PbeStatusColor = (SolidColorBrush)Application.Current.FindResource("TextMuted");
            DataStatusColor = (SolidColorBrush)Application.Current.FindResource("TextMuted");
            AppVersionColor = (SolidColorBrush)Application.Current.FindResource("TextMuted");
            
            PbeStatusText = "Initializing...";
            PbeLastCheck = "N/A";
            
            AppVersionText = ApplicationInfos.Version;
            AppVersionIconKind = ApplicationInfos.BuildIcon;
            BuildType = ApplicationInfos.BuildType;
            
            HashesStatus = "Checking...";
            SystemHealthFooterText = "Standby";
            SystemHealthFooterColor = (SolidColorBrush)Application.Current.FindResource("TextMuted");
            SystemHealthFooterIconKind = MaterialIconKind.InformationOutline;
            
            WatcherLastUpdate = "No updates detected";
            AssetTrackerStatus = "Standby";
        }

        #region Properties for XAML Bindings

        // HUD Indicators
        private Brush _pbeStatusColor;
        public Brush PbeStatusColor { get => _pbeStatusColor; set { _pbeStatusColor = value; OnPropertyChanged(); } }

        private Brush _dataStatusColor;
        public Brush DataStatusColor { get => _dataStatusColor; set { _dataStatusColor = value; OnPropertyChanged(); } }

        private Brush _appVersionColor;
        public Brush AppVersionColor { get => _appVersionColor; set { _appVersionColor = value; OnPropertyChanged(); } }

        // CARD 1: PBE SERVER
        private string _pbeStatusText;
        public string PbeStatusText { get => _pbeStatusText; set { _pbeStatusText = value; OnPropertyChanged(); } }

        private string _pbeLastCheck;
        public string PbeLastCheck { get => _pbeLastCheck; set { _pbeLastCheck = value; OnPropertyChanged(); } }

        // CARD 2: SYSTEM HEALTH
        private string _appVersionText;
        public string AppVersionText { get => _appVersionText; set { _appVersionText = value; OnPropertyChanged(); } }

        private MaterialIconKind _appVersionIconKind;
        public MaterialIconKind AppVersionIconKind { get => _appVersionIconKind; set { _appVersionIconKind = value; OnPropertyChanged(); } }

        private string _hashesStatus;
        public string HashesStatus { get => _hashesStatus; set { _hashesStatus = value; OnPropertyChanged(); } }

        private string _systemHealthFooterText;
        public string SystemHealthFooterText { get => _systemHealthFooterText; set { _systemHealthFooterText = value; OnPropertyChanged(); } }

        private Brush _systemHealthFooterColor;
        public Brush SystemHealthFooterColor { get => _systemHealthFooterColor; set { _systemHealthFooterColor = value; OnPropertyChanged(); } }

        private MaterialIconKind _systemHealthFooterIconKind;
        public MaterialIconKind SystemHealthFooterIconKind { get => _systemHealthFooterIconKind; set { _systemHealthFooterIconKind = value; OnPropertyChanged(); } }

        private string _buildType;
        public string BuildType { get => _buildType; set { _buildType = value; OnPropertyChanged(); } }

        // CARD 3: ASSET WATCHER (Local)
        public int MonitoredFilesCount => _monitorService.MonitoredAssets.Count;
        public int MonitoredFilesChangedCount => _monitorService.MonitoredAssets.Count(a => a.HasChanges);

        private string _watcherLastUpdate;
        public string WatcherLastUpdate { get => _watcherLastUpdate; set { _watcherLastUpdate = value; OnPropertyChanged(); } }

        // CARD 4: ASSET TRACKER
        public int AssetTrackerTotalFound => _monitorService.AssetCategories.Sum(c => c.FoundUrls.Count);
        public int AssetTrackerCategoriesCount => _monitorService.AssetCategories.Count;

        private string _assetTrackerStatus;
        public string AssetTrackerStatus { get => _assetTrackerStatus; set { _assetTrackerStatus = value; OnPropertyChanged(); } }

        public bool IsBusy { get => _isBusy; set { _isBusy = value; OnPropertyChanged(); } }

        #endregion

        public async Task RefreshAllAsync()
        {
            if (IsBusy) return;
            IsBusy = true;

            try
            {
                // 1. PBE Status
                await _pbeStatusService.CheckPbeStatusAsync();
                PbeStatusText = _appSettings.LastPbeStatusMessage ?? "OFFLINE";
                PbeLastCheck = _appSettings.LastPbeCheckTime ?? "N/A";
                bool isPbeOk = PbeStatusText == "ONLINE";
                PbeStatusColor = (SolidColorBrush)Application.Current.FindResource(isPbeOk ? "AccentGreen" : "AccentOrange");

                // 2. Hash Database Status
                var outdatedHashes = await _statusService.GetOutdatedHashFilesAsync(true);
                bool hashesOk = outdatedHashes == null || outdatedHashes.Count == 0;
                HashesStatus = hashesOk ? "All hashes healthy" : $"{outdatedHashes.Count} files outdated";
                DataStatusColor = (SolidColorBrush)Application.Current.FindResource(hashesOk ? "AccentGreen" : "AccentOrange");

                // 3. App Version Status
                await _updateCheckService.CheckForGeneralUpdatesAsync(true);
                bool isAppLatest = string.IsNullOrEmpty(_updateCheckService.AvailableVersion);
                AppVersionColor = (SolidColorBrush)Application.Current.FindResource(isAppLatest ? "AccentGreen" : "AccentOrange");
                
                // System Health Footer logic
                if (!isAppLatest)
                {
                    SystemHealthFooterText = "UPDATE AVAILABLE";
                    SystemHealthFooterColor = (SolidColorBrush)Application.Current.FindResource("AccentOrange");
                    SystemHealthFooterIconKind = MaterialIconKind.CloudDownload;
                }
                else if (!hashesOk)
                {
                    SystemHealthFooterText = "HASHES OUTDATED";
                    SystemHealthFooterColor = (SolidColorBrush)Application.Current.FindResource("AccentOrange");
                    SystemHealthFooterIconKind = MaterialIconKind.DatabaseAlert;
                }
                else
                {
                    SystemHealthFooterText = "ALL SYSTEMS GO";
                    SystemHealthFooterColor = (SolidColorBrush)Application.Current.FindResource("AccentGreen");
                    SystemHealthFooterIconKind = MaterialIconKind.CheckCircleOutline;
                }

                // 4. Asset Watcher Info
                OnPropertyChanged(nameof(MonitoredFilesCount));
                OnPropertyChanged(nameof(MonitoredFilesChangedCount));
                var lastChangedAsset = _monitorService.MonitoredAssets.OrderByDescending(a => a.LastUpdated).FirstOrDefault();
                if (lastChangedAsset != null && lastChangedAsset.LastUpdated != DateTime.MinValue)
                {
                    WatcherLastUpdate = $"Updated: {lastChangedAsset.Alias} ({lastChangedAsset.LastUpdated:HH:mm})";
                }
                else
                {
                    WatcherLastUpdate = "No updates detected";
                }

                // 5. Asset Tracker Info
                OnPropertyChanged(nameof(AssetTrackerTotalFound));
                OnPropertyChanged(nameof(AssetTrackerCategoriesCount));
                AssetTrackerStatus = "Scanner ready";

            }
            catch (Exception)
            {
                SystemHealthFooterText = "DIAGNOSTICS ERROR";
                SystemHealthFooterColor = (SolidColorBrush)Application.Current.FindResource("AccentRed");
                SystemHealthFooterIconKind = MaterialIconKind.AlertCircle;
            }
            finally
            {
                IsBusy = false;
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
