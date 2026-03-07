using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
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
using NodeClickedEventArgs = AssetsManager.Views.Controls.Explorer.NodeClickedEventArgs;

namespace AssetsManager.Views
{
    public partial class ExplorerWindow : UserControl
    {
        public ExplorerWindow(
            LogService logService,
            CustomMessageBoxService customMessageBoxService,
            WadExtractionService wadExtractionService,
            WadSearchBoxService wadSearchBoxService,
            DirectoriesCreator directoriesCreator,
            ExplorerPreviewService explorerPreviewService,
            DiffViewService diffViewService,
            AppSettings appSettings,
            TreeBuilderService treeBuilderService,
            TreeUIManager treeUIManager,
            AudioBankService audioBankService,
            AudioBankLinkerService audioBankLinkerService,
            WadSavingService wadSavingService,
            HashResolverService hashResolverService,
            TaskCancellationManager taskCancellationManager,
            FavoritesManager favoritesManager,
            ImageMergerService imageMergerService,
            ProgressUIManager progressUIManager
        )
        {
            InitializeComponent();
            this.Loaded += ExplorerWindow_Loaded;

            FileExplorer.LogService = logService;
            FileExplorer.CustomMessageBoxService = customMessageBoxService;
            FileExplorer.WadExtractionService = wadExtractionService;
            FileExplorer.WadSavingService = wadSavingService;
            FileExplorer.WadSearchBoxService = wadSearchBoxService;
            FileExplorer.DiffViewService = diffViewService;
            FileExplorer.DirectoriesCreator = directoriesCreator;
            FileExplorer.AppSettings = appSettings;
            FileExplorer.TreeBuilderService = treeBuilderService;
            FileExplorer.TreeUIManager = treeUIManager;
            FileExplorer.AudioBankService = audioBankService;
            FileExplorer.AudioBankLinkerService = audioBankLinkerService;
            FileExplorer.HashResolverService = hashResolverService;
            FileExplorer.TaskCancellationManager = taskCancellationManager;
            FileExplorer.FavoritesManager = favoritesManager;
            FileExplorer.ImageMergerService = imageMergerService;
            FileExplorer.ProgressUIManager = progressUIManager;

            FilePreviewer.LogService = logService;
            FilePreviewer.CustomMessageBoxService = customMessageBoxService;
            FilePreviewer.DirectoriesCreator = directoriesCreator;
            FilePreviewer.ExplorerPreviewService = explorerPreviewService;
            FilePreviewer.TreeUIManager = treeUIManager;

            FileExplorer.FilePreviewer = FilePreviewer;
        }

        private void ExplorerWindow_Loaded(object sender, RoutedEventArgs e)
        {
            FilePreviewer.BreadcrumbNodeClicked += FilePreviewer_BreadcrumbNodeClicked;
            FilePreviewer.SelectionActionRequested += FilePreviewer_SelectionActionRequested;
            FileExplorer.BreadcrumbVisibilityChanged += Toolbar_BreadcrumbVisibilityChanged;
        }

        private void Toolbar_BreadcrumbVisibilityChanged(object sender, RoutedPropertyChangedEventArgs<bool> e)
        {
            FilePreviewer.SetBreadcrumbToggleState(e.NewValue);
        }

        private void FilePreviewer_BreadcrumbNodeClicked(object sender, NodeClickedEventArgs e)
        {
            FileExplorer.SelectNode(e.Node);
        }

        private void FilePreviewer_SelectionActionRequested(object sender, SelectionActionEventArgs e)
        {
            switch (e.Action)
            {
                case "Extract":
                    FileExplorer.TriggerExtractNodes(e.Nodes);
                    break;
                case "Save":
                    FileExplorer.TriggerSaveNodes(e.Nodes);
                    break;
                case "Merge":
                    FileExplorer.TriggerAddToMerger(e.Nodes);
                    break;
            }
        }

        private async void FileExplorer_FileSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is FileSystemNodeModel selectedNode)
            {
                FilePreviewer.UpdateSelectedNode(selectedNode, FileExplorer.RootNodes);
                await FilePreviewer.ShowPreviewAsync(selectedNode);
            }
        }

        public void CleanupResources()
        {
            if (FileExplorer != null)
            {
                FileExplorer.FileSelected -= FileExplorer_FileSelected;
                FileExplorer.BreadcrumbVisibilityChanged -= Toolbar_BreadcrumbVisibilityChanged;
            }

            if (FilePreviewer != null)
            {
                FilePreviewer.BreadcrumbNodeClicked -= FilePreviewer_BreadcrumbNodeClicked;
                FilePreviewer.SelectionActionRequested -= FilePreviewer_SelectionActionRequested;
            }

            FileExplorer?.CleanupResources();

            if (FileExplorer != null)
            {
                FileExplorer.FilePreviewer = null;
            }
        }
    }
}
