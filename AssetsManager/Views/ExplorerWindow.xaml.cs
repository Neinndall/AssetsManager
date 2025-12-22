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
            TaskCancellationManager taskCancellationManager // Added this line
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
            FileExplorer.TaskCancellationManager = taskCancellationManager; // Added this line

            FilePreviewer.LogService = logService;
            FilePreviewer.CustomMessageBoxService = customMessageBoxService;
            FilePreviewer.DirectoriesCreator = directoriesCreator;
            FilePreviewer.ExplorerPreviewService = explorerPreviewService;
            FilePreviewer.TreeUIManager = treeUIManager;

            FileExplorer.FilePreviewer = FilePreviewer; // Set the dependency
        }

        private void ExplorerWindow_Loaded(object sender, RoutedEventArgs e)
        {
            FilePreviewer.BreadcrumbNodeClicked += FilePreviewer_BreadcrumbNodeClicked;
            FileExplorer.BreadcrumbVisibilityChanged += Toolbar_BreadcrumbVisibilityChanged;
        }

        private void Toolbar_BreadcrumbVisibilityChanged(object sender, RoutedPropertyChangedEventArgs<bool> e)
        {
            FilePreviewer.SetBreadcrumbVisibility(e.NewValue ? Visibility.Visible : Visibility.Collapsed);
        }


        private void FilePreviewer_BreadcrumbNodeClicked(object sender, NodeClickedEventArgs e)
        {
            FileExplorer.SelectNode(e.Node);
        }

        private async void FileExplorer_FileSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is FileSystemNodeModel selectedNode)
            {
                FilePreviewer.UpdateSelectedNode(selectedNode, FileExplorer.RootNodes);

                // A Details tab should only be for a RENAMED FILE, not a directory.
                if (selectedNode.Status == DiffStatus.Renamed && selectedNode.Type == NodeType.VirtualFile)
                {
                    FilePreviewer.UpdateAndEnsureSingleDetailsTab(selectedNode);
                }

                // Always show the preview for the selected node.
                await FilePreviewer.ShowPreviewAsync(selectedNode);
            }
        }

        public void CleanupResources()
        {
            // Desuscribir evento
            if (FileExplorer != null)
            {
                FileExplorer.FileSelected -= FileExplorer_FileSelected;
                FileExplorer.BreadcrumbVisibilityChanged -= Toolbar_BreadcrumbVisibilityChanged;
            }

            if (FilePreviewer != null)
            {
                FilePreviewer.BreadcrumbNodeClicked -= FilePreviewer_BreadcrumbNodeClicked;
            }

            // Limpiar controles hijo
            FileExplorer?.CleanupResources();

            // Romper referencia cruzada
            if (FileExplorer != null)
            {
                FileExplorer.FilePreviewer = null;
            }
        }
    }
}
