using AssetsManager.Services;
using AssetsManager.Services.Core;
using AssetsManager.Services.Downloads;
using AssetsManager.Services.Monitor;
using AssetsManager.Utils;
using System;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using AssetsManager.Views.Controls.Monitor;
using AssetsManager.Views.Models.Monitor;

namespace AssetsManager.Views
{
    public partial class MonitorWindow : UserControl
    {
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
 
            // Inject all necessary dependencies into the MonitorDashboardControl
            MonitorDashboardControl.MonitorService = monitorService;
            MonitorDashboardControl.PbeStatusService = pbeStatusService;
            MonitorDashboardControl.AppSettings = appSettings;
            MonitorDashboardControl.VersionService = versionService;
            MonitorDashboardControl.StatusService = statusService;
            MonitorDashboardControl.UpdateCheckService = updateCheckService;
 
            // Inject all necessary dependencies into the AssetWatcherControl
            AssetWatcherControl.MonitorService = monitorService;
            AssetWatcherControl.ServiceProvider = serviceProvider;
            AssetWatcherControl.DiffViewService = diffViewService;
            AssetWatcherControl.AssetWatcherService = assetWatcherService;
            AssetWatcherControl.AppSettings = appSettings;
            AssetWatcherControl.LogService = logService;
            AssetWatcherControl.CustomMessageBoxService = customMessageBoxService;
 
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
            ManageVersionsControl.BackupManager = backupManager;
 
            // Setup and inject dependencies for BackupsControl
            BackupsControl.BackupManager = backupManager;
            BackupsControl.VersionService = versionService;
            BackupsControl.LogService = logService;
            BackupsControl.AppSettings = appSettings;
            BackupsControl.CustomMessageBoxService = customMessageBoxService;
            BackupsControl.TaskCancellationManager = taskCancellationManager;
            BackupsControl.ServiceProvider = serviceProvider;
 
            // Setup and inject dependencies for ApiControl
            ApiControl.LogService = logService;
            ApiControl.CustomMessageBoxService = customMessageBoxService;
            ApiControl.RiotApiService = riotApiService;
            ApiControl.AppSettings = appSettings;
            ApiControl.DirectoriesCreator = directoriesCreator;
        }
    }
}
