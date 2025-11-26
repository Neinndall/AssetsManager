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

namespace AssetsManager.Views.Controls.Explorer
{
    public partial class FileExplorerControl : UserControl
    {
        public event RoutedPropertyChangedEventHandler<object> FileSelected;

        public FilePreviewerControl FilePreviewer { get; set; }

        public MenuItem PinMenuItem => (this.FindResource("ExplorerContextMenu") as ContextMenu)?.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "PinMenuItem");
        public MenuItem ViewChangesMenuItem => (this.FindResource("ExplorerContextMenu") as ContextMenu)?.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "ViewChangesMenuItem");
        public MenuItem ExtractMenuItem => (this.FindResource("ExplorerContextMenu") as ContextMenu)?.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "ExtractMenuItem");
        public MenuItem SaveMenuItem => (this.FindResource("ExplorerContextMenu") as ContextMenu)?.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "SaveMenuItem");

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
        public AudioBankService AudioBankService { get; set; }
        public AudioBankLinkerService AudioBankLinkerService { get; set; }
        public HashResolverService HashResolverService { get; set; }
        public TaskCancellationManager TaskCancellationManager { get; set; }

        public string NewLolPath { get; set; }
        public string OldLolPath { get; set; }

        public ObservableCollection<FileSystemNodeModel> RootNodes { get; set; }
        private readonly DispatcherTimer _searchTimer;
        private string _currentRootPath;
        private bool _isWadMode = true;
        private bool _isBackupMode = false;
        private bool _isSortingEnabled = true;
        private string _backupJsonPath;

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

        public void CleanupResources()
        {
            TaskCancellationManager.CancelCurrentOperation(); // Use the manager

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
                Toolbar.SortStateChanged -= Toolbar_SortStateChanged;
            }

            // Desuscribir eventos del Breadcrumbs
            if (Breadcrumbs != null)
            {
                Breadcrumbs.NodeClicked -= Breadcrumbs_NodeClicked;
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
            if (RootNodes != null)
            {
                foreach (var rootNode in RootNodes.ToList())
                {
                    rootNode.Dispose(); // ← Usa el nuevo método Dispose
                }
                RootNodes.Clear();
                RootNodes = null;
            }

            // 6. Romper referencias cruzadas
            FilePreviewer = null;

            // 8. Limpiar paths
            _currentRootPath = null;
        }

        private async void FileExplorerControl_Loaded(object sender, RoutedEventArgs e)
        {
            // First, do the synchronous checks to decide the initial UI state.
            bool shouldLoadWadTree = _isWadMode && !string.IsNullOrEmpty(AppSettings.LolPbeDirectory) && Directory.Exists(AppSettings.LolPbeDirectory);
            bool shouldLoadDirTree = !_isWadMode && !string.IsNullOrEmpty(DirectoriesCreator.AssetsDownloadedPath) && Directory.Exists(DirectoriesCreator.AssetsDownloadedPath);

            if (shouldLoadWadTree || shouldLoadDirTree)
            {
                // If we are going to load, show the indicator immediately.
                LoadingIndicator.Visibility = Visibility.Visible;
                NoDirectoryMessage.Visibility = Visibility.Collapsed;
                FileTreeView.Visibility = Visibility.Collapsed;
            }
            else
            {
                // If we are not going to load, show the correct placeholder immediately.
                ShowPlaceholder(_isWadMode);
            }

            // Now, perform the async hash loading.
            await HashResolverService.LoadAllHashesAsync();

            // Setup toolbar events (can be done regardless of loading)
            Toolbar.SearchTextChanged += Toolbar_SearchTextChanged;
            Toolbar.CollapseToContainerClicked += Toolbar_CollapseToContainerClicked;
            Toolbar.LoadComparisonClicked += Toolbar_LoadComparisonClicked;
            Toolbar.SwitchModeClicked += Toolbar_SwitchModeClicked;
            Toolbar.BreadcrumbVisibilityChanged += Toolbar_BreadcrumbVisibilityChanged;
            Toolbar.SortStateChanged += Toolbar_SortStateChanged;

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

        private async void Toolbar_SwitchModeClicked(object sender, RoutedEventArgs e)
        {
            _isWadMode = !_isWadMode;
            await ReloadTreeAsync();
        }

        private void Toolbar_BreadcrumbVisibilityChanged(object sender, RoutedPropertyChangedEventArgs<bool> e)
        {
            Breadcrumbs.Visibility = e.NewValue ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void Toolbar_SortStateChanged(object sender, RoutedPropertyChangedEventArgs<bool> e)
        {
            _isSortingEnabled = e.NewValue;
            if (_isBackupMode)
            {
                await BuildTreeFromBackupAsync(_backupJsonPath);
            }
        }

        public async Task ReloadTreeAsync()
        {
            if (_isWadMode)
            {
                if (!string.IsNullOrEmpty(AppSettings.LolPbeDirectory) && Directory.Exists(AppSettings.LolPbeDirectory))
                {
                    await BuildWadTreeAsync(AppSettings.LolPbeDirectory);
                }
                else
                {
                    ShowPlaceholder(true);
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
                    ShowPlaceholder(false);
                }
            }
        }

        private void ShowPlaceholder(bool isWadMode)
        {
            if (isWadMode) {
                PlaceholderTitle.Text = "Select a LoL Directory";
                PlaceholderDescription.Text = "Choose the root folder where you installed League of Legends to browse its WAD files.";
                SelectLolDirButton.Visibility = Visibility.Visible;
            } else {
                PlaceholderTitle.Text = "Assets Directory Not Found";
                PlaceholderDescription.Text = "The application could not find the directory for downloaded assets.";
                SelectLolDirButton.Visibility = Visibility.Collapsed;
            }
            RootNodes.Clear();
            FileTreeView.Visibility = Visibility.Collapsed;
            NoDirectoryMessage.Visibility = Visibility.Visible;
            LoadingIndicator.Visibility = Visibility.Collapsed;
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
                await BuildTreeFromBackupAsync(openFileDialog.FileName);
            }
        }

        private async Task BuildWadTreeAsync(string rootPath)
        {
            _isBackupMode = false;
            var cancellationToken = TaskCancellationManager.PrepareNewOperation(); // Use the manager

            _currentRootPath = rootPath;
            NewLolPath = null;
            OldLolPath = null;
            NoDirectoryMessage.Visibility = Visibility.Collapsed;
            FileTreeView.Visibility = Visibility.Collapsed;
            LoadingIndicator.Visibility = Visibility.Visible;

            Toolbar.IsSortButtonVisible = false;

            try
            {
                var newNodes = await TreeBuilderService.BuildWadTreeAsync(rootPath, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                RootNodes.Clear();
                foreach (var node in newNodes)
                {
                    RootNodes.Add(node);
                }

                if (RootNodes.Count == 0)
                {
                    CustomMessageBoxService.ShowError("Error", "Could not find any WAD files in 'Game' or 'Plugins' subdirectories.", Window.GetWindow(this));
                    NoDirectoryMessage.Visibility = Visibility.Visible;
                }
            }
            catch (OperationCanceledException)
            {
                LogService.Log("WAD tree building was cancelled.");
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
            _isBackupMode = false;
            var cancellationToken = TaskCancellationManager.PrepareNewOperation(); // Use the manager

            _currentRootPath = rootPath;
            NewLolPath = null;
            OldLolPath = null;
            NoDirectoryMessage.Visibility = Visibility.Collapsed;
            FileTreeView.Visibility = Visibility.Collapsed;

            Toolbar.IsSortButtonVisible = false;

            LoadingIndicator.Visibility = Visibility.Visible;

            try
            {
                var newNodes = await TreeBuilderService.BuildDirectoryTreeAsync(rootPath, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                RootNodes.Clear();
                foreach (var node in newNodes)
                {
                    RootNodes.Add(node);
                }
            }
            catch (OperationCanceledException)
            {
                LogService.Log("Directory tree building was cancelled.");
            }
            catch (Exception ex)
            {
                LogService.LogError(ex, "Failed to build directory tree.");
                CustomMessageBoxService.ShowError("Error", "Could not load the directory. Please check the logs.", Window.GetWindow(this));
                NoDirectoryMessage.Visibility = Visibility.Visible;
            }
            finally
            {
                // The indicatorTask will be implicitly cancelled by the main token,
                // or its continuation will check t.IsCanceled.
                // No need for explicit cts.Cancel() here as the token is already cancelled by _cancellationTokenSource?.Cancel()
                LoadingIndicator.Visibility = Visibility.Collapsed;
                FileTreeView.Visibility = Visibility.Visible;
                Toolbar.Visibility = Visibility.Visible;
                ToolbarSeparator.Visibility = Visibility.Visible;
            }
        }

        private async Task BuildTreeFromBackupAsync(string jsonPath)
        {
            _isBackupMode = true;
            _backupJsonPath = jsonPath;
            var cancellationToken = TaskCancellationManager.PrepareNewOperation(); // Use the manager

            NoDirectoryMessage.Visibility = Visibility.Collapsed;
            FileTreeView.Visibility = Visibility.Collapsed;
            LoadingIndicator.Visibility = Visibility.Visible;

            Toolbar.IsSortButtonVisible = true;

            try
            {
                var (backupNodes, newLolPath, oldLolPath) = await TreeBuilderService.BuildTreeFromBackupAsync(jsonPath, _isSortingEnabled, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                RootNodes.Clear();
                NewLolPath = newLolPath;
                OldLolPath = oldLolPath;
                foreach (var node in backupNodes)
                {
                    RootNodes.Add(node);
                }
            }
            catch (OperationCanceledException)
            {
                LogService.Log("Backup tree building was cancelled.");
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
                    LogService.Log("Extracting selected files...");

                    string logPath = destinationPath;
                    if (selectedNode.Type == NodeType.RealDirectory || selectedNode.Type == NodeType.VirtualDirectory || selectedNode.Type == NodeType.WadFile || selectedNode.Type == NodeType.AudioEvent)
                    {
                        logPath = Path.Combine(destinationPath, selectedNode.Name);
                    }

                    await WadExtractionService.ExtractNodeAsync(selectedNode, destinationPath, cancellationToken);

                    LogService.LogInteractiveSuccess($"Successfully extracted {selectedNode.Name}.", logPath, selectedNode.Name);
                }
                catch (OperationCanceledException)
                {
                    LogService.LogWarning("Extraction was cancelled by the user.");
                    CustomMessageBoxService.ShowInfo("Cancelled", "Extraction was cancelled.", Window.GetWindow(this));
                }
                catch (Exception ex)
                {
                    LogService.LogError(ex, $"Failed to extract '{selectedNode.Name}'.");
                    CustomMessageBoxService.ShowError("Error", $"An error occurred during extraction: {ex.Message}", Window.GetWindow(this));
                }
                finally
                {
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

            if (FileTreeView.SelectedItem is not FileSystemNodeModel selectedNode)
            {
                CustomMessageBoxService.ShowInfo("Info", "Please select a file or folder to save.", Window.GetWindow(this));
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
                    LogService.Log("Processing and saving selected files...");

                    string finalLogPath = destinationPath;
                    if (selectedNode.Type == NodeType.SoundBank)
                    {
                        finalLogPath = Path.Combine(destinationPath, Path.GetFileNameWithoutExtension(selectedNode.Name));
                    }
                    else if (selectedNode.Type == NodeType.RealDirectory || selectedNode.Type == NodeType.VirtualDirectory || selectedNode.Type == NodeType.WadFile || selectedNode.Type == NodeType.AudioEvent)
                    {
                        finalLogPath = Path.Combine(destinationPath, selectedNode.Name);
                    }

                    await WadSavingService.ProcessAndSaveAsync(selectedNode, destinationPath, RootNodes, _currentRootPath, cancellationToken);

                    LogService.LogInteractiveSuccess($"Successfully saved {selectedNode.Name}.", finalLogPath, selectedNode.Name);
                }
                catch (OperationCanceledException)
                {
                    LogService.LogWarning("Save operation was cancelled by the user.");
                    CustomMessageBoxService.ShowInfo("Cancelled", "Save operation was cancelled.", Window.GetWindow(this));
                }
                catch (Exception ex)
                {
                    LogService.LogError(ex, $"Failed to save '{selectedNode.Name}'.");
                    CustomMessageBoxService.ShowError("Error", $"An error occurred during save: {ex.Message}", Window.GetWindow(this));
                }
                finally
                {
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

            if (_isBackupMode)
            {
                oldPbePath = null;
                newPbePath = null;
            }

            await DiffViewService.ShowWadDiffAsync(selectedNode.ChunkDiff, oldPbePath, newPbePath, Window.GetWindow(this), _isBackupMode ? _backupJsonPath : null);
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

            if (ExtractMenuItem is not null)
            {
                ExtractMenuItem.IsEnabled = _isWadMode;
            }

            if (SaveMenuItem is not null)
            {
                SaveMenuItem.IsEnabled = _isWadMode;
            }

            if (PinMenuItem is not null)
            {
                PinMenuItem.IsEnabled = selectedNode.Type != NodeType.RealDirectory && selectedNode.Type != NodeType.VirtualDirectory && selectedNode.Type != NodeType.WadFile;
            }

            if (ViewChangesMenuItem is not null)
            {
                ViewChangesMenuItem.IsEnabled = selectedNode.Status == DiffStatus.Modified;
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
                UpdateBreadcrumbs(selectedNode);

                if (selectedNode.Type == NodeType.SoundBank && selectedNode.Children.Count == 1 && selectedNode.Children[0].Name == "Loading...")
                {
                    await HandleAudioBankExpansion(selectedNode);
                }
                else
                {
                    FileSelected?.Invoke(this, e);
                }
            }
        }

        private void UpdateBreadcrumbs(FileSystemNodeModel selectedNode)
        {
            Breadcrumbs.Nodes.Clear();
            if (selectedNode == null) return;

            var path = TreeUIManager.FindNodePath(RootNodes, selectedNode);
            if (path == null) return;

            if (selectedNode.Type == NodeType.RealFile ||
                selectedNode.Type == NodeType.VirtualFile ||
                selectedNode.Type == NodeType.WemFile ||
                selectedNode.Type == NodeType.AudioEvent)
            {
                if (path.Count > 0)
                {
                    path.RemoveAt(path.Count - 1);
                }
            }

            const int maxItems = 5;

            if (path.Count > maxItems)
            {
                var truncatedPath = new List<FileSystemNodeModel>();
                truncatedPath.Add(path[0]);
                truncatedPath.Add(path[1]);

                truncatedPath.Add(new FileSystemNodeModel("...", NodeType.VirtualDirectory) { IsEnabled = false });

                for (int i = path.Count - 2; i < path.Count; i++)
                {
                    truncatedPath.Add(path[i]);
                }
                path = truncatedPath;
            }

            foreach (var node in path)
            {
                Breadcrumbs.Nodes.Add(node);
            }
        }

        private void Breadcrumbs_NodeClicked(object sender, NodeClickedEventArgs e)
        {
            if (e.Node == null) return;
            TreeUIManager.SelectAndFocusNode(FileTreeView, RootNodes, e.Node, false);
        }

        private async Task HandleAudioBankExpansion(FileSystemNodeModel clickedNode)
        {
            var linkedBank = await AudioBankLinkerService.LinkAudioBankAsync(clickedNode, RootNodes, _currentRootPath);
            if (linkedBank == null)
            {
                return; // Errors are logged by the service
            }

            // Read other file data from the WAD.
            var eventsData = linkedBank.EventsBnkNode != null ? await WadExtractionService.GetVirtualFileBytesAsync(linkedBank.EventsBnkNode) : null;
            byte[] wpkData = linkedBank.WpkNode != null ? await WadExtractionService.GetVirtualFileBytesAsync(linkedBank.WpkNode) : null;
            byte[] audioBnkFileData = linkedBank.AudioBnkNode != null ? await WadExtractionService.GetVirtualFileBytesAsync(linkedBank.AudioBnkNode) : null;

            List<AudioEventNode> audioTree;
            if (linkedBank.BinData != null)
            {
                // BIN-based parsing (Champions, Maps)
                if (wpkData != null)
                {
                    audioTree = AudioBankService.ParseAudioBank(wpkData, audioBnkFileData, eventsData, linkedBank.BinData, linkedBank.BaseName, linkedBank.BinType);
                }
                else
                {
                    audioTree = AudioBankService.ParseSfxAudioBank(audioBnkFileData, eventsData, linkedBank.BinData, linkedBank.BaseName, linkedBank.BinType);
                }
            }
            else
            {
                // Generic parsing (no BIN file)
                audioTree = AudioBankService.ParseGenericAudioBank(wpkData, audioBnkFileData, eventsData);
            }

            // 5. Populate the tree view with the results.
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                clickedNode.Children.Clear();

                // Determine the absolute source WAD path for the child sound nodes.
                string absoluteSourceWadPath;
                if (clickedNode.ChunkDiff != null && (!string.IsNullOrEmpty(NewLolPath) || !string.IsNullOrEmpty(OldLolPath)))
                {
                    // Backup mode: construct the absolute path from the base LoL directory and the relative WAD path.
                    string basePath = clickedNode.ChunkDiff.Type == ChunkDiffType.Removed ? OldLolPath : NewLolPath;
                    absoluteSourceWadPath = Path.Combine(basePath, clickedNode.SourceWadPath);
                }
                else
                {
                    // Normal mode: the SourceWadPath should already be absolute.
                    absoluteSourceWadPath = clickedNode.SourceWadPath;
                }

                foreach (var eventNode in audioTree)
                {
                    var newEventNode = new FileSystemNodeModel(eventNode.Name, NodeType.AudioEvent);
                    foreach (var soundNode in eventNode.Sounds)
                    {
                        // Determine the correct source file (WPK or BNK) for the sound.
                        // This is crucial for the previewer to know where to extract the WEM data from.
                        AudioSourceType sourceType;
                        ulong sourceHash;
                        if (linkedBank.WpkNode != null)
                        {
                            sourceType = AudioSourceType.Wpk;
                            sourceHash = linkedBank.WpkNode.SourceChunkPathHash;
                        }
                        else
                        {
                            sourceType = AudioSourceType.Bnk;
                            sourceHash = linkedBank.AudioBnkNode.SourceChunkPathHash;
                        }

                        var newSoundNode = new FileSystemNodeModel(soundNode.Name, soundNode.Id, soundNode.Offset, soundNode.Size)
                        {
                            SourceWadPath = absoluteSourceWadPath, // Use the resolved absolute path
                            SourceChunkPathHash = sourceHash,
                            AudioSource = sourceType
                        };
                        newEventNode.Children.Add(newSoundNode);
                    }
                    clickedNode.Children.Add(newEventNode);
                }
                clickedNode.IsExpanded = true;
            });
        }

        private void Toolbar_SearchTextChanged(object sender, RoutedEventArgs e)
        {
            _searchTimer.Stop();
            _searchTimer.Start();
        }

        private void Toolbar_CollapseToContainerClicked(object sender, RoutedEventArgs e)
        {
            if (FileTreeView.SelectedItem is not FileSystemNodeModel selectedNode) return;

            var path = TreeUIManager.FindNodePath(RootNodes, selectedNode);
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
                    TreeUIManager.SelectAndFocusNode(FileTreeView, RootNodes, containerNode, false);
                }), DispatcherPriority.ContextIdle);
            }
        }

        private async void SearchTimer_Tick(object sender, EventArgs e)
        {
            _searchTimer.Stop();
            string searchText = Toolbar.SearchText;

            var nodeToSelect = await WadSearchBoxService.PerformSearchAsync(searchText, RootNodes, LoadAllChildrenForSearch);

            if (nodeToSelect != null)
            {
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    TreeUIManager.SelectAndFocusNode(FileTreeView, RootNodes, nodeToSelect, false);
                }), DispatcherPriority.ContextIdle);
            }
            else
            {
                var selectedNode = FileTreeView.SelectedItem as FileSystemNodeModel;
                if (selectedNode != null && string.IsNullOrEmpty(searchText))
                {
                    _ = Dispatcher.BeginInvoke(new Action(() =>
                    {
                        TreeUIManager.SelectAndFocusNode(FileTreeView, RootNodes, selectedNode);
                    }), DispatcherPriority.ContextIdle);
                }
            }
        }

        private async Task LoadAllChildrenForSearch(FileSystemNodeModel node)
        {
            await TreeBuilderService.LoadAllChildren(node, _currentRootPath);
        }
    }
}
