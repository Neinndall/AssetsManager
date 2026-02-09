using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;
using Microsoft.WindowsAPICodePack.Dialogs;
using AssetsManager.Utils;
using AssetsManager.Services.Core;
using AssetsManager.Services.Explorer;
using AssetsManager.Services.Audio;
using AssetsManager.Services.Explorer.Tree;
using AssetsManager.Services.Hashes;
using AssetsManager.Views.Models.Audio;
using AssetsManager.Views.Models.Explorer;
using AssetsManager.Views.Models.Wad;
using AssetsManager.Views.Models.Shared;

namespace AssetsManager.Views.Controls.Explorer
{
    public partial class FileExplorerControl : UserControl
    {
        public event RoutedPropertyChangedEventHandler<object> FileSelected;
        public event RoutedPropertyChangedEventHandler<bool> BreadcrumbVisibilityChanged;
        
        public FilePreviewerControl FilePreviewer { get; set; }

        public MenuItem PinMenuItem => (this.FindResource("ExplorerContextMenu") as ContextMenu)?.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "PinMenuItem");
        public MenuItem AddToFavoritesMenuItem => (this.FindResource("ExplorerContextMenu") as ContextMenu)?.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "AddToFavoritesMenuItem");
        public MenuItem ViewChangesMenuItem => (this.FindResource("ExplorerContextMenu") as ContextMenu)?.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "ViewChangesMenuItem");
        public MenuItem ExtractMenuItem => (this.FindResource("ExplorerContextMenu") as ContextMenu)?.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "ExtractMenuItem");
        public MenuItem SaveMenuItem => (this.FindResource("ExplorerContextMenu") as ContextMenu)?.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "SaveMenuItem");
        public MenuItem AddToImageMergerMenuItem => (this.FindResource("ExplorerContextMenu") as ContextMenu)?.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "AddToImageMergerMenuItem");

        // Injected Services
        public LogService LogService { get; set; }
        public CustomMessageBoxService CustomMessageBoxService { get; set; }
        public WadExtractionService WadExtractionService { get; set; }
        public WadSavingService WadSavingService { get; set; }
        public WadSearchBoxService WadSearchBoxService { get; set; }
        public DiffViewService DiffViewService { get; set; }
        public DirectoriesCreator DirectoriesCreator { get; set; }
        public AppSettings AppSettings { get; set; }
        public TreeBuilderService TreeBuilderService { get; set; }
        public TreeUIManager TreeUIManager { get; set; }
        public FavoritesManager FavoritesManager { get; set; }
        public AudioBankService AudioBankService { get; set; }
        public AudioBankLinkerService AudioBankLinkerService { get; set; }
        public HashResolverService HashResolverService { get; set; }
        public TaskCancellationManager TaskCancellationManager { get; set; }
        public ImageMergerService ImageMergerService { get; set; }
        public ProgressUIManager ProgressUIManager { get; set; }

        public string NewLolPath { get; set; }
        public string OldLolPath { get; set; }

        public ObservableRangeCollection<FileSystemNodeModel> RootNodes => _viewModel.RootNodes;

        private readonly FileExplorerModel _viewModel;
        
        private readonly DispatcherTimer _searchTimer;
        private string _currentRootPath;
        private string _backupJsonPath;

        public FileExplorerControl()
        {
            InitializeComponent();
            
            _viewModel = new FileExplorerModel();
            DataContext = _viewModel;
            
            this.Loaded += FileExplorerControl_Loaded;

            _searchTimer = new DispatcherTimer();
            _searchTimer.Interval = TimeSpan.FromMilliseconds(300);
            _searchTimer.Tick += SearchTimer_Tick;
        }

        public void CleanupResources()
        {
            TaskCancellationManager.CancelCurrentOperation(true, "Cancelling tree building...");

            // 1. Detener el timer PRIMERO
            if (_searchTimer != null)
            {
                _searchTimer.Stop();
                _searchTimer.Tick -= SearchTimer_Tick;
            }

            // 2. Desuscribir eventos del Toolbar
            if (Toolbar != null)
            {
                Toolbar.SearchTextChanged -= Toolbar_SearchTextChanged;
                Toolbar.CollapseToContainerClicked -= Toolbar_CollapseToContainerClicked;
                Toolbar.LoadComparisonClicked -= Toolbar_LoadComparisonClicked;
                Toolbar.SwitchModeClicked -= Toolbar_SwitchModeClicked;
                Toolbar.BreadcrumbVisibilityChanged -= Toolbar_BreadcrumbVisibilityChanged;
                Toolbar.FavoritesVisibilityChanged -= Toolbar_FavoritesVisibilityChanged;
                Toolbar.SortStateChanged -= Toolbar_SortStateChanged;
                Toolbar.ViewModeChanged -= Toolbar_ViewModeChanged;
                Toolbar.ImageMergerClicked -= Toolbar_ImageMergerClicked;
            }

            // 3. Desuscribir eventos propios
            this.Loaded -= FileExplorerControl_Loaded;

            // 4. Limpiar el TreeView
            if (FileTreeView != null)
            {
                FileTreeView.SelectedItemChanged -= FileTreeView_SelectedItemChanged;
                FileTreeView.ItemsSource = null; // ← IMPORTANTE: Desvincular binding
            }

            // 5. CRÍTICO: Limpiar recursivamente todos los nodos
            if (_viewModel.RootNodes != null)
            {
                foreach (var rootNode in _viewModel.RootNodes.ToList())
                {
                    rootNode.Dispose(); // ← Usa el nuevo método Dispose
                }
                _viewModel.RootNodes.Clear();
            }

            // 6. Desuscribir de favoritos
            if (FavoritesManager != null)
            {
                FavoritesManager.Favorites.CollectionChanged -= Favorites_CollectionChanged;
            }

            // 7. Romper referencias cruzadas
            FilePreviewer = null; // Remove reference

            // 8. Limpiar paths
            _currentRootPath = null;
        }

        private async void FileExplorerControl_Loaded(object sender, RoutedEventArgs e)
        {
            // First, do the synchronous checks to decide the initial UI state.
            bool shouldLoadWadTree = _viewModel.IsWadMode && !string.IsNullOrEmpty(AppSettings.LolPbeDirectory) && Directory.Exists(AppSettings.LolPbeDirectory);
            bool shouldLoadDirTree = !_viewModel.IsWadMode && !string.IsNullOrEmpty(DirectoriesCreator.AssetsDownloadedPath) && Directory.Exists(DirectoriesCreator.AssetsDownloadedPath);

            if (shouldLoadWadTree || shouldLoadDirTree)
            {
                // Start with Hashes since it's the first async operation
                _viewModel.SetLoadingState(ExplorerLoadingState.LoadingHashes);
            }
            else
            {
                // If we are not going to load, show the correct placeholder immediately.
                _viewModel.UpdateEmptyState(_viewModel.IsWadMode);
            }

            // Now, perform the async hash loading.
            await HashResolverService.LoadAllHashesAsync();

            // Bind Favorites
            if (FavoritesManager != null)
            {
                FavoritesListView.ItemsSource = FavoritesManager.Favorites;
                
                // Track changes to update visibility
                FavoritesManager.Favorites.CollectionChanged += Favorites_CollectionChanged;
                _viewModel.HasFavorites = FavoritesManager.Favorites.Count > 0;
            }

            // Setup toolbar events (can be done regardless of loading)
            Toolbar.SearchTextChanged += Toolbar_SearchTextChanged;
            Toolbar.CollapseToContainerClicked += Toolbar_CollapseToContainerClicked;
            Toolbar.LoadComparisonClicked += Toolbar_LoadComparisonClicked;
            Toolbar.SwitchModeClicked += Toolbar_SwitchModeClicked;
            Toolbar.BreadcrumbVisibilityChanged += Toolbar_BreadcrumbVisibilityChanged;
            Toolbar.FavoritesVisibilityChanged += Toolbar_FavoritesVisibilityChanged;
            Toolbar.SortStateChanged += Toolbar_SortStateChanged;
            Toolbar.ViewModeChanged += Toolbar_ViewModeChanged;
            Toolbar.ImageMergerClicked += Toolbar_ImageMergerClicked;

            Toolbar.SetWadMode(_viewModel.IsWadMode);
            
            // Finally, trigger the tree build if needed.
            if (shouldLoadWadTree)
            {
                await BuildWadTreeAsync(AppSettings.LolPbeDirectory);
            }
            else if (shouldLoadDirTree)
            {
                await BuildDirectoryTreeAsync(DirectoriesCreator.AssetsDownloadedPath);
            }
        }

        private void Favorites_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            _viewModel.HasFavorites = FavoritesManager.Favorites.Count > 0;
        }

        private void Toolbar_ViewModeChanged(object sender, RoutedPropertyChangedEventArgs<bool> e)
        {
            if (FilePreviewer != null)
            {
                FilePreviewer.SetViewMode(e.NewValue);
            }
        }

        public void SetToolbarViewMode(bool isGridMode)
        {
            Toolbar.SetViewMode(isGridMode);
        }

        private async void Toolbar_SwitchModeClicked(object sender, RoutedEventArgs e)
        {
            _viewModel.IsWadMode = !_viewModel.IsWadMode;
            Toolbar.SetWadMode(_viewModel.IsWadMode);
            // Mode switched, we keep the current view mode (Grid/Preview) preference.
            
            if (FilePreviewer != null)
            {
                await FilePreviewer.ResetToDefaultState();
            }
            await ReloadTreeAsync();
        }

        private void Toolbar_BreadcrumbVisibilityChanged(object sender, RoutedPropertyChangedEventArgs<bool> e)
        {
            BreadcrumbVisibilityChanged?.Invoke(this, e);
        }

        private void Toolbar_FavoritesVisibilityChanged(object sender, RoutedPropertyChangedEventArgs<bool> e)
        {
            _viewModel.IsFavoritesEnabled = e.NewValue;
        }

        private async void Toolbar_SortStateChanged(object sender, RoutedPropertyChangedEventArgs<bool> e)
        {
            _viewModel.IsSortingEnabled = e.NewValue;
            if (_viewModel.IsBackupMode)
            {
                await BuildTreeFromBackupAsync(_backupJsonPath);
            }
        }

        private void Toolbar_ImageMergerClicked(object sender, RoutedEventArgs e)
        {
            ImageMergerService.ShowWindow();
        }

        public async Task ReloadTreeAsync()
        {
            if (_viewModel.IsWadMode)
            {
                if (!string.IsNullOrEmpty(AppSettings.LolPbeDirectory) && Directory.Exists(AppSettings.LolPbeDirectory))
                {
                    await BuildWadTreeAsync(AppSettings.LolPbeDirectory);
                }
                else
                {
                    _viewModel.UpdateEmptyState(true);
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
                    _viewModel.UpdateEmptyState(false);
                }
            }
        }

        private async void Toolbar_LoadComparisonClicked(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new CommonOpenFileDialog
            {
                Title = "Select a backup file",
                Filters = { new CommonFileDialogFilter("WAD Comparison JSON", "wadcomparison.json"), new CommonFileDialogFilter("All files", "*.*") },
                InitialDirectory = DirectoriesCreator.WadComparisonSavePath
            };

            if (openFileDialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                if (FilePreviewer != null)
                {
                    await FilePreviewer.ResetToDefaultState();
                }
                await BuildTreeFromBackupAsync(openFileDialog.FileName);
            }
        }

        private async Task ExecuteTreeBuildInternalAsync(
            Func<CancellationToken, Task<ObservableRangeCollection<FileSystemNodeModel>>> buildFunc, 
            ExplorerLoadingState loadingState, 
            string errorMsg, 
            bool isBackupMode,
            Action<ObservableRangeCollection<FileSystemNodeModel>> onSuccess = null)
        {
            _viewModel.IsBackupMode = isBackupMode;
            var cancellationToken = TaskCancellationManager.PrepareNewOperation();

            _viewModel.SetLoadingState(loadingState);
            Toolbar.IsSortButtonVisible = isBackupMode;

            try
            {
                var newNodes = await buildFunc(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                _viewModel.RootNodes.ReplaceRange(newNodes);

                onSuccess?.Invoke(newNodes);

                if (_viewModel.RootNodes.Count == 0 && !isBackupMode)
                {
                    CustomMessageBoxService.ShowError("Error", "No items found in the selected location.", Window.GetWindow(this));
                    _viewModel.IsEmptyState = true;
                }

                TaskCancellationManager.CompleteCurrentOperation();
            }
            catch (OperationCanceledException)
            {
                LogService.LogWarning("The tree build was cancelled.");
                await Task.Delay(1500);
                TaskCancellationManager.CompleteCurrentOperation();
            }
            catch (Exception ex)
            {
                LogService.LogError(ex, errorMsg);
                CustomMessageBoxService.ShowError("Error", $"{errorMsg} Please check the logs.", Window.GetWindow(this));
                _viewModel.IsEmptyState = true;
            }
            finally
            {
                _viewModel.IsBusy = false;
                if (!_viewModel.IsEmptyState)
                {
                    _viewModel.IsTreeReady = true;
                }
            }
        }

        private async Task BuildWadTreeAsync(string rootPath)
        {
            _currentRootPath = rootPath;
            NewLolPath = null;
            OldLolPath = null;

            await ExecuteTreeBuildInternalAsync(
                async ct => await TreeBuilderService.BuildWadTreeAsync(rootPath, ct),
                ExplorerLoadingState.LoadingWads,
                "Failed to build WAD tree.",
                false);
        }

        private async Task BuildDirectoryTreeAsync(string rootPath)
        {
            _currentRootPath = rootPath;
            NewLolPath = null;
            OldLolPath = null;

            await ExecuteTreeBuildInternalAsync(
                async ct => await TreeBuilderService.BuildDirectoryTreeAsync(rootPath, ct),
                ExplorerLoadingState.ExploringDirectory,
                "Failed to build directory tree.",
                false);
        }

        private async Task BuildTreeFromBackupAsync(string jsonPath)
        {
            _backupJsonPath = jsonPath;

            await ExecuteTreeBuildInternalAsync(
                async ct => 
                {
                    var (nodes, newPath, oldPath) = await TreeBuilderService.BuildTreeFromBackupAsync(jsonPath, _viewModel.IsSortingEnabled, ct);
                    NewLolPath = newPath;
                    OldLolPath = oldPath;
                    return nodes;
                },
                ExplorerLoadingState.LoadingBackup,
                "Failed to build tree from backup.",
                true);
        }

        private async void ExtractSelected_Click(object sender, RoutedEventArgs e)
        {
            if (WadExtractionService == null)
            {
                CustomMessageBoxService.ShowError("Error", "Extraction Service is not available.", Window.GetWindow(this));
                return;
            }

            var selectedNodes = TreeUIManager.GetSelectedNodes(_viewModel.RootNodes, FileTreeView.SelectedItem as FileSystemNodeModel);
            if (selectedNodes.Count == 0)
            {
                CustomMessageBoxService.ShowInfo("Info", "Please select one or more files or folders to extract.", Window.GetWindow(this));
                return;
            }

            string destinationPath = null;

            if (!string.IsNullOrEmpty(AppSettings.DefaultExtractedSelectDirectory) && Directory.Exists(AppSettings.DefaultExtractedSelectDirectory))
            {
                destinationPath = AppSettings.DefaultExtractedSelectDirectory;
            }
            else
            {
                var dialog = new CommonOpenFileDialog { IsFolderPicker = true, Title = "Select destination folder" };
                if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
                {
                    destinationPath = dialog.FileName;
                }
            }

            if (destinationPath != null)
            {
                ExtractMenuItem.IsEnabled = false;
                SaveMenuItem.IsEnabled = false;
                CancellationToken cancellationToken = TaskCancellationManager.PrepareNewOperation(); // Get new token for this operation

                try
                {
                    // Show immediate activity
                    ProgressUIManager?.OnExtractionStarted(this, ("Extracting Assets...", 0));

                    // Calculate total files for accurate progress
                    int totalFiles = await WadExtractionService.CalculateTotalAsync(selectedNodes, cancellationToken);
                    
                    // Update with real total
                    ProgressUIManager?.OnExtractionStarted(this, ("Extracting Assets...", totalFiles));

                    int processedCount = 0;
                    foreach (var node in selectedNodes)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        
                        await WadExtractionService.ExtractNodeAsync(node, destinationPath, cancellationToken, (fileName) => 
                        {
                            processedCount++;
                            ProgressUIManager?.OnExtractionProgressChanged(processedCount, totalFiles, fileName);
                        });
                    }

                    if (selectedNodes.Count == 1)
                    {
                        var node = selectedNodes[0];
                        string logPath = destinationPath;
                        if (node.Type == NodeType.RealDirectory || node.Type == NodeType.VirtualDirectory || node.Type == NodeType.WadFile || node.Type == NodeType.AudioEvent)
                        {
                            logPath = Path.Combine(destinationPath, node.Name);
                        }
                        LogService.LogInteractiveSuccess($"Successfully extracted {node.Name}.", logPath, node.Name);
                    }
                    else
                    {
                        string folderName = Path.GetFileName(destinationPath);
                        LogService.LogInteractiveSuccess($"Successfully extracted {selectedNodes.Count} selected items in {folderName}.", destinationPath, folderName);
                    }
                    
                    TaskCancellationManager.CompleteCurrentOperation();
                }
                catch (OperationCanceledException)
                {
                    LogService.LogWarning("Extraction was cancelled by the user.");
                }
                catch (Exception ex)
                {
                    LogService.LogError(ex, $"Failed during extraction process.");
                    CustomMessageBoxService.ShowError("Error", $"An error occurred during extraction: {ex.Message}", Window.GetWindow(this));
                }
                finally
                {
                    ProgressUIManager?.OnExtractionCompleted();
                    // TaskCancellationManager is a singleton, its internal CancellationTokenSource is disposed by PrepareNewOperation()
                    ExtractMenuItem.IsEnabled = true;
                    SaveMenuItem.IsEnabled = true;
                }
            }
        }

        private async void SaveSelected_Click(object sender, RoutedEventArgs e)
        {
            if (WadSavingService == null)
            {
                CustomMessageBoxService.ShowError("Error", "Wad Saving Service is not available.", Window.GetWindow(this));
                return;
            }

            var selectedNodes = TreeUIManager.GetSelectedNodes(_viewModel.RootNodes, FileTreeView.SelectedItem as FileSystemNodeModel);
            if (selectedNodes.Count == 0)
            {
                CustomMessageBoxService.ShowInfo("Info", "Please select one or more files or folders to save.", Window.GetWindow(this));
                return;
            }

            string destinationPath = null;

            if (!string.IsNullOrEmpty(AppSettings.DefaultExtractedSelectDirectory) && Directory.Exists(AppSettings.DefaultExtractedSelectDirectory))
            {
                destinationPath = AppSettings.DefaultExtractedSelectDirectory;
            }
            else
            {
                var dialog = new CommonOpenFileDialog { IsFolderPicker = true, Title = "Select destination folder" };
                if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
                {
                    destinationPath = dialog.FileName;
                }
            }

            if (destinationPath != null)
            {
                ExtractMenuItem.IsEnabled = false;
                SaveMenuItem.IsEnabled = false;
                CancellationToken cancellationToken = TaskCancellationManager.PrepareNewOperation(); // Get new token for this operation

                try
                {
                    // Show immediate activity
                    ProgressUIManager?.OnSavingStarted(0);

                    int totalFiles = await WadSavingService.CalculateTotalAsync(selectedNodes, _viewModel.RootNodes, _currentRootPath, cancellationToken);
                    
                    // Update with real total
                    ProgressUIManager?.OnSavingStarted(totalFiles);

                    string singleSavedPath = null;
                    string singleDisplayName = null;

                    int processedCount = 0;
                    foreach (var node in selectedNodes)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var savedFiles = new List<string>();
                        await WadSavingService.ProcessAndSaveAsync(node, destinationPath, _viewModel.RootNodes, _currentRootPath, cancellationToken, (path) => 
                        {
                            processedCount++;
                            ProgressUIManager?.OnSavingProgressChanged(processedCount, totalFiles, Path.GetFileName(path));
                            savedFiles.Add(path);
                        });

                        if (selectedNodes.Count == 1 && savedFiles.Count > 0)
                        {
                            singleSavedPath = destinationPath;
                            singleDisplayName = node.Name;

                            if (node.Type == NodeType.SoundBank)
                            {
                                singleSavedPath = Path.Combine(destinationPath, Path.GetFileNameWithoutExtension(node.Name));
                            }
                            else if (node.Type == NodeType.RealDirectory || node.Type == NodeType.VirtualDirectory || node.Type == NodeType.WadFile || node.Type == NodeType.AudioEvent)
                            {
                                singleSavedPath = Path.Combine(destinationPath, node.Name);
                            }

                            if (savedFiles.Count == 1)
                            {
                                singleSavedPath = savedFiles.First();
                                singleDisplayName = Path.GetFileName(singleSavedPath);
                            }
                        }
                    }

                    if (selectedNodes.Count == 1)
                    {
                        if (singleSavedPath != null)
                        {
                            LogService.LogInteractiveSuccess($"Successfully saved {singleDisplayName}.", singleSavedPath, singleDisplayName);
                        }
                    }
                    else
                    {
                        string folderName = Path.GetFileName(destinationPath);
                        LogService.LogInteractiveSuccess($"Successfully saved {selectedNodes.Count} selected items in {folderName}.", destinationPath, folderName);
                    }

                    TaskCancellationManager.CompleteCurrentOperation();
                }
                catch (OperationCanceledException)
                {
                    LogService.LogWarning("Save operation was cancelled by the user.");
                }
                catch (Exception ex)
                {
                    LogService.LogError(ex, $"Failed during save process.");
                    CustomMessageBoxService.ShowError("Error", $"An error occurred during save: {ex.Message}", Window.GetWindow(this));
                }
                finally
                {
                    ProgressUIManager?.OnSavingCompleted();
                    // TaskCancellationManager is a singleton, its internal CancellationTokenSource is disposed by PrepareNewOperation()
                    ExtractMenuItem.IsEnabled = true;
                    SaveMenuItem.IsEnabled = true;
                }
            }
        }

        private async void ViewChanges_Click(object sender, RoutedEventArgs e)
        {
            if (FileTreeView.SelectedItem is not FileSystemNodeModel { ChunkDiff: not null } selectedNode) return;

            string oldPbePath = this.OldLolPath;
            string newPbePath = this.NewLolPath;

            if (_viewModel.IsBackupMode)
            {
                oldPbePath = null;
                newPbePath = null;
            }

            await DiffViewService.ShowWadDiffAsync(selectedNode.ChunkDiff, oldPbePath, newPbePath, Window.GetWindow(this), _viewModel.IsBackupMode ? _backupJsonPath : null);
        }

        private void PinSelected_Click(object sender, RoutedEventArgs e)
        {
            if (FileTreeView.SelectedItem is FileSystemNodeModel selectedNode && FilePreviewer != null)
            {
                var existingNormalPin = FilePreviewer.ViewModel.PinnedFilesManager.PinnedFiles.FirstOrDefault(p => p.Node == selectedNode);

                if (existingNormalPin != null)
                {
                    FilePreviewer.ViewModel.PinnedFilesManager.SelectedFile = existingNormalPin;
                }
                else
                {
                    var newPin = new PinnedFileModel(selectedNode);
                    FilePreviewer.ViewModel.PinnedFilesManager.PinnedFiles.Add(newPin);
                    FilePreviewer.ViewModel.PinnedFilesManager.SelectedFile = newPin;
                }
            }
        }

        private void AddToFavorites_Click(object sender, RoutedEventArgs e)
        {
            if (FileTreeView.SelectedItem is FileSystemNodeModel selectedNode)
            {
                var path = TreeUIManager.FindNodePath(_viewModel.RootNodes, selectedNode);
                if (path != null)
                {
                    // Filter out any "Loading..." nodes that might be in the path
                    // And join by '/'
                    var validNodes = path.Where(n => n.Name != "Loading...");
                    string fullPath = string.Join("/", validNodes.Select(n => n.Name));
                    
                    FavoritesManager.AddFavorite(fullPath);
                }
            }
        }

        private async void AddToImageMerger_Click(object sender, RoutedEventArgs e)
        {
            var selectedNodes = TreeUIManager.GetSelectedNodes(_viewModel.RootNodes, FileTreeView.SelectedItem as FileSystemNodeModel);
            if (selectedNodes.Count == 0) return;

            int addedCount = 0;
            foreach (var node in selectedNodes)
            {
                // Only process files that are supported images or textures
                if (!(SupportedFileTypes.Images.Contains(node.Extension) || SupportedFileTypes.Textures.Contains(node.Extension)) ||
                    !(node.Type == NodeType.VirtualFile || node.Type == NodeType.RealFile))
                {
                    continue;
                }

                try
                {
                    byte[] data = null;
                    if (node.Type == NodeType.VirtualFile)
                        data = await WadExtractionService.GetVirtualFileBytesAsync(node);
                    else if (node.Type == NodeType.RealFile)
                        data = await File.ReadAllBytesAsync(node.FullPath);

                    if (data == null) continue;

                    BitmapSource bitmap = null;
                    if (SupportedFileTypes.Textures.Contains(node.Extension))
                    {
                        using (var stream = new MemoryStream(data))
                        {
                            bitmap = Utils.TextureUtils.LoadTexture(stream, node.Extension);
                        }
                    }
                    else
                    {
                        using (var stream = new MemoryStream(data))
                        {
                            var bmp = new BitmapImage();
                            bmp.BeginInit();
                            bmp.StreamSource = stream;
                            bmp.CacheOption = BitmapCacheOption.OnLoad;
                            bmp.EndInit();
                            bmp.Freeze();
                            bitmap = bmp;
                        }
                    }

                    if (bitmap != null)
                    {
                        ImageMergerService.AddItem(new ImageMergerItem
                        {
                            Name = node.Name,
                            Path = node.FullPath ?? node.Name,
                            Image = bitmap
                        });
                        addedCount++;
                    }
                }
                catch (Exception ex)
                {
                    LogService.LogError(ex, $"Failed to add image '{node.Name}' to merger.");
                }
            }
        }

        private void RemoveFavorite_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is FavoriteItemModel item)
            {
                FavoritesManager.RemoveFavorite(item);
                e.Handled = true; // Stop bubbling
            }
        }

        private async void FavoritesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FavoritesListView.SelectedItem is FavoriteItemModel item)
            {
                // Deselect immediately so it can be clicked again
                FavoritesListView.SelectedItem = null;

                var node = await WadSearchBoxService.NavigateToPathAsync(item.FullPath, _viewModel.RootNodes, LoadAllChildrenForSearch);

                if (node != null)
                {
                    TreeUIManager.SelectAndFocusNode(FileTreeView, _viewModel.RootNodes, node, true);
                }
                else
                {
                    LogService.LogWarning($"Favorite node not found: {item.FullPath}");
                    CustomMessageBoxService.ShowInfo("Not Found", $"Could not find '{item.DisplayName}' in the current tree.", Window.GetWindow(this));
                }
            }
        }

        private void ContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (FileTreeView.SelectedItem is not FileSystemNodeModel selectedNode) return;

            if (ExtractMenuItem is not null)
            {
                ExtractMenuItem.IsEnabled = _viewModel.IsWadMode;
            }

            if (SaveMenuItem is not null)
            {
                SaveMenuItem.IsEnabled = _viewModel.IsWadMode;
            }

            if (PinMenuItem is not null)
            {
                PinMenuItem.IsEnabled = selectedNode.Type != NodeType.RealDirectory && selectedNode.Type != NodeType.VirtualDirectory && selectedNode.Type != NodeType.WadFile;
            }

            if (AddToFavoritesMenuItem is not null)
            {
                // Favorites are only supported in pure WAD Mode (not Backup, not Directory)
                 AddToFavoritesMenuItem.IsEnabled = _viewModel.IsWadMode && !_viewModel.IsBackupMode; 
            }

            if (ViewChangesMenuItem is not null)
            {
                ViewChangesMenuItem.IsEnabled = selectedNode.Status == DiffStatus.Modified;
            }

            if (AddToImageMergerMenuItem is not null)
            {
                AddToImageMergerMenuItem.IsEnabled = (SupportedFileTypes.Images.Contains(selectedNode.Extension) || SupportedFileTypes.Textures.Contains(selectedNode.Extension)) && 
                                                    (selectedNode.Type == NodeType.VirtualFile || selectedNode.Type == NodeType.RealFile);
            }
        }

        private void TreeViewItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (TreeUIManager.SafeVisualUpwardSearch(e.OriginalSource as DependencyObject) is TreeViewItem treeViewItem)
            {
                treeViewItem.IsSelected = true;
                e.Handled = true;
            }
        }

        private async void SelectLolDirButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new CommonOpenFileDialog { IsFolderPicker = true, Title = "Select a league of legends directory" };

            if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                string selectedDirectory = dialog.FileName;
                if (Directory.Exists(selectedDirectory))
                {
                    await BuildWadTreeAsync(selectedDirectory);
                }
                else
                {
                    CustomMessageBoxService.ShowError("Error", "Invalid directory selected.", Window.GetWindow(this));
                }
            }
        }

        private async void FileTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is FileSystemNodeModel selectedNode)
            {
                if (selectedNode.Type == NodeType.SoundBank && selectedNode.Children.Count == 1 && selectedNode.Children[0].Name == "Loading...")
                {
                    await TreeBuilderService.ExpandAudioBankAsync(selectedNode, _viewModel.RootNodes, _currentRootPath, NewLolPath, OldLolPath);
                }
                
                FileSelected?.Invoke(this, e);
            }
        }

        private void Toolbar_SearchTextChanged(object sender, RoutedEventArgs e)
        {
            _searchTimer.Stop();
            _searchTimer.Start();
        }

        private void Toolbar_CollapseToContainerClicked(object sender, RoutedEventArgs e)
        {
            if (FileTreeView.SelectedItem is not FileSystemNodeModel selectedNode) return;

            var path = TreeUIManager.FindNodePath(_viewModel.RootNodes, selectedNode);
            if (path == null) return;

            FileSystemNodeModel containerNode = null;

            for (int i = path.Count - 1; i >= 0; i--)
            {
                if (path[i].Type == NodeType.WadFile)
                {
                    containerNode = path[i];
                    break;
                }
            }

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
                foreach (var child in containerNode.Children)
                {
                    TreeUIManager.CollapseAll(child);
                }
                containerNode.IsExpanded = false;

                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    TreeUIManager.SelectAndFocusNode(FileTreeView, _viewModel.RootNodes, containerNode, false);
                }), DispatcherPriority.ContextIdle);
            }
        }

        private async void SearchTimer_Tick(object sender, EventArgs e)
        {
            _searchTimer.Stop();
            string searchText = Toolbar.SearchText;

            if (FilePreviewer != null)
            {
                FilePreviewer.SetSearchFilter(searchText);
            }

            var nodeToSelect = await WadSearchBoxService.PerformSearchAsync(searchText, _viewModel.RootNodes, LoadAllChildrenForSearch);

            if (nodeToSelect != null)
            {
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    TreeUIManager.SelectAndFocusNode(FileTreeView, _viewModel.RootNodes, nodeToSelect, false);
                }), DispatcherPriority.ContextIdle);
            }
            else
            {
                var selectedNode = FileTreeView.SelectedItem as FileSystemNodeModel;
                if (selectedNode != null && string.IsNullOrEmpty(searchText))
                {
                    _ = Dispatcher.BeginInvoke(new Action(() =>
                    {
                        TreeUIManager.SelectAndFocusNode(FileTreeView, _viewModel.RootNodes, selectedNode);
                    }), DispatcherPriority.ContextIdle);
                }
            }
        }

        private async Task LoadAllChildrenForSearch(FileSystemNodeModel node)
        {
            await TreeBuilderService.EnsureAllChildrenLoadedAsync(node, _currentRootPath);
        }

        public void SelectNode(FileSystemNodeModel node)
        {
            if (node == null) return;
            TreeUIManager.SelectAndFocusNode(FileTreeView, _viewModel.RootNodes, node, false);
        }
    }
}
