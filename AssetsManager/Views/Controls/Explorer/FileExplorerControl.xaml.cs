using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

using Microsoft.WindowsAPICodePack.Dialogs;
using AssetsManager.Services.Monitor;
using AssetsManager.Services.Comparator;  
using AssetsManager.Services.Hashes;      
using AssetsManager.Services.Core;        
using AssetsManager.Services.Explorer;    
using AssetsManager.Utils;                
using AssetsManager.Views.Models;         

namespace AssetsManager.Views.Controls.Explorer
{
    public partial class FileExplorerControl : UserControl
    {
        public event RoutedPropertyChangedEventHandler<object> FileSelected;

        public FilePreviewerControl FilePreviewer { get; set; }

        public MenuItem PinMenuItem => (this.FindResource("ExplorerContextMenu") as ContextMenu)?.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "PinMenuItem");
        public MenuItem ViewChangesMenuItem => (this.FindResource("ExplorerContextMenu") as ContextMenu)?.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "ViewChangesMenuItem");

        public LogService LogService { get; set; }
        public CustomMessageBoxService CustomMessageBoxService { get; set; }
        public HashResolverService HashResolverService { get; set; }
        public WadNodeLoaderService WadNodeLoaderService { get; set; }
        public WadExtractionService WadExtractionService { get; set; }
        public WadSearchBoxService WadSearchBoxService { get; set; }
        public DiffViewService DiffViewService { get; set; }
        public DirectoriesCreator DirectoriesCreator { get; set; }
        public AppSettings AppSettings { get; set; }

        public ObservableCollection<FileSystemNodeModel> RootNodes { get; set; }
        private readonly DispatcherTimer _searchTimer;
        private string _currentRootPath;
        private bool _isWadMode = true;

        public FileExplorerControl()
        {
            InitializeComponent();
            RootNodes = new ObservableCollection<FileSystemNodeModel>();
            DataContext = this;
            this.Loaded += FileExplorerControl_Loaded;

            _searchTimer = new DispatcherTimer();
            _searchTimer.Interval = TimeSpan.FromMilliseconds(300);
            _searchTimer.Tick += SearchTimer_Tick;
        }

        private async void FileExplorerControl_Loaded(object sender, RoutedEventArgs e)
        {
            Toolbar.SearchTextChanged += Toolbar_SearchTextChanged;
            Toolbar.CollapseToContainerClicked += Toolbar_CollapseToContainerClicked;
            Toolbar.LoadComparisonClicked += Toolbar_LoadComparisonClicked;
            Toolbar.SwitchModeClicked += Toolbar_SwitchModeClicked;

            if (!string.IsNullOrEmpty(AppSettings.LolDirectory) && Directory.Exists(AppSettings.LolDirectory))
            {
                await BuildWadTreeAsync(AppSettings.LolDirectory);
            }
            else
            {
                FileTreeView.Visibility = Visibility.Collapsed;
                NoDirectoryMessage.Visibility = Visibility.Visible;
            }
        }

        private async void Toolbar_SwitchModeClicked(object sender, RoutedEventArgs e)
        {
            _isWadMode = !_isWadMode;
            await ReloadTreeAsync();
        }

        public async Task ReloadTreeAsync()
        {
            if (_isWadMode)
            {
                if (!string.IsNullOrEmpty(AppSettings.LolDirectory) && Directory.Exists(AppSettings.LolDirectory))
                {
                    await BuildWadTreeAsync(AppSettings.LolDirectory);
                }
                else
                {
                    FileTreeView.Visibility = Visibility.Collapsed;
                    PlaceholderTitle.Text = "Select a LoL Directory";
                    PlaceholderDescription.Text = "Choose the root folder where you installed League of Legends to browse its WAD files.";
                    SelectLolDirButton.Visibility = Visibility.Visible;
                    NoDirectoryMessage.Visibility = Visibility.Visible;
                }
            }
            else
            {
                if (!string.IsNullOrEmpty(DirectoriesCreator.AssetsDownloadedPath) && Directory.Exists(DirectoriesCreator.AssetsDownloadedPath))
                {
                    await BuildDirectoryTreeAsync(DirectoriesCreator.AssetsDownloadedPath);
                }
                else
                {
                    FileTreeView.Visibility = Visibility.Collapsed;
                    PlaceholderTitle.Text = "Assets Directory Not Found";
                    PlaceholderDescription.Text = "The application could not find the directory for downloaded assets.";
                    SelectLolDirButton.Visibility = Visibility.Collapsed;
                    NoDirectoryMessage.Visibility = Visibility.Visible;
                }
            }
        }

        private async void Toolbar_LoadComparisonClicked(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new CommonOpenFileDialog
            {
                Title = "Select Wadcomparison File",
                Filters = { new CommonFileDialogFilter("WAD Comparison JSON", "wadcomparison.json"), new CommonFileDialogFilter("All files", "*.*") },
                InitialDirectory = DirectoriesCreator.WadComparisonSavePath
            };

            if (openFileDialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                await BuildTreeFromBackupAsync(openFileDialog.FileName);
            }
        }

        private async Task BuildTreeFromBackupAsync(string jsonPath)
        {
            NoDirectoryMessage.Visibility = Visibility.Collapsed;
            FileTreeView.Visibility = Visibility.Collapsed;
            LoadingIndicator.Visibility = Visibility.Visible;

            try
            {
                RootNodes.Clear();
                // Note: LoadFromBackupAsync will be created in the next step.
                var backupNodes = await WadNodeLoaderService.LoadFromBackupAsync(jsonPath);
                foreach (var node in backupNodes)
                {
                    RootNodes.Add(node);
                }
            }
            catch (Exception ex)
            {
                LogService.LogError(ex, "Failed to build tree from backup.");
                CustomMessageBoxService.ShowError("Error", "Could not load the backup file. Please check the logs.", Window.GetWindow(this));
                NoDirectoryMessage.Visibility = Visibility.Visible;
            }
            finally
            {
                LoadingIndicator.Visibility = Visibility.Collapsed;
                FileTreeView.Visibility = Visibility.Visible;
                Toolbar.Visibility = Visibility.Visible;
                ToolbarSeparator.Visibility = Visibility.Visible;
            }
        }


        private async void ExtractSelected_Click(object sender, RoutedEventArgs e)
        {
            if (WadExtractionService == null)
            {
                CustomMessageBoxService.ShowError("Error", "Extraction Service is not available.", Window.GetWindow(this));
                return;
            }

            if (FileTreeView.SelectedItem is not FileSystemNodeModel selectedNode)
            {
                CustomMessageBoxService.ShowInfo("Info", "Please select a file or folder to extract.", Window.GetWindow(this));
                return;
            }

            string destinationPath = null;

            if (!string.IsNullOrEmpty(AppSettings.DefaultExtractedSelectDirectory) && Directory.Exists(AppSettings.DefaultExtractedSelectDirectory))
            {
                destinationPath = AppSettings.DefaultExtractedSelectDirectory;
            }
            else
            {
                var dialog = new CommonOpenFileDialog
                {
                    IsFolderPicker = true,
                    Title = "Select Destination Folder"
                };

                if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
                {
                    destinationPath = dialog.FileName;
                }
            }

            if (destinationPath != null)
            {
                try
                {
                    LogService.Log("Extracting selected files...");
                    await WadExtractionService.ExtractNodeAsync(selectedNode, destinationPath);
                    LogService.LogInteractiveSuccess($"Successfully extracted {selectedNode.Name} to {destinationPath}", destinationPath);
                }
                catch (Exception ex)
                {   
                    LogService.LogError(ex, $"Failed to extract '{selectedNode.Name}'.");
                    CustomMessageBoxService.ShowError("Error", $"An error occurred during extraction: {ex.Message}", Window.GetWindow(this));
                }
            }
        }

        private async void ViewChanges_Click(object sender, RoutedEventArgs e)
        {
            if (FileTreeView.SelectedItem is not FileSystemNodeModel { ChunkDiff: not null } selectedNode) return;

            string backupDir = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(selectedNode.SourceWadPath)));
            string oldChunksPath = Path.Combine(backupDir, "wad_chunks", "old");
            string newChunksPath = Path.Combine(backupDir, "wad_chunks", "new");

            await DiffViewService.ShowWadDiffAsync(selectedNode.ChunkDiff, oldChunksPath, newChunksPath, Window.GetWindow(this));
        }

        private void PinSelected_Click(object sender, RoutedEventArgs e)
        {
            if (FileTreeView.SelectedItem is FileSystemNodeModel selectedNode && FilePreviewer != null)
            {
                var existingNormalPin = FilePreviewer.ViewModel.PinnedFiles.FirstOrDefault(p => p.Node == selectedNode && !p.IsDetailsTab);

                if (existingNormalPin != null)
                {
                    FilePreviewer.ViewModel.SelectedFile = existingNormalPin;
                }
                else
                {
                    var newPin = new PinnedFileModel(selectedNode);
                    FilePreviewer.ViewModel.PinnedFiles.Add(newPin);
                    FilePreviewer.ViewModel.SelectedFile = newPin;
                }
            }
        }

        private void ContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (FileTreeView.SelectedItem is not FileSystemNodeModel selectedNode) return;

            if (PinMenuItem is not null)
            {
                PinMenuItem.IsEnabled = selectedNode.Type != NodeType.RealDirectory && selectedNode.Type != NodeType.VirtualDirectory;
            }

            if (ViewChangesMenuItem is not null)
            {
                ViewChangesMenuItem.IsEnabled = selectedNode.Status == DiffStatus.Modified;
            }
        }

        private void TreeViewItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (SafeVisualUpwardSearch(e.OriginalSource as DependencyObject) is TreeViewItem treeViewItem)
            {
                treeViewItem.IsSelected = true;
                e.Handled = true;
            }
        }

        private static TreeViewItem SafeVisualUpwardSearch(DependencyObject source)
        {
            while (source != null && !(source is TreeViewItem))
            {
                if (source is Visual || source is System.Windows.Media.Media3D.Visual3D)
                {
                    source = VisualTreeHelper.GetParent(source);
                }
                else
                {
                    source = LogicalTreeHelper.GetParent(source);
                }
            }
            return source as TreeViewItem;
        }

        private async void SelectLolDirButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new CommonOpenFileDialog
            {
                IsFolderPicker = true,
                Title = "Select the League of Legends Directory"
            };

            if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                string selectedDirectory = dialog.FileName;
                if (Directory.Exists(selectedDirectory))
                {
                    AppSettings.LolDirectory = selectedDirectory;
                    await ReloadTreeAsync();
                }
                else
                {
                    CustomMessageBoxService.ShowError("Error", "Invalid directory selected.", Window.GetWindow(this));
                }
            }
        }

        private async Task BuildWadTreeAsync(string rootPath)
        {
            _currentRootPath = rootPath;
            NoDirectoryMessage.Visibility = Visibility.Collapsed;
            FileTreeView.Visibility = Visibility.Collapsed;
            LoadingIndicator.Visibility = Visibility.Visible;

            try
            {
                await HashResolverService.LoadHashesAsync();
                await HashResolverService.LoadBinHashesAsync();

                RootNodes.Clear();

                string gamePath = Path.Combine(rootPath, "Game");
                if (Directory.Exists(gamePath))
                {
                    var gameNode = new FileSystemNodeModel(gamePath);
                    RootNodes.Add(gameNode);
                    await LoadAllChildren(gameNode);
                }

                string pluginsPath = Path.Combine(rootPath, "Plugins");
                if (Directory.Exists(pluginsPath))
                {
                    var pluginsNode = new FileSystemNodeModel(pluginsPath);
                    RootNodes.Add(pluginsNode);
                    await LoadAllChildren(pluginsNode);
                }

                // Prune empty directories after loading
                for (int i = RootNodes.Count - 1; i >= 0; i--)
                {
                    if (!PruneEmptyDirectories(RootNodes[i]))
                    {
                        RootNodes.RemoveAt(i);
                    }
                }

                if (RootNodes.Count == 0)
                {
                    CustomMessageBoxService.ShowError("Error", "Could not find any WAD files in 'Game' or 'Plugins' subdirectories.", Window.GetWindow(this));
                    NoDirectoryMessage.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                LogService.LogError(ex, "Failed to build initial tree.");
                CustomMessageBoxService.ShowError("Error", "Could not load the directory. Please check the logs.", Window.GetWindow(this));
                NoDirectoryMessage.Visibility = Visibility.Visible;
            }
            finally
            {
                LoadingIndicator.Visibility = Visibility.Collapsed;
                FileTreeView.Visibility = Visibility.Visible;
                Toolbar.Visibility = Visibility.Visible;
                ToolbarSeparator.Visibility = Visibility.Visible;
            }
        }

        private async Task BuildDirectoryTreeAsync(string rootPath)
        {
            _currentRootPath = rootPath;
            NoDirectoryMessage.Visibility = Visibility.Collapsed;
            FileTreeView.Visibility = Visibility.Collapsed;
            LoadingIndicator.Visibility = Visibility.Visible;

            try
            {
                RootNodes.Clear();
                var nodes = await WadNodeLoaderService.LoadDirectoryAsync(rootPath);
                foreach (var node in nodes)
                {
                    RootNodes.Add(node);
                }
            }
            catch (Exception ex)
            {
                LogService.LogError(ex, "Failed to build directory tree.");
                CustomMessageBoxService.ShowError("Error", "Could not load the directory. Please check the logs.", Window.GetWindow(this));
                NoDirectoryMessage.Visibility = Visibility.Visible;
            }
            finally
            {
                LoadingIndicator.Visibility = Visibility.Collapsed;
                FileTreeView.Visibility = Visibility.Visible;
                Toolbar.Visibility = Visibility.Visible;
                ToolbarSeparator.Visibility = Visibility.Visible;
            }
        }

        private bool PruneEmptyDirectories(FileSystemNodeModel node)
        {
            if (node.Type != NodeType.RealDirectory)
            {
                return true; // Keep files
            }

            // Recursively prune children
            for (int i = node.Children.Count - 1; i >= 0; i--)
            {
                if (!PruneEmptyDirectories(node.Children[i]))
                {
                    node.Children.RemoveAt(i);
                }
            }

            // If directory is now empty, it should be pruned
            return node.Children.Any();
        }

        private async Task LoadAllChildren(FileSystemNodeModel node)
        {
            if (node.Children.Count == 1 && node.Children[0].Name == "Loading...")
            {
                node.Children.Clear();
            }

            if (node.Type == NodeType.WadFile)
            {
                var children = await WadNodeLoaderService.LoadChildrenAsync(node);
                foreach (var child in children)
                {
                    node.Children.Add(child);
                }
                return;
            }

            if (node.Type == NodeType.RealDirectory)
            {
                try
                {
                    // Recurse into subdirectories
                    var directories = Directory.GetDirectories(node.FullPath);
                    foreach (var dir in directories.OrderBy(d => d))
                    {
                        var childNode = new FileSystemNodeModel(dir);
                        node.Children.Add(childNode);
                        await LoadAllChildren(childNode);
                    }

                    // Process files
                    var files = Directory.GetFiles(node.FullPath);
                    foreach (var file in files.OrderBy(f => f))
                    {
                        string lowerFile = file.ToLowerInvariant();

                        // Determine if the file is a WAD file we want to keep
                        bool keepFile = false;
                        if (lowerFile.EndsWith(".wad.client"))
                        {
                            if (node.FullPath.StartsWith(Path.Combine(_currentRootPath, "Game")))
                                keepFile = true;
                        }
                        else if (lowerFile.EndsWith(".wad"))
                        {
                            if (node.FullPath.StartsWith(Path.Combine(_currentRootPath, "Plugins")))
                                keepFile = true;
                        }

                        if (keepFile)
                        {
                            var childNode = new FileSystemNodeModel(file);
                            node.Children.Add(childNode);
                            await LoadAllChildren(childNode); // Eager load WAD content
                        }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    LogService.LogWarning($"Access denied to: {node.FullPath}");
                }
            }
        }

        private void FileTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            FileSelected?.Invoke(this, e);
        }

        private void Toolbar_SearchTextChanged(object sender, RoutedEventArgs e)
        {
            _searchTimer.Stop();
            _searchTimer.Start();
        }

        private void Toolbar_CollapseToContainerClicked(object sender, RoutedEventArgs e)
        {
            var selectedNode = FileTreeView.SelectedItem as FileSystemNodeModel;
            if (selectedNode == null) return;

            var path = FindNodePath(RootNodes, selectedNode);
            if (path == null) return;

            FileSystemNodeModel containerNode = null;

            // First, try to find a traditional WAD container
            for (int i = path.Count - 1; i >= 0; i--)
            {
                if (path[i].Type == NodeType.WadFile)
                {
                    containerNode = path[i];
                    break;
                }
            }

            // If not found, we might be in a backup view. The container is the root of the backup.
            if (containerNode == null && path.Count > 0)
            {
                var rootNode = path[0];
                bool isBackupRoot = rootNode.Children.Any(c => c.Name.StartsWith("["));
                if (isBackupRoot)
                {
                    containerNode = rootNode;
                }
            }

            if (containerNode != null)
            {
                // Collapse all children recursively
                foreach (var child in containerNode.Children)
                {
                    CollapseAll(child);
                }

                // Now, collapse the container itself
                containerNode.IsExpanded = false;

                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    SelectAndFocusNode(containerNode, false);
                }), DispatcherPriority.ContextIdle);
            }
        }
        
        private void CollapseAll(FileSystemNodeModel node)
        {
            node.IsExpanded = false;
            if (node.Children == null) return;
            foreach (var child in node.Children)
            {
                CollapseAll(child);
            }
        }

        private async void SearchTimer_Tick(object sender, EventArgs e)
        {
            _searchTimer.Stop();
            string searchText = Toolbar.SearchText;

            var nodeToSelect = await WadSearchBoxService.PerformSearchAsync(searchText, RootNodes, LoadAllChildren);

            if (nodeToSelect != null)
            {
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    SelectAndFocusNode(nodeToSelect, false); // Pass false to prevent focus stealing
                }), DispatcherPriority.ContextIdle);
            }
            else
            {
                // If service returns null, it was a filter operation or an empty search.
                // The service already handled the filtering, so we just need to restore selection if possible.
                var selectedNode = FileTreeView.SelectedItem as FileSystemNodeModel;
                if (selectedNode != null && string.IsNullOrEmpty(searchText))
                {
                    _ = Dispatcher.BeginInvoke(new Action(() =>
                    {
                        SelectAndFocusNode(selectedNode);
                    }), DispatcherPriority.ContextIdle);
                }
            }
        }

        private void SelectAndFocusNode(FileSystemNodeModel node, bool focusNode = true)
        {
            var path = FindNodePath(RootNodes, node);
            if (path == null) return;

            var container = (ItemsControl)FileTreeView;
            TreeViewItem itemContainer = null;

            // Expand all parent nodes.
            foreach (var parentNode in path)
            {
                if (parentNode == node) break;

                itemContainer = (TreeViewItem)container.ItemContainerGenerator.ContainerFromItem(parentNode);
                if (itemContainer == null)
                {
                    container.UpdateLayout(); // Force the container to be generated
                    itemContainer = (TreeViewItem)container.ItemContainerGenerator.ContainerFromItem(parentNode);
                }

                if (itemContainer == null) return; // Could not create container

                // Force expansion on both the model and the UI item to be safe.
                parentNode.IsExpanded = true;
                if (!itemContainer.IsExpanded)
                {
                    itemContainer.IsExpanded = true;
                }
                container = itemContainer;
            }

            // Select the final node.
            itemContainer = (TreeViewItem)container.ItemContainerGenerator.ContainerFromItem(node);
            if (itemContainer == null)
            {
                container.UpdateLayout();
                itemContainer = (TreeViewItem)container.ItemContainerGenerator.ContainerFromItem(node);
            }

            if (itemContainer != null)
            {
                itemContainer.BringIntoView();
                itemContainer.IsSelected = true; // Always select the item.
                if (focusNode)
                {
                    itemContainer.Focus(); // Only set focus if requested.
                }
            }
        }

        private List<FileSystemNodeModel> FindNodePath(IEnumerable<FileSystemNodeModel> nodes, FileSystemNodeModel nodeToFind)
        {
            foreach (var n in nodes)
            {
                if (n == nodeToFind)
                {
                    return new List<FileSystemNodeModel> { n };
                }

                if (n.Children != null)
                {
                    var path = FindNodePath(n.Children, nodeToFind);
                    if (path != null)
                    {
                        path.Insert(0, n);
                        return path;
                    }
                }
            }
            return null;
        }
    }
}
