using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.WindowsAPICodePack.Dialogs;
using AssetsManager.Utils.Framework;
using AssetsManager.Utils;
using AssetsManager.Services.Core;
using AssetsManager.Services.Monitor;
using AssetsManager.Services.Explorer;
using AssetsManager.Services.Audio;
using AssetsManager.Services.Explorer.Tree;
using AssetsManager.Services.Hashes;
using AssetsManager.Views.Models.Audio;
using AssetsManager.Views.Models.Explorer;
using AssetsManager.Views.Models.Wad;
using AssetsManager.Views.Models.Dialogs;
using AssetsManager.Views.Models.Shared;
using AssetsManager.Views.Models.Monitor;
using AssetsManager.Views.Models.Settings;

namespace AssetsManager.Views.Controls.Explorer
{
    public partial class FileExplorerControl : UserControl
    {
        public FilePreviewerControl FilePreviewer { get; set; }

        public MenuItem PinMenuItem => (this.FindResource("ExplorerContextMenu") as ContextMenu)?.Items.OfType<MenuItem>().FirstOrDefault(m => m.Header?.ToString() == "Pin to Tabs");
        public MenuItem AddToFavoritesMenuItem => (this.FindResource("ExplorerContextMenu") as ContextMenu)?.Items.OfType<MenuItem>().FirstOrDefault(m => m.Header?.ToString() == "Add to")?.Items.OfType<MenuItem>().FirstOrDefault(m => m.Header?.ToString() == "Favorites");
        public MenuItem ViewChangesMenuItem => (this.FindResource("ExplorerContextMenu") as ContextMenu)?.Items.OfType<MenuItem>().FirstOrDefault(m => m.Header?.ToString()?.Contains("Differences") == true || m.Header?.ToString()?.Contains("Changes") == true);
        public MenuItem ExtractMenuItem => (this.FindResource("ExplorerContextMenu") as ContextMenu)?.Items.OfType<MenuItem>().FirstOrDefault(m => m.Header?.ToString() == "Extract");
        public MenuItem SaveMenuItem => (this.FindResource("ExplorerContextMenu") as ContextMenu)?.Items.OfType<MenuItem>().FirstOrDefault(m => m.Header?.ToString() == "Save");
        public MenuItem AddToImageMergerMenuItem => (this.FindResource("ExplorerContextMenu") as ContextMenu)?.Items.OfType<MenuItem>().FirstOrDefault(m => m.Header?.ToString() == "Add to")?.Items.OfType<MenuItem>().FirstOrDefault(m => m.Header?.ToString() == "Image Merger");
        public MenuItem WatchAssetMenuItem => (this.FindResource("ExplorerContextMenu") as ContextMenu)?.Items.OfType<MenuItem>().FirstOrDefault(m => m.Header?.ToString() == "Add to")?.Items.OfType<MenuItem>().FirstOrDefault(m => m.Header?.ToString() == "Watch Asset");
        public MenuItem CopyMenuItem => (this.FindResource("ExplorerContextMenu") as ContextMenu)?.Items.OfType<MenuItem>().FirstOrDefault(m => m.Header?.ToString() == "Copy");

        // Injected Services
        public LogService LogService { get; set; }
        public CustomMessageBoxService CustomMessageBoxService { get; set; }
        public WadContentProvider WadContentProvider { get; set; }
        public WadExportService WadExportService { get; set; }
        public WadSearchBoxService WadSearchBoxService { get; set; }
        public WadNodeLoaderService WadNodeLoaderService { get; set; }
        public DiffViewService DiffViewService { get; set; }
        public DirectoriesCreator DirectoriesCreator { get; set; }
        public AppSettings AppSettings { get; set; }
        public TreeBuilderService TreeBuilderService { get; set; }
        public TreeUIManager TreeUIManager { get; set; }
        public FavoritesManager FavoritesManager { get; set; }
        public AudioBankService AudioBankService { get; set; }
        public AudioBankLinkerService AudioBankLinkerService { get; set; }
        public HashResolverService HashResolverService { get; set; }
        public AssetWatcherService AssetWatcherService { get; set; }
        public VersionService VersionService { get; set; }
        public MonitorService MonitorService { get; set; }
        public BackupManager BackupManager { get; set; }
        public TaskCancellationManager TaskCancellationManager { get; set; }
        public ImageMergerService ImageMergerService { get; set; }
        public ProgressUIManager ProgressUIManager { get; set; }

