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
        public MonitorWindow(IServiceProvider serviceProvider) 
        {
            InitializeComponent();
 
            // Inject all necessary dependencies into the MonitorDashboardControl
            MonitorDashboardControl.MonitorService = serviceProvider.GetRequiredService<MonitorService>();
            MonitorDashboardControl.PbeStatusService = serviceProvider.GetRequiredService<PbeStatusService>();
            MonitorDashboardControl.AppSettings = serviceProvider.GetRequiredService<AppSettings>();
            MonitorDashboardControl.VersionService = serviceProvider.GetRequiredService<VersionService>();
            MonitorDashboardControl.StatusService = serviceProvider.GetRequiredService<Status>();
            MonitorDashboardControl.UpdateCheckService = serviceProvider.GetRequiredService<UpdateCheckService>();
 
            // Inject all necessary dependencies into the AssetWatcherControl
            AssetWatcherControl.MonitorService = serviceProvider.GetRequiredService<MonitorService>();
            AssetWatcherControl.ServiceProvider = serviceProvider;
            AssetWatcherControl.DiffViewService = serviceProvider.GetRequiredService<DiffViewService>();
            AssetWatcherControl.AssetWatcherService = serviceProvider.GetRequiredService<AssetWatcherService>();
            AssetWatcherControl.AppSettings = serviceProvider.GetRequiredService<AppSettings>();
            AssetWatcherControl.LogService = serviceProvider.GetRequiredService<LogService>();
            AssetWatcherControl.CustomMessageBoxService = serviceProvider.GetRequiredService<CustomMessageBoxService>();
 
            // Setup and inject dependencies for HistoryViewControl
            HistoryViewControl.AppSettings = serviceProvider.GetRequiredService<AppSettings>();
            HistoryViewControl.LogService = serviceProvider.GetRequiredService<LogService>();
            HistoryViewControl.CustomMessageBoxService = serviceProvider.GetRequiredService<CustomMessageBoxService>();
            HistoryViewControl.DiffViewService = serviceProvider.GetRequiredService<DiffViewService>();
            HistoryViewControl.ComparisonHistoryService = serviceProvider.GetRequiredService<ComparisonHistoryService>();
            HistoryViewControl.ServiceProvider = serviceProvider;
 
            // Setup and inject dependencies for AssetTrackerControl
            AssetTrackerControl.MonitorService = serviceProvider.GetRequiredService<MonitorService>();
            AssetTrackerControl.AssetDownloader = serviceProvider.GetRequiredService<AssetDownloader>();
            AssetTrackerControl.LogService = serviceProvider.GetRequiredService<LogService>();
            AssetTrackerControl.CustomMessageBoxService = serviceProvider.GetRequiredService<CustomMessageBoxService>();
            AssetTrackerControl.AppSettings = serviceProvider.GetRequiredService<AppSettings>();
 
            // Setup and inject dependencies for ManageVersionsControl
            ManageVersionsControl.VersionService = serviceProvider.GetRequiredService<VersionService>();
            ManageVersionsControl.LogService = serviceProvider.GetRequiredService<LogService>();
            ManageVersionsControl.AppSettings = serviceProvider.GetRequiredService<AppSettings>();
            ManageVersionsControl.CustomMessageBoxService = serviceProvider.GetRequiredService<CustomMessageBoxService>();
            ManageVersionsControl.TaskCancellationManager = serviceProvider.GetRequiredService<TaskCancellationManager>();
 
            // Setup and inject dependencies for BackupsControl
            BackupsControl.BackupManager = serviceProvider.GetRequiredService<BackupManager>();
            BackupsControl.VersionService = serviceProvider.GetRequiredService<VersionService>();
            BackupsControl.LogService = serviceProvider.GetRequiredService<LogService>();
            BackupsControl.AppSettings = serviceProvider.GetRequiredService<AppSettings>();
            BackupsControl.CustomMessageBoxService = serviceProvider.GetRequiredService<CustomMessageBoxService>();
            BackupsControl.TaskCancellationManager = serviceProvider.GetRequiredService<TaskCancellationManager>();
            BackupsControl.ServiceProvider = serviceProvider;
 
            // Setup and inject dependencies for ApiControl
            ApiControl.LogService = serviceProvider.GetRequiredService<LogService>();
            ApiControl.CustomMessageBoxService = serviceProvider.GetRequiredService<CustomMessageBoxService>();
            ApiControl.RiotApiService = serviceProvider.GetRequiredService<RiotApiService>();
            ApiControl.AppSettings = serviceProvider.GetRequiredService<AppSettings>();
            ApiControl.DirectoriesCreator = serviceProvider.GetRequiredService<DirectoriesCreator>();
        }
    }
}
