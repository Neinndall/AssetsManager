using AssetsManager.Services.Comparator;
using AssetsManager.Services.Downloads;
using AssetsManager.Services;
using AssetsManager.Services.Core;
using AssetsManager.Services.Monitor;
using AssetsManager.Utils;
using AssetsManager.Views.Controls.Comparator;
using System;
using System.Windows.Controls;
using AssetsManager.Services.Hashes;

namespace AssetsManager.Views
{
    public partial class ComparatorWindow : UserControl
    {
        public ComparatorWindow(
            CustomMessageBoxService customMessageBoxService,
            WadComparatorService wadComparatorService,
            LogService logService,
            DirectoriesCreator directoriesCreator,
            AssetDownloader assetDownloader,
            WadDifferenceService wadDifferenceService,
            WadPackagingService wadPackagingService,
            BackupManager backupManager,
            AppSettings appSettings,
            DiffViewService diffViewService,
            IServiceProvider serviceProvider,
            HashResolverService hashResolverService,
            TaskCancellationManager taskCancellationManager // Added this line
            )
        {
            InitializeComponent();

            // Set services for WadComparisonControl
            WadComparisonControl.CustomMessageBoxService = customMessageBoxService;
            WadComparisonControl.WadComparatorService = wadComparatorService;
            WadComparisonControl.LogService = logService;
            WadComparisonControl.DirectoriesCreator = directoriesCreator;
            WadComparisonControl.AssetDownloaderService = assetDownloader;
            WadComparisonControl.WadDifferenceService = wadDifferenceService;
            WadComparisonControl.WadPackagingService = wadPackagingService;
            WadComparisonControl.BackupManager = backupManager;
            WadComparisonControl.AppSettings = appSettings;
            WadComparisonControl.ServiceProvider = serviceProvider;
            WadComparisonControl.DiffViewService = diffViewService;
            WadComparisonControl.HashResolverService = hashResolverService;
            WadComparisonControl.TaskCancellationManager = taskCancellationManager; // Added this line
        }
    }
}