        public string NewLolPath { get; set; }
        public string OldLolPath { get; set; }

        public string NewPbePath => NewLolPath ?? AppSettings.LolPbeDirectory;
        public string OldPbePath => OldLolPath;

        public ObservableRangeCollection<FileSystemNodeModel> RootNodes => _viewModel.RootNodes;

        private readonly FileExplorerModel _viewModel;
        
        private readonly DispatcherTimer _searchTimer;
        private string _currentRootPath;
        private string _backupJsonPath;
        private bool _isExternalInitRequested = false;

        // State tracking for smart reloading
        private string _lastPbePath;
        private string _lastLivePath;
        private PreferredClient _lastPreferredClient;
        private PreferredDirectory _lastPreferredDirectory;

        public FileExplorerControl()
        {
            InitializeComponent();
            
            _viewModel = new FileExplorerModel();
            DataContext = _viewModel;
            
            this.Loaded += FileExplorerControl_Loaded;
            this.Unloaded += FileExplorerControl_Unloaded;

            _searchTimer = new DispatcherTimer();
            _searchTimer.Interval = TimeSpan.FromMilliseconds(300);
        }

        private void FileExplorerControl_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_searchTimer != null)
            {
                _searchTimer.Stop();
                _searchTimer.Tick -= SearchTimer_Tick;
            }

            if (AppSettings != null)
            {
                AppSettings.ConfigurationSaved -= OnConfigurationSaved;
            }

            if (_viewModel.Toolbar != null)
            {
                _viewModel.Toolbar.PropertyChanged -= Toolbar_PropertyChanged;
                _viewModel.Toolbar.ParentExplorer = null;
            }

