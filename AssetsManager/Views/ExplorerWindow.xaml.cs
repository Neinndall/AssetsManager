using System.Windows;
using System.Windows.Controls;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Comparator;
using AssetsManager.Services.Explorer;
using AssetsManager.Services.Core;
using AssetsManager.Services.Monitor;
using AssetsManager.Utils;
using AssetsManager.Views.Models;
using AssetsManager.Services.Audio;

using AssetsManager.Services.Explorer.Tree;

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
        HashResolverService hashResolverService // Added HashResolverService
    )
    {
      InitializeComponent();

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
      FileExplorer.HashResolverService = hashResolverService; // Assign the service

      FilePreviewer.LogService = logService;
      FilePreviewer.CustomMessageBoxService = customMessageBoxService;
      FilePreviewer.DirectoriesCreator = directoriesCreator;
      FilePreviewer.ExplorerPreviewService = explorerPreviewService;

      FileExplorer.FilePreviewer = FilePreviewer; // Set the dependency
    }


    private async void FileExplorer_FileSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
      if (e.NewValue is FileSystemNodeModel selectedNode)
      {
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
