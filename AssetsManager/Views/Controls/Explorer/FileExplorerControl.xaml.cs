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
using Microsoft.WindowsAPICodePack.Dialogs;
using AssetsManager.Services.Core;
using AssetsManager.Services.Explorer;
using AssetsManager.Services.Audio;
using AssetsManager.Services.Explorer.Tree;
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
        public MenuItem ExtractMenuItem => (this.FindResource("ExplorerContextMenu") as ContextMenu)?.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "ExtractMenuItem");

        // Injected Services
        public LogService LogService { get; set; }
        public CustomMessageBoxService CustomMessageBoxService { get; set; }
        public WadExtractionService WadExtractionService { get; set; }
        public WadSearchBoxService WadSearchBoxService { get; set; }
        public DiffViewService DiffViewService { get; set; }
        public DirectoriesCreator DirectoriesCreator { get; set; }
        public AppSettings AppSettings { get; set; }
        public TreeBuilderService TreeBuilderService { get; set; }
        public TreeUIManager TreeUIManager { get; set; }
        public AudioBankService AudioBankService { get; set; }
        public AudioBankLinkerService AudioBankLinkerService { get; set; }

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

            if (_isWadMode && !string.IsNullOrEmpty(AppSettings.LolDirectory) && Directory.Exists(AppSettings.LolDirectory))
            {
                await BuildWadTreeAsync(AppSettings.LolDirectory);
            }
            else
            {
                await ReloadTreeAsync();
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
                    RootNodes.Clear();
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
                    RootNodes.Clear();
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

        private async Task BuildWadTreeAsync(string rootPath)
        {
            _currentRootPath = rootPath;
            NoDirectoryMessage.Visibility = Visibility.Collapsed;
            FileTreeView.Visibility = Visibility.Collapsed;
            LoadingIndicator.Visibility = Visibility.Visible;

            try
            {
                var newNodes = await TreeBuilderService.BuildWadTreeAsync(rootPath);
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

            var cts = new CancellationTokenSource();
            var indicatorTask = Task.Delay(100, cts.Token).ContinueWith(t =>
            {
                if (!t.IsCanceled)
                {
                    Dispatcher.Invoke(() => LoadingIndicator.Visibility = Visibility.Visible);
                }
            }, TaskScheduler.Default);

            try
            {
                var newNodes = await TreeBuilderService.BuildDirectoryTreeAsync(rootPath);
                RootNodes.Clear();
                foreach (var node in newNodes)
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
                cts.Cancel();
                LoadingIndicator.Visibility = Visibility.Collapsed;
                FileTreeView.Visibility = Visibility.Visible;
                Toolbar.Visibility = Visibility.Visible;
                ToolbarSeparator.Visibility = Visibility.Visible;
            }
        }

        private async Task BuildTreeFromBackupAsync(string jsonPath)
        {
            NoDirectoryMessage.Visibility = Visibility.Collapsed;
            FileTreeView.Visibility = Visibility.Collapsed;
            LoadingIndicator.Visibility = Visibility.Visible;

            try
            {
                var backupNodes = await TreeBuilderService.BuildTreeFromBackupAsync(jsonPath);
                RootNodes.Clear();
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
                var dialog = new CommonOpenFileDialog { IsFolderPicker = true, Title = "Select Destination Folder" };
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

            if (ExtractMenuItem is not null)
            {
                ExtractMenuItem.IsEnabled = _isWadMode;
            }

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
            if (TreeUIManager.SafeVisualUpwardSearch(e.OriginalSource as DependencyObject) is TreeViewItem treeViewItem)
            {
                treeViewItem.IsSelected = true;
                e.Handled = true;
            }
        }

        private async void SelectLolDirButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new CommonOpenFileDialog { IsFolderPicker = true, Title = "Select the League of Legends Directory" };

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

        private async void FileTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is FileSystemNodeModel selectedNode)
            {
                bool isAudioBank = SupportedFileTypes.AudioBank.Contains(selectedNode.Extension) && selectedNode.Name.Contains("_audio");
                if (isAudioBank && selectedNode.Children.Count == 1 && selectedNode.Children[0].Name == "Loading...")
                {
                    await HandleAudioBankExpansion(selectedNode);
                }
                else
                {
                    FileSelected?.Invoke(this, e);
                }
            }
        }

        private async Task HandleAudioBankExpansion(FileSystemNodeModel clickedNode)
        {
            var linkedBank = await AudioBankLinkerService.LinkAudioBankAsync(clickedNode, RootNodes, _currentRootPath);
            if (linkedBank == null)
            {
                return; // Errors are logged by the service
            }

            bool isVo = clickedNode.Name.Contains("_vo_audio");

            // Read other file data from the WAD.
            var eventsData = linkedBank.EventsBnkNode != null ? await WadExtractionService.GetVirtualFileBytesAsync(linkedBank.EventsBnkNode) : null;
            byte[] wpkData = (isVo && linkedBank.WpkNode != null) ? await WadExtractionService.GetVirtualFileBytesAsync(linkedBank.WpkNode) : null;
            byte[] audioBnkFileData = linkedBank.AudioBnkNode != null ? await WadExtractionService.GetVirtualFileBytesAsync(linkedBank.AudioBnkNode) : null;

            if (eventsData == null)
            {
                LogService.LogError(null, "Failed to read required data for events file.");
                return;
            }

            // 4. Call the appropriate service method to parse the data and build the audio tree.
            List<AudioEventNode> audioTree;
            if (isVo)
            {
                audioTree = AudioBankService.ParseAudioBank(wpkData, audioBnkFileData, eventsData, linkedBank.BinData, linkedBank.BaseName, linkedBank.BinType);
            }
            else
            {
                audioTree = AudioBankService.ParseSfxAudioBank(audioBnkFileData, eventsData, linkedBank.BinData, linkedBank.BaseName, linkedBank.BinType);
            }

            // 5. Populate the tree view with the results.
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                clickedNode.Children.Clear();
                foreach (var eventNode in audioTree)
                {
                    var newEventNode = new FileSystemNodeModel(eventNode.Name, NodeType.AudioEvent);
                    foreach (var soundNode in eventNode.Sounds)
                    {
                        // Determine the correct source file (WPK or BNK) for the sound.
                        // This is crucial for the previewer to know where to extract the WEM data from.
                        AudioSourceType sourceType;
                        ulong sourceHash;
                        if (isVo)
                        {
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
                        }
                        else
                        {
                            sourceType = AudioSourceType.Bnk;
                            sourceHash = linkedBank.AudioBnkNode.SourceChunkPathHash;
                        }

                        var newSoundNode = new FileSystemNodeModel(soundNode.Name, soundNode.Id, soundNode.Offset, soundNode.Size)
                        {
                            SourceWadPath = clickedNode.SourceWadPath,
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
