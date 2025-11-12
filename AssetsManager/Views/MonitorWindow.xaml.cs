using AssetsManager.Services;
using AssetsManager.Services.Api;
using AssetsManager.Services.Core;
using AssetsManager.Services.Downloads;
using AssetsManager.Services.Monitor;
using AssetsManager.Services.Versions;
using AssetsManager.Utils;
using System;
using System.Windows.Controls;
using AssetsManager.Views.Controls.Monitor;

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
            RiotApiService riotApiService) // Added RiotApiService
        {
            InitializeComponent();

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

            // Setup and inject dependencies for AssetTrackerControl
            AssetTrackerControl.MonitorService = monitorService;
            AssetTrackerControl.AssetDownloader = assetDownloader;
            AssetTrackerControl.LogService = logService;
            AssetTrackerControl.CustomMessageBoxService = customMessageBoxService;

            // Setup and inject dependencies for ManageVersionsControl
            ManageVersionsControl.VersionService = versionService;
            ManageVersionsControl.LogService = logService;
            ManageVersionsControl.AppSettings = appSettings;
            ManageVersionsControl.CustomMessageBoxService = customMessageBoxService;

            // Setup and inject dependencies for ApiControl
            ApiControl.LogService = logService;
            ApiControl.CustomMessageBoxService = customMessageBoxService;
            ApiControl.RiotApiService = riotApiService;
            ApiControl.AppSettings = appSettings;
            ApiControl.DirectoriesCreator = directoriesCreator;
        }
    }
}