            if (FavoritesManager != null)
            {
                FavoritesManager.Favorites.CollectionChanged -= Favorites_CollectionChanged;
            }
        }

        private async void OnConfigurationSaved(object sender, EventArgs e)
        {
            // SMART FILTER: Only reload if settings that actually affect the Explorer have changed.
            // This prevents the tree from resetting when history, notifications, or background checks occur.
            bool pathChanged = _lastPbePath != AppSettings.LolPbeDirectory || _lastLivePath != AppSettings.LolLiveDirectory;
            bool prefChanged = _lastPreferredClient != AppSettings.PreferredClient || _lastPreferredDirectory != AppSettings.PreferredDirectory;

            if (pathChanged || prefChanged)
            {
                // Update tracked state
                _lastPbePath = AppSettings.LolPbeDirectory;
                _lastLivePath = AppSettings.LolLiveDirectory;
                _lastPreferredClient = AppSettings.PreferredClient;
                _lastPreferredDirectory = AppSettings.PreferredDirectory;

                await Dispatcher.InvokeAsync(async () =>
                {
                    await ReloadTreeAsync();
                });
            }
        }

        public void CleanupResources()
        {
            // 1. CRITICAL: Cancel any active tree build or extraction
            TaskCancellationManager.CancelCurrentOperation();

            // 2. Stop search timer (Unloaded also does this, but we ensure it here too)
            if (_searchTimer != null)
            {
                _searchTimer.Stop();
            }

            // 3. Clear the TreeView binding and events
            if (FileTreeView != null)
            {
                FileTreeView.SelectedItemChanged -= FileTreeView_SelectedItemChanged;
                FileTreeView.ItemsSource = null; 
            }

            // 4. DEEP CLEANUP: Dispose all nodes recursively to release megabytes of RAM
            if (_viewModel.RootNodes != null)
            {
                foreach (var rootNode in _viewModel.RootNodes.ToList())
                {
                    rootNode.Dispose(); 
                }
                _viewModel.RootNodes.Clear();
            }

            // 5. Break peer connections
            FilePreviewer = null; 

            // 6. Reset internal state
            _currentRootPath = null;
            _isExternalInitRequested = false;
        }

        private async void FileExplorerControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (AppSettings != null)
            {
                AppSettings.ConfigurationSaved -= OnConfigurationSaved;
                AppSettings.ConfigurationSaved += OnConfigurationSaved;
            }

            // Setup toolbar peer connection - ALWAYS do this
            _viewModel.Toolbar.ParentExplorer = this;
            _viewModel.Toolbar.PropertyChanged -= Toolbar_PropertyChanged;
            _viewModel.Toolbar.PropertyChanged += Toolbar_PropertyChanged;

            // Setup search timer tick - ALWAYS do this
            _searchTimer.Tick -= SearchTimer_Tick;
            _searchTimer.Tick += SearchTimer_Tick;

            // Bind Favorites - ALWAYS do this
            if (FavoritesManager != null)
            {
                FavoritesListView.ItemsSource = FavoritesManager.Favorites;
                
                // Track changes to update visibility (using self-healing pattern to avoid duplicates)
                FavoritesManager.Favorites.CollectionChanged -= Favorites_CollectionChanged;
                FavoritesManager.Favorites.CollectionChanged += Favorites_CollectionChanged;
                _viewModel.HasFavorites = FavoritesManager.Favorites.Count > 0;
            }

            // Initialize tracking state to prevent false-positive reloads
            _lastPbePath = AppSettings.LolPbeDirectory;
            _lastLivePath = AppSettings.LolLiveDirectory;
            _lastPreferredClient = AppSettings.PreferredClient;
            _lastPreferredDirectory = AppSettings.PreferredDirectory;

            if (_isExternalInitRequested) return;

            // Smart discovery based on preference
            string pbe = AppSettings.LolPbeDirectory;
            string live = AppSettings.LolLiveDirectory;
            bool pbeValid = !string.IsNullOrEmpty(pbe) && Directory.Exists(pbe);
            bool liveValid = !string.IsNullOrEmpty(live) && Directory.Exists(live);
            
            string wadPath = null;
            if (AppSettings.PreferredClient == PreferredClient.PBE)
            {
                wadPath = pbeValid ? pbe : (liveValid ? live : null);
            }
            else
            {
                wadPath = liveValid ? live : (pbeValid ? pbe : null);
            }

            // First, do the synchronous checks to decide the initial UI state.
            bool shouldLoadWadTree = _viewModel.IsWadMode && wadPath != null;
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

            // Finally, trigger the tree build if needed.
            if (shouldLoadWadTree)
            {
                await BuildWadTreeAsync(wadPath);
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

        private void Toolbar_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(ExplorerToolbarModel.IsGridMode):
                    FilePreviewer?.SetViewMode(_viewModel.Toolbar.IsGridMode);
                    break;
                case nameof(ExplorerToolbarModel.IsBreadcrumbVisible):
                    FilePreviewer?.SetBreadcrumbToggleState(_viewModel.Toolbar.IsBreadcrumbVisible);
                    break;
                case nameof(ExplorerToolbarModel.IsGroupingEnabled):
                    HandleSortStateChanged();
                    break;
                case nameof(ExplorerToolbarModel.SearchText):
                    HandleSearchTextChanged();
                    break;
            }
        }

        public void SetToolbarViewMode(bool isGridMode)
        {
            _viewModel.Toolbar.IsGridMode = isGridMode;
        }

        public async void HandleSwitchMode()
        {
            // Update the toolbar state to switch modes
            _viewModel.Toolbar.IsWadMode = !_viewModel.Toolbar.IsWadMode;
            _viewModel.Toolbar.IsBackupMode = false;

            // Reset paths so ReloadTree can re-discover the correct root
            _currentRootPath = null;
            _backupJsonPath = null;
            
            if (FilePreviewer != null)
            {
                await FilePreviewer.ResetToDefaultState();
            }
            await ReloadTreeAsync();
        }

        public async void HandleSortStateChanged()
        {
            if (_viewModel.IsBackupMode)
            {
                await BuildTreeFromBackupAsync(_backupJsonPath);
            }
        }

        public void HandleImageMergerClicked()
        {
            ImageMergerService.ShowWindow();
        }

        public async Task InitializeWithMode(string mode)
        {
            _isExternalInitRequested = true;

            switch (mode)
            {
                case "Live":
                    _viewModel.IsWadMode = true;
                    if (!string.IsNullOrEmpty(AppSettings.LolLiveDirectory) && Directory.Exists(AppSettings.LolLiveDirectory))
                        await BuildWadTreeAsync(AppSettings.LolLiveDirectory);
                    else
                        _viewModel.UpdateEmptyState(true);
                    break;
                case "Pbe":
                    _viewModel.IsWadMode = true;
                    if (!string.IsNullOrEmpty(AppSettings.LolPbeDirectory) && Directory.Exists(AppSettings.LolPbeDirectory))
                        await BuildWadTreeAsync(AppSettings.LolPbeDirectory);
                    else
                        _viewModel.UpdateEmptyState(true);
                    break;
                case "Local":
                    _viewModel.IsWadMode = false;
                    if (!string.IsNullOrEmpty(DirectoriesCreator.AssetsDownloadedPath) && Directory.Exists(DirectoriesCreator.AssetsDownloadedPath))
                        await BuildDirectoryTreeAsync(DirectoriesCreator.AssetsDownloadedPath);
                    else
                        _viewModel.UpdateEmptyState(false);
                    break;
            }
        }

        public async Task ReloadTreeAsync()
        {
            if (_viewModel.IsWadMode)
            {
                string path = _currentRootPath;
                
                if (string.IsNullOrEmpty(path))
                {
                    // Discovery logic based on preference
                    string pbe = AppSettings.LolPbeDirectory;
                    string live = AppSettings.LolLiveDirectory;
                    bool pbeValid = !string.IsNullOrEmpty(pbe) && Directory.Exists(pbe);
                    bool liveValid = !string.IsNullOrEmpty(live) && Directory.Exists(live);

                    if (AppSettings.PreferredClient == PreferredClient.PBE)
                    {
                        path = pbeValid ? pbe : (liveValid ? live : null);
                    }
                    else
                    {
                        path = liveValid ? live : (pbeValid ? pbe : null);
                    }
                }

                if (path != null)
                {
                    await BuildWadTreeAsync(path);
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

        public async void HandleLoadResults()
        {
            var openFileDialog = new CommonOpenFileDialog
            {
                Title = "Select a comparison result file",
                Filters = { new CommonFileDialogFilter("WAD Comparison JSON", "wadcomparison.json"), new CommonFileDialogFilter("All files", "*.*") },
                InitialDirectory = DirectoriesCreator.WadComparisonSavePath
            };

            if (openFileDialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                _currentRootPath = null; // Important: Clear root when loading result

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

            try
            {
                var newNodes = await buildFunc(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                _viewModel.RootNodes.ReplaceRange(newNodes);

                onSuccess?.Invoke(newNodes);

                if (_viewModel.RootNodes.Count == 0 && !isBackupMode)
                {
                    LogService.LogWarning("No items found in the selected location.");
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

            // Identity Badge Logic
            if (BackupManager != null)
            {
                var (isPbe, _) = BackupManager.GetPathIdentification(rootPath);
                _viewModel.Toolbar.CurrentClientName = isPbe ? "PBE" : "LIVE";
                _viewModel.Toolbar.CurrentClientBrush = (System.Windows.Media.Brush)Application.Current.FindResource(isPbe ? "AccentBlue" : "AccentBrush");
                _viewModel.Toolbar.CurrentClientIcon = isPbe ? Material.Icons.MaterialIconKind.FlaskOutline : Material.Icons.MaterialIconKind.SealVariant;
            }

            await ExecuteTreeBuildInternalAsync(
                async ct => await TreeBuilderService.BuildWadTreeAsync(rootPath, ct, AppSettings.PreferredDirectory, 
                    (file) => Dispatcher.Invoke(() => _viewModel.UpdateScanningProgress(file)),
                    (dir) => Dispatcher.Invoke(() => _viewModel.UpdateMountingProgress(dir))),
                ExplorerLoadingState.LoadingWads,
                "Failed to build WAD tree.",
                false);
        }

        private async Task BuildDirectoryTreeAsync(string rootPath)
        {
            _currentRootPath = rootPath;
            NewLolPath = null;
            OldLolPath = null;

            _viewModel.Toolbar.CurrentClientName = "LOCAL";
            _viewModel.Toolbar.CurrentClientBrush = (System.Windows.Media.Brush)Application.Current.FindResource("TextMuted");

            await ExecuteTreeBuildInternalAsync(
                async ct => await TreeBuilderService.BuildDirectoryTreeAsync(rootPath, ct, 
                    (file) => Dispatcher.Invoke(() => _viewModel.UpdateScanningProgress(file)),
                    (dir) => Dispatcher.Invoke(() => _viewModel.UpdateMountingProgress(dir))),
                ExplorerLoadingState.ExploringDirectory,
                "Failed to build directory tree.",
                false);
        }

        private async Task BuildTreeFromBackupAsync(string jsonPath)
        {
            _backupJsonPath = jsonPath;

            _viewModel.Toolbar.CurrentClientName = "RESULT";
            _viewModel.Toolbar.CurrentClientBrush = (System.Windows.Media.Brush)Application.Current.FindResource("AccentTeal");

            await ExecuteTreeBuildInternalAsync(
                async ct => 
                {
                    var (nodes, newPath, oldPath) = await TreeBuilderService.BuildTreeFromBackupAsync(jsonPath, _viewModel.IsSortingEnabled, ct);
                    NewLolPath = newPath;
                    OldLolPath = oldPath;
                    return nodes;
                },
                ExplorerLoadingState.LoadingResults,
                "Failed to build tree from backup.",
                true);
        }

        public async void TriggerExtractNodes(List<FileSystemNodeModel> nodes)
        {
            if (WadExportService == null || nodes == null || nodes.Count == 0) return;
            await ExecuteExtractionAsync(nodes);
        }

        public async void TriggerSaveNodes(List<FileSystemNodeModel> nodes)
        {
            if (WadExportService == null || nodes == null || nodes.Count == 0) return;
            await ExecuteSaveAsync(nodes);
        }

        public async void TriggerAddToMerger(List<FileSystemNodeModel> nodes)
        {
            if (ImageMergerService == null || nodes == null || nodes.Count == 0) return;
            await ExecuteAddToMergerAsync(nodes);
        }

        private async void ExtractSelected_Click(object sender, RoutedEventArgs e)
        {
            if (WadExportService == null)
            {
                CustomMessageBoxService.ShowError("Error", "Wad Export Service is not available.", Window.GetWindow(this));
                return;
            }

            var selectedNodes = TreeUIManager.GetSelectedNodes(_viewModel.RootNodes, FileTreeView.SelectedItem as FileSystemNodeModel);
            if (selectedNodes.Count == 0)
            {
                CustomMessageBoxService.ShowInfo("Info", "Please select one or more files or folders to extract.", Window.GetWindow(this));
                return;
            }

            await ExecuteExtractionAsync(selectedNodes);
        }

        private async Task ExecuteExtractionAsync(List<FileSystemNodeModel> selectedNodes)
        {
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
                CancellationToken cancellationToken = TaskCancellationManager.PrepareNewOperation();

                try
                {
                    ProgressUIManager?.OnExtractionStarted(this, ("Extracting Assets...", 0));

                    int processed = await WadExportService.ExportNodesAsync(selectedNodes, destinationPath, WadExportMode.Original,
                        _viewModel.RootNodes, _currentRootPath, cancellationToken,
                        (current, total, fileName) => ProgressUIManager?.OnExtractionProgressChanged(current, total, fileName));

                    if (selectedNodes.Count == 1)
                    {
                        var node = selectedNodes[0];
                        string logName = PathUtils.GetLogName(node.Name);
                        string logPath = destinationPath;

                        if (node.Type == NodeType.RealDirectory || node.Type == NodeType.VirtualDirectory || node.Type == NodeType.WadFile || node.Type == NodeType.AudioEvent)
                        {
                            string cleanName = PathUtils.GetLogName(node.Name);
                            logPath = Path.Combine(destinationPath, PathUtils.SanitizeName(cleanName));
                        }

                        LogService.LogInteractiveSuccess($"Successfully extracted {logName}", logPath, logName);
                    }
                    else
                    {
                        LogService.LogInteractiveSuccess($"Successfully extracted {selectedNodes.Count} selected items", destinationPath, "Extracted Assets");
                    }

                    TaskCancellationManager.CompleteCurrentOperation();
                }
                catch (OperationCanceledException)
                {
                    LogService.LogWarning("Extraction process was cancelled.");
                }
                catch (Exception ex)
                {
                    LogService.LogError(ex, $"Failed during extraction process.");
                    CustomMessageBoxService.ShowError("Error", $"An error occurred during extraction: {ex.Message}", Window.GetWindow(this));
                }
                finally
                {
                    ProgressUIManager?.OnExtractionCompleted();
                    ExtractMenuItem.IsEnabled = true;
                    SaveMenuItem.IsEnabled = true;
                }
            }
        }

        private async void SaveSelected_Click(object sender, RoutedEventArgs e)
        {
            if (WadExportService == null)
            {
                CustomMessageBoxService.ShowError("Error", "Wad Export Service is not available.", Window.GetWindow(this));
                return;
            }

            var selectedNodes = TreeUIManager.GetSelectedNodes(_viewModel.RootNodes, FileTreeView.SelectedItem as FileSystemNodeModel);
            if (selectedNodes.Count == 0)
            {
                CustomMessageBoxService.ShowInfo("Info", "Please select one or more files or folders to save.", Window.GetWindow(this));
                return;
            }

            await ExecuteSaveAsync(selectedNodes);
        }

        private async Task ExecuteSaveAsync(List<FileSystemNodeModel> selectedNodes)
        {
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
                CancellationToken cancellationToken = TaskCancellationManager.PrepareNewOperation();

                try
                {
                    ProgressUIManager?.OnSavingStarted(0);

                    var allSavedFiles = new List<string>();
                    int processed = await WadExportService.ExportNodesAsync(selectedNodes, destinationPath, WadExportMode.Smart,
                        _viewModel.RootNodes, _currentRootPath, cancellationToken,
                        (current, total, fileName) => ProgressUIManager?.OnSavingProgressChanged(current, total, fileName),
                        (path) => allSavedFiles.Add(path));

                    if (selectedNodes.Count == 1)
                    {
                        var node = selectedNodes[0];
                        string logPath = destinationPath;
                        string logName = node.Name;

                        if (node.Type == NodeType.SoundBank)
                        {
                            string cleanName = PathUtils.GetLogName(node.Name);
                            logPath = Path.Combine(destinationPath, Path.GetFileNameWithoutExtension(cleanName));
                            logName = PathUtils.GetLogName(node.Name);
                        }
                        else if (node.Type == NodeType.RealDirectory || node.Type == NodeType.VirtualDirectory || node.Type == NodeType.WadFile || node.Type == NodeType.AudioEvent)
                        {
                            string cleanName = PathUtils.GetLogName(node.Name);
                            logPath = Path.Combine(destinationPath, PathUtils.SanitizeName(cleanName));
                            logName = PathUtils.GetLogName(node.Name);
                        }

                        if (allSavedFiles.Count == 1)
                        {
                            logPath = allSavedFiles.First();
                            logName = Path.GetFileName(logPath);
                        }

                        LogService.LogInteractiveSuccess($"Successfully saved {logName}", logPath, logName);
                    }
                    else
                    {
                        LogService.LogInteractiveSuccess($"Successfully saved {selectedNodes.Count} selected items", destinationPath, "Saved Assets");
                    }

                    TaskCancellationManager.CompleteCurrentOperation();
                }
                catch (OperationCanceledException)
                {
                    LogService.LogWarning("Save process was cancelled.");
                }
                catch (Exception ex)
                {
                    LogService.LogError(ex, $"Failed during save process.");
                    CustomMessageBoxService.ShowError("Error", $"An error occurred during save: {ex.Message}", Window.GetWindow(this));
                }
                finally
                {
                    ProgressUIManager?.OnSavingCompleted();
                    ExtractMenuItem.IsEnabled = true;
                    SaveMenuItem.IsEnabled = true;
                }
            }
        }

        private async void ViewChanges_Click(object sender, RoutedEventArgs e)
        {
            // Use the TreeUIManager to get ALL multi-selected nodes, or the current one if none
            var selectedNodes = TreeUIManager.GetSelectedNodes(_viewModel.RootNodes, FileTreeView.SelectedItem as FileSystemNodeModel);
            
            // Filter only those that actually HAVE a real difference (exclude dependencies)
            var nodesWithDiff = selectedNodes.Where(n => n.ChunkDiff != null && n.Status != DiffStatus.Dependency).ToList();

            if (nodesWithDiff.Count > 1 && FilePreviewer != null)
            {
                await FilePreviewer.HandleBatchDiffRequestAsync(nodesWithDiff);
                return;
            }

            if (nodesWithDiff.Count == 1)
            {
                string oldPbePath = this.OldLolPath;
                string newPbePath = this.NewLolPath;

                if (_viewModel.IsBackupMode)
                {
                    oldPbePath = null;
                    newPbePath = null;
                }

                await DiffViewService.ShowWadDiffAsync(nodesWithDiff[0].ChunkDiff, oldPbePath, newPbePath, Window.GetWindow(this), _viewModel.IsBackupMode ? _backupJsonPath : null);
            }
        }

        public async void TriggerBatchDiff(List<FileSystemNodeModel> nodes)
        {
            if (FilePreviewer != null)
            {
                await FilePreviewer.HandleBatchDiffRequestAsync(nodes);
            }
        }

        private void PinSelected_Click(object sender, RoutedEventArgs e)
        {
            if (FileTreeView.SelectedItem is FileSystemNodeModel selectedNode && FilePreviewer != null)
            {
                // Use the manager to pin explicitly (marks as IsPinned = true)
                FilePreviewer.ViewModel.PinnedFilesManager.PinFile(selectedNode, isExplicitPin: true);
            }
        }

        private void CopyName_Click(object sender, RoutedEventArgs e)
        {
            if (FileTreeView.SelectedItem is FileSystemNodeModel node)
            {
                Clipboard.SetText(node.Name);
            }
        }

        private void CopyPath_Click(object sender, RoutedEventArgs e)
        {
            if (FileTreeView.SelectedItem is FileSystemNodeModel node)
            {
                Clipboard.SetText(node.CopyFullPath);
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
                    string virtualPath = string.Join("/", validNodes.Select(n => n.Name));
                    FavoritesManager.AddFavorite(virtualPath);
                }
            }
        }

        private async void WatchAsset_Click(object sender, RoutedEventArgs e)
        {
            if (FileTreeView.SelectedItem is FileSystemNodeModel selectedNode && selectedNode.Type == NodeType.VirtualFile)
            {
                var path = TreeUIManager.FindNodePath(_viewModel.RootNodes, selectedNode);
                if (path == null) return;

                var (added, duplicatePath) = await AssetWatcherService.AddAssetAsync(selectedNode, path, AppSettings.LolPbeDirectory, NewPbePath);

                if (!added && duplicatePath != null)
                {
                    CustomMessageBoxService.ShowWarning("Warning", "This asset is already being monitored.", Window.GetWindow(this));
                    return;
                }

                MonitorService?.LoadMonitoredAssets();
                LogService.LogSuccess($"Asset added to watcher: {selectedNode.Name}");
            }
        }

        private async void AddToImageMerger_Click(object sender, RoutedEventArgs e)
        {
            var selectedNodes = TreeUIManager.GetSelectedNodes(_viewModel.RootNodes, FileTreeView.SelectedItem as FileSystemNodeModel);
            await ExecuteAddToMergerAsync(selectedNodes);
        }

        private async Task ExecuteAddToMergerAsync(List<FileSystemNodeModel> selectedNodes)
        {
            if (selectedNodes == null || selectedNodes.Count == 0) return;

            foreach (var node in selectedNodes)
                await ImageMergerService.AddNodeAsync(node);
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

                var node = await WadSearchBoxService.NavigateToPathAsync(item.VirtualPath, _viewModel.RootNodes, LoadAllChildrenForSearch);

                if (node != null)
                {
                    TreeUIManager.SelectAndFocusNode(FileTreeView, _viewModel.RootNodes, node, true);
                }
                else
                {
                    LogService.LogWarning($"Favorite node not found: {item.VirtualPath}");
                    CustomMessageBoxService.ShowInfo("Not Found", $"Could not find '{item.DisplayName}' in the current tree.", Window.GetWindow(this));
                }
            }
        }

        private void ContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (FileTreeView.SelectedItem is not FileSystemNodeModel selectedNode) return;

            // Update ViewModel selection so it can calculate values
            _viewModel.SelectedItem = selectedNode;
            _viewModel.SelectedNodes = new ObservableCollection<FileSystemNodeModel>(
                TreeUIManager.GetSelectedNodes(_viewModel.RootNodes, selectedNode));

            if (ExtractMenuItem != null) ExtractMenuItem.IsEnabled = _viewModel.IsWadMode;
            if (SaveMenuItem != null) SaveMenuItem.IsEnabled = _viewModel.IsWadMode;
            if (PinMenuItem != null) PinMenuItem.IsEnabled = selectedNode.Type != NodeType.RealDirectory && selectedNode.Type != NodeType.VirtualDirectory && selectedNode.Type != NodeType.WadFile;
            if (AddToFavoritesMenuItem != null) AddToFavoritesMenuItem.IsEnabled = _viewModel.IsWadMode && !_viewModel.IsBackupMode; 
            if (WatchAssetMenuItem != null) WatchAssetMenuItem.IsEnabled = _viewModel.IsWadMode && !_viewModel.IsBackupMode && (selectedNode.Type == NodeType.VirtualFile);

            if (ViewChangesMenuItem != null)
            {
                ViewChangesMenuItem.Header = _viewModel.ViewChangesHeader;
                ViewChangesMenuItem.IsEnabled = _viewModel.CanViewChanges;
            }

            if (AddToImageMergerMenuItem != null)
            {
                AddToImageMergerMenuItem.IsEnabled = (SupportedFileTypes.Images.Contains(selectedNode.Extension) || SupportedFileTypes.Textures.Contains(selectedNode.Extension)) && 
                                                    (selectedNode.Type == NodeType.VirtualFile || selectedNode.Type == NodeType.RealFile);
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
                
                if (FilePreviewer != null)
                {
                    FilePreviewer.UpdateSelectedNode(selectedNode, RootNodes);
                    await FilePreviewer.ShowPreviewAsync(selectedNode);
                }
            }
        }

        public void HandleSearchTextChanged()
        {
            _searchTimer.Stop();
            _searchTimer.Start();
        }

        public async void HandleCollapseToContainer()
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
                bool isBackupRoot = rootNode.Children.Any(c => c.IsGroupingFolder);
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

                await Dispatcher.InvokeAsync(() =>
                {
                    TreeUIManager.SelectAndFocusNode(FileTreeView, _viewModel.RootNodes, containerNode, false);
                }, DispatcherPriority.ContextIdle);
            }
        }

        private async Task LoadAllChildrenForSearch(FileSystemNodeModel node)
        {
            if (node.Type == NodeType.SoundBank)
            {
                await TreeBuilderService.ExpandAudioBankAsync(node, _viewModel.RootNodes, _currentRootPath, NewLolPath, OldLolPath);
            }
            else
            {
                await TreeBuilderService.EnsureAllChildrenLoadedAsync(node, _currentRootPath);
            }
        }

        private async void SearchTimer_Tick(object sender, EventArgs e)
        {
            _searchTimer.Stop();
            string searchText = _viewModel.Toolbar.SearchText;

            // CRITICAL: Immediate UI feedback when clearing search
            if (string.IsNullOrEmpty(searchText))
            {
                _viewModel.IsNoResultsFound = false;
            }

            if (FilePreviewer != null)
            {
                FilePreviewer.SetSearchFilter(searchText);
            }

            if (!string.IsNullOrEmpty(searchText))
            {
                Toolbar?.AddSearchToHistory(searchText);
            }

            var selectedNode = FileTreeView.SelectedItem as FileSystemNodeModel;
            var nodeToSelect = await WadSearchBoxService.PerformSearchAsync(searchText, _viewModel.RootNodes, null, selectedNode);

            // Update No Results found UI after search completes
            if (!string.IsNullOrEmpty(searchText))
            {
                _viewModel.IsNoResultsFound = _viewModel.RootNodes.All(n => !n.IsVisible);
            }
            else
            {
                // Already set above, but ensuring consistency
                _viewModel.IsNoResultsFound = false;
            }

            if (nodeToSelect != null)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    TreeUIManager.SelectAndFocusNode(FileTreeView, _viewModel.RootNodes, nodeToSelect, false);
                }, DispatcherPriority.ContextIdle);
            }
            else if (selectedNode != null && string.IsNullOrEmpty(searchText))
            {
                // If search was cleared, ensure the previously selected node is still in view
                await Dispatcher.InvokeAsync(() =>
                {
                    TreeUIManager.SelectAndFocusNode(FileTreeView, _viewModel.RootNodes, selectedNode);
                }, DispatcherPriority.ContextIdle);
            }
        }

        public void SelectNode(FileSystemNodeModel node)
        {
            if (node == null) return;
            
            // SECURITY GUARD: Only skip if the TreeView already has THIS EXACT node selected.
            // This prevents infinite loops but allows refreshing if the underlying pin content changes.
            if (FileTreeView.SelectedItem is FileSystemNodeModel current && current == node) return;

            TreeUIManager.SelectAndFocusNode(FileTreeView, _viewModel.RootNodes, node, false);
        }
    }
}
