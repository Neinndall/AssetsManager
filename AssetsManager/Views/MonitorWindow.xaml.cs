using AssetsManager.Services;
using AssetsManager.Services.Core;
using AssetsManager.Services.Downloads;
using AssetsManager.Services.Monitor;
using AssetsManager.Utils;
using System;
using System.Windows.Controls;
using AssetsManager.Views.Controls.Monitor;
using AssetsManager.Views.Models.Monitor;

namespace AssetsManager.Views
{
    public partial class MonitorWindow : UserControl
    {
        public MonitorWindow(
            MonitorService monitorService,
            AssetDownloader assetDownloader,
            IServiceProvider serviceProvider,
            DiffViewService diffViewService,
            AppSettings appSettings,
            LogService logService,
            CustomMessageBoxService customMessageBoxService,
            JsonDataService jsonDataService,
            VersionService versionService,
            DirectoriesCreator directoriesCreator,
            RiotApiService riotApiService,
            TaskCancellationManager taskCancellationManager,
            BackupManager backupManager,
            PbeStatusService pbeStatusService,
            Status statusService,
            UpdateCheckService updateCheckService,
            ComparisonHistoryService comparisonHistoryService) 
        {
            InitializeComponent();

            // Inject all necessary dependencies into the MonitorDashboardControl
            MonitorDashboardControl.MonitorService = monitorService;
            MonitorDashboardControl.PbeStatusService = pbeStatusService;
            MonitorDashboardControl.AppSettings = appSettings;
            MonitorDashboardControl.VersionService = versionService;
            MonitorDashboardControl.StatusService = statusService;
            MonitorDashboardControl.UpdateCheckService = updateCheckService;

            // Inject all necessary dependencies into the FileWatcherControl
            FileWatcherControl.MonitorService = monitorService;
            FileWatcherControl.ServiceProvider = serviceProvider;
            FileWatcherControl.DiffViewService = diffViewService;
            FileWatcherControl.JsonDataService = jsonDataService;
            FileWatcherControl.AppSettings = appSettings;
            FileWatcherControl.LogService = logService;
            FileWatcherControl.CustomMessageBoxService = customMessageBoxService;

            // Setup and inject dependencies for HistoryViewControl
            HistoryViewControl.AppSettings = appSettings;
            HistoryViewControl.LogService = logService;
            HistoryViewControl.CustomMessageBoxService = customMessageBoxService;
            HistoryViewControl.DiffViewService = diffViewService;
            HistoryViewControl.ComparisonHistoryService = comparisonHistoryService;
            HistoryViewControl.ServiceProvider = serviceProvider;

            // Setup and inject dependencies for AssetTrackerControl
            AssetTrackerControl.MonitorService = monitorService;
            AssetTrackerControl.AssetDownloader = assetDownloader;
            AssetTrackerControl.LogService = logService;
            AssetTrackerControl.CustomMessageBoxService = customMessageBoxService;
            AssetTrackerControl.AppSettings = appSettings;

            // Setup and inject dependencies for ManageVersionsControl
            ManageVersionsControl.VersionService = versionService;
            ManageVersionsControl.LogService = logService;
            ManageVersionsControl.AppSettings = appSettings;
            ManageVersionsControl.CustomMessageBoxService = customMessageBoxService;
            ManageVersionsControl.TaskCancellationManager = taskCancellationManager;

            // Setup and inject dependencies for BackupsControl
            BackupsControl.BackupManager = backupManager;
            BackupsControl.LogService = logService;
            BackupsControl.AppSettings = appSettings;
            BackupsControl.CustomMessageBoxService = customMessageBoxService;
            BackupsControl.TaskCancellationManager = taskCancellationManager;

            // Setup and inject dependencies for ApiControl
            ApiControl.LogService = logService;
            ApiControl.CustomMessageBoxService = customMessageBoxService;
            ApiControl.RiotApiService = riotApiService;
            ApiControl.AppSettings = appSettings;
            ApiControl.DirectoriesCreator = directoriesCreator;
        }
    }
}
