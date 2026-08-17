using AssetsManager.Services;
using AssetsManager.Services.Core;
using AssetsManager.Services.Downloads;
using AssetsManager.Services.Monitor;
using AssetsManager.Utils;
using AssetsManager.Views.Controls.Monitor;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace AssetsManager.Views
{
    public partial class MonitorWindow : UserControl
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly MonitorService _monitorService;
        private readonly PbeStatusService _pbeStatusService;
        private readonly AppSettings _appSettings;
        private readonly VersionService _versionService;
        private readonly Status _statusService;
        private readonly UpdateCheckService _updateCheckService;
        private readonly DiffViewService _diffViewService;
        private readonly AssetWatcherService _assetWatcherService;
        private readonly LogService _logService;
        private readonly CustomMessageBoxService _customMessageBoxService;
        private readonly ComparisonHistoryService _comparisonHistoryService;
        private readonly AssetDownloader _assetDownloader;
        private readonly TaskCancellationManager _taskCancellationManager;
        private readonly BackupManager _backupManager;
        private readonly RiotApiService _riotApiService;
        private readonly DirectoriesCreator _directoriesCreator;

        private readonly Dictionary<string, UserControl> _tabControls = new(StringComparer.Ordinal);
        private string _activeTab;

        public MonitorWindow(
            IServiceProvider serviceProvider,
            MonitorService monitorService,
            PbeStatusService pbeStatusService,
            AppSettings appSettings,
            VersionService versionService,
            Status statusService,
            UpdateCheckService updateCheckService,
            DiffViewService diffViewService,
            AssetWatcherService assetWatcherService,
            LogService logService,
            CustomMessageBoxService customMessageBoxService,
            ComparisonHistoryService comparisonHistoryService,
            AssetDownloader assetDownloader,
            TaskCancellationManager taskCancellationManager,
            BackupManager backupManager,
            RiotApiService riotApiService,
            DirectoriesCreator directoriesCreator)
        {
            InitializeComponent();

            _serviceProvider = serviceProvider;
            _monitorService = monitorService;
            _pbeStatusService = pbeStatusService;
            _appSettings = appSettings;
            _versionService = versionService;
            _statusService = statusService;
            _updateCheckService = updateCheckService;
            _diffViewService = diffViewService;
            _assetWatcherService = assetWatcherService;
            _logService = logService;
            _customMessageBoxService = customMessageBoxService;
            _comparisonHistoryService = comparisonHistoryService;
            _assetDownloader = assetDownloader;
            _taskCancellationManager = taskCancellationManager;
            _backupManager = backupManager;
            _riotApiService = riotApiService;
            _directoriesCreator = directoriesCreator;

            TabDashboard.Checked += MonitorTab_Checked;
            TabFileWatcher.Checked += MonitorTab_Checked;
            TabHistory.Checked += MonitorTab_Checked;
            TabAssetTracker.Checked += MonitorTab_Checked;
            TabVersions.Checked += MonitorTab_Checked;
            TabBackups.Checked += MonitorTab_Checked;
            TabApi.Checked += MonitorTab_Checked;
            Unloaded += MonitorWindow_Unloaded;

            // Only the default tab is constructed during Monitor startup.
            LoadTab(nameof(TabDashboard));
        }

        private void MonitorTab_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton radioButton)
            {
                LoadTab(radioButton.Name);
            }
        }

        private void LoadTab(string tabKey)
        {
            if (string.IsNullOrWhiteSpace(tabKey) ||
                (_activeTab == tabKey && MonitorContentArea.Content != null))
            {
                return;
            }

            if (!_tabControls.TryGetValue(tabKey, out var tabControl))
            {
                tabControl = CreateTabControl(tabKey);
                if (tabControl == null)
                {
                    return;
                }

                _tabControls[tabKey] = tabControl;
            }

            MonitorContentArea.Content = tabControl;
            _activeTab = tabKey;
        }

        private UserControl CreateTabControl(string tabKey)
        {
            return tabKey switch
            {
                nameof(TabDashboard) => CreateDashboardControl(),
                nameof(TabFileWatcher) => CreateAssetWatcherControl(),
                nameof(TabHistory) => CreateHistoryControl(),
                nameof(TabAssetTracker) => CreateAssetTrackerControl(),
                nameof(TabVersions) => CreateVersionsControl(),
                nameof(TabBackups) => CreateBackupsControl(),
                nameof(TabApi) => CreateApiControl(),
                _ => null
            };
        }

        private MonitorDashboardControl CreateDashboardControl()
        {
            return new MonitorDashboardControl
            {
                MonitorService = _monitorService,
                PbeStatusService = _pbeStatusService,
                AppSettings = _appSettings,
                VersionService = _versionService,
                StatusService = _statusService,
                UpdateCheckService = _updateCheckService
            };
        }

        private AssetWatcherControl CreateAssetWatcherControl()
        {
            return new AssetWatcherControl
            {
                MonitorService = _monitorService,
                ServiceProvider = _serviceProvider,
                DiffViewService = _diffViewService,
                AssetWatcherService = _assetWatcherService,
                AppSettings = _appSettings,
                LogService = _logService,
                CustomMessageBoxService = _customMessageBoxService
            };
        }

        private HistoryViewControl CreateHistoryControl()
        {
            return new HistoryViewControl
            {
                AppSettings = _appSettings,
                LogService = _logService,
                CustomMessageBoxService = _customMessageBoxService,
                DiffViewService = _diffViewService,
                ComparisonHistoryService = _comparisonHistoryService,
                ServiceProvider = _serviceProvider
            };
        }

        private AssetTrackerControl CreateAssetTrackerControl()
        {
            return new AssetTrackerControl
            {
                MonitorService = _monitorService,
                AssetDownloader = _assetDownloader,
                LogService = _logService,
                CustomMessageBoxService = _customMessageBoxService,
                AppSettings = _appSettings
            };
        }

        private ManageVersionsControl CreateVersionsControl()
        {
            return new ManageVersionsControl
            {
                VersionService = _versionService,
                LogService = _logService,
                AppSettings = _appSettings,
                CustomMessageBoxService = _customMessageBoxService,
                TaskCancellationManager = _taskCancellationManager,
                BackupManager = _backupManager
            };
        }

        private BackupsControl CreateBackupsControl()
        {
            return new BackupsControl
            {
                BackupManager = _backupManager,
                VersionService = _versionService,
                LogService = _logService,
                AppSettings = _appSettings,
                CustomMessageBoxService = _customMessageBoxService,
                TaskCancellationManager = _taskCancellationManager,
                ServiceProvider = _serviceProvider
            };
        }

        private ApiControl CreateApiControl()
        {
            return new ApiControl
            {
                LogService = _logService,
                CustomMessageBoxService = _customMessageBoxService,
                RiotApiService = _riotApiService,
                AppSettings = _appSettings,
                DirectoriesCreator = _directoriesCreator
            };
        }

        private void MonitorWindow_Unloaded(object sender, RoutedEventArgs e)
        {
            MonitorContentArea.Content = null;
            _tabControls.Clear();
            _activeTab = null;
        }
    }
}
