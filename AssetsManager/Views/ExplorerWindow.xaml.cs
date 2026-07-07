using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Comparator;
using AssetsManager.Services.Explorer;
using AssetsManager.Services.Core;
using AssetsManager.Services.Monitor;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Explorer;
using AssetsManager.Services.Audio;
using AssetsManager.Services.Explorer.Tree;
using AssetsManager.Views.Controls.Explorer;
using AssetsManager.Services.Downloads;

namespace AssetsManager.Views
{
    public partial class ExplorerWindow : UserControl
    {
        public ExplorerWindow(IServiceProvider serviceProvider)
        {
            InitializeComponent();
            FileExplorer.LogService = serviceProvider.GetRequiredService<LogService>();
            FileExplorer.CustomMessageBoxService = serviceProvider.GetRequiredService<CustomMessageBoxService>();
            FileExplorer.WadContentProvider = serviceProvider.GetRequiredService<WadContentProvider>();
            FileExplorer.WadNodeLoaderService = serviceProvider.GetRequiredService<WadNodeLoaderService>();
            FileExplorer.WadSearchBoxService = serviceProvider.GetRequiredService<WadSearchBoxService>();
            FileExplorer.DiffViewService = serviceProvider.GetRequiredService<DiffViewService>();
            FileExplorer.DirectoriesCreator = serviceProvider.GetRequiredService<DirectoriesCreator>();
            FileExplorer.AppSettings = serviceProvider.GetRequiredService<AppSettings>();
            FileExplorer.TreeBuilderService = serviceProvider.GetRequiredService<TreeBuilderService>();
            FileExplorer.TreeUIManager = serviceProvider.GetRequiredService<TreeUIManager>();
            FileExplorer.AudioBankService = serviceProvider.GetRequiredService<AudioBankService>();
            FileExplorer.AudioBankLinkerService = serviceProvider.GetRequiredService<AudioBankLinkerService>();
            FileExplorer.HashResolverService = serviceProvider.GetRequiredService<HashResolverService>();
            FileExplorer.VersionService = serviceProvider.GetRequiredService<VersionService>();
            FileExplorer.TaskCancellationManager = serviceProvider.GetRequiredService<TaskCancellationManager>();
            FileExplorer.FavoritesManager = serviceProvider.GetRequiredService<FavoritesManager>();
            FileExplorer.ImageMergerService = serviceProvider.GetRequiredService<ImageMergerService>();
            FileExplorer.MonitorService = serviceProvider.GetRequiredService<MonitorService>();
            FileExplorer.BackupManager = serviceProvider.GetRequiredService<BackupManager>();
            FileExplorer.AssetWatcherService = serviceProvider.GetRequiredService<AssetWatcherService>();
            FileExplorer.ProgressUIManager = serviceProvider.GetRequiredService<ProgressUIManager>();
            FileExplorer.ExtractionService = serviceProvider.GetRequiredService<ExtractionService>();
 
            FilePreviewer.LogService = serviceProvider.GetRequiredService<LogService>();
            FilePreviewer.CustomMessageBoxService = serviceProvider.GetRequiredService<CustomMessageBoxService>();
            FilePreviewer.DirectoriesCreator = serviceProvider.GetRequiredService<DirectoriesCreator>();
            FilePreviewer.ExplorerPreviewService = serviceProvider.GetRequiredService<ExplorerPreviewService>();
            FilePreviewer.TreeUIManager = serviceProvider.GetRequiredService<TreeUIManager>();
 
            FileExplorer.FilePreviewer = FilePreviewer;
            FilePreviewer.FileExplorer = FileExplorer;
        }

        public async Task InitializeWithMode(string mode)
        {
            if (FileExplorer != null)
            {
                await FileExplorer.InitializeWithMode(mode);
            }
        }

        public void CleanupResources()
        {
            FileExplorer?.CleanupResources();

            if (FileExplorer != null)
            {
                FileExplorer.FilePreviewer = null;
            }

            if (FilePreviewer != null)
            {
                FilePreviewer.FileExplorer = null;
            }
        }
    }
}
