using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using AssetsManager.Services.Core;
using AssetsManager.Services.Explorer;
using AssetsManager.Services.Explorer.Tree;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Explorer;
using AssetsManager.Views.Controls.Explorer;
using AssetsManager.Views.Models.Wad;
using NodeClickedEventArgs = AssetsManager.Views.Controls.Explorer.NodeClickedEventArgs;

namespace AssetsManager.Views.Controls.Explorer
{
    public partial class FilePreviewerControl : UserControl
    {
        public LogService LogService { get; set; }
        public CustomMessageBoxService CustomMessageBoxService { get; set; }
        public DirectoriesCreator DirectoriesCreator { get; set; }
        public ExplorerPreviewService ExplorerPreviewService { get; set; }
        public TreeUIManager TreeUIManager { get; set; }

        public event EventHandler<NodeClickedEventArgs> BreadcrumbNodeClicked;
        public event EventHandler<SelectionActionEventArgs> SelectionActionRequested;

        public FilePreviewerModel ViewModel { get; set; }
        private bool _isLoaded = false;

        private FileSystemNodeModel _currentNode;
        private FileSystemNodeModel _currentFolderNode;
        private ObservableRangeCollection<FileSystemNodeModel> _rootNodes;
        private string _currentSearchFilter = string.Empty;
        private CancellationTokenSource _thumbnailCts;

        public FilePreviewerControl()
        {
            InitializeComponent();
            ViewModel = new FilePreviewerModel();
            
            // Subscriptions
            ViewModel.PinnedFilesManager.PinnedFiles.CollectionChanged += PinnedFiles_CollectionChanged;
            ViewModel.PinnedFilesManager.PropertyChanged += PinnedFilesManager_PropertyChanged;
            
            this.DataContext = ViewModel;
            
            this.Loaded += FilePreviewerControl_Loaded;
            this.Unloaded += FilePreviewerControl_Unloaded;
        }

        public void SetSearchFilter(string searchText)
        {
            _currentSearchFilter = searchText;
            if (ViewModel.IsGridMode && _currentFolderNode != null)
            {
                UpdateSelectedNode(_currentFolderNode, _rootNodes);
            }
        }

        public void SetViewMode(bool isGridMode)
        {
            if (ViewModel.IsGridMode != isGridMode)
            {
                ViewModel.IsGridMode = isGridMode;
            }

            if (ViewModel.IsGridMode)
            {
                if (_currentFolderNode != null)
                {
                    UpdateSelectedNode(_currentFolderNode, _rootNodes);
                }
            }
            else // Preview Mode
            {
                if (_currentNode != null && _currentNode != _currentFolderNode && !ViewModel.IsSelectedNodeContainer)
                {
                    _ = ShowPreviewAsync(_currentNode);
                }
                else if (!ViewModel.HasEverPreviewedAFile)
                {
                    _ = ExplorerPreviewService.ResetPreviewAsync();
                }
            }
        }

        private void PinnedFiles_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            UpdateScrollButtonsVisibility();
        }

        private void ScrollLeftButton_Click(object sender, RoutedEventArgs e)
        {
            TabsScrollViewer.ScrollToHorizontalOffset(TabsScrollViewer.HorizontalOffset - TabsScrollViewer.ActualWidth);
        }

        private void ScrollRightButton_Click(object sender, RoutedEventArgs e)
        {
            TabsScrollViewer.ScrollToHorizontalOffset(TabsScrollViewer.HorizontalOffset + TabsScrollViewer.ActualWidth);
        }

        private void TabsScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            UpdateScrollButtonsVisibility();
        }

        private void UpdateScrollButtonsVisibility()
        {
            if (TabsScrollViewer.ScrollableWidth > 0)
            {
                ViewModel.CanScrollLeft = TabsScrollViewer.HorizontalOffset > 0;
                ViewModel.CanScrollRight = TabsScrollViewer.HorizontalOffset < TabsScrollViewer.ScrollableWidth;
            }
            else
            {
                ViewModel.CanScrollLeft = false;
                ViewModel.CanScrollRight = false;
            }
        }

        private void FilePreviewerControl_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (TextEditorPreview.Visibility == Visibility.Visible)
                {
                    ViewModel.IsFindVisible = true;
                    FindInDocumentControl.FocusInput();
                    e.Handled = true;
                }
            }
        }

        private void FindInDocumentControl_Close(object sender, RoutedEventArgs e)
        {
            ViewModel.IsFindVisible = false;
            TextEditorPreview.Focus();
        }

        private async void PinnedFilesManager_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PinnedFilesManager.SelectedFile))
            {
                var selectedPin = ViewModel.PinnedFilesManager.SelectedFile;

                if (selectedPin == null)
                {
                    // If no pins are left, we MUST reset the service state so it "forgets" the last file
                    // This prevents the bug where re-opening the same file shows Welcome instead of content.
                    if (ViewModel.PinnedFilesManager.PinnedFiles.Count == 0)
                    {
                        await ExplorerPreviewService.ResetPreviewAsync();

                        if (ViewModel.IsGridMode && _currentFolderNode != null)
                        {
                            UpdateSelectedNode(_currentFolderNode, _rootNodes);
                        }
                        else
                        {
                            _currentNode = null;
                        }
                    }
                    return;
                }

                _currentNode = selectedPin.Node;
                ViewModel.HasSelectedNode = true;
                ViewModel.IsSelectedNodeContainer = false;

                ViewModel.RenamedDiffDetails = selectedPin.Node?.ChunkDiff;

                UpdateBreadcrumbs(selectedPin.Node);

                // Important: Always call the service with the LATEST node from the pin
                await ExplorerPreviewService.ShowPreviewAsync(selectedPin.Node);
            }
        }

        private void Tab_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is PinnedFileModel vm)
            {
                ViewModel.PinnedFilesManager.SelectedFile = vm;
            }
        }

        private void CloseTabButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is PinnedFileModel vm)
            {
                // 1. First check the category logic to see if panels should hide
                ViewModel.ClosePanelByCategory(vm.Node);
                
                // 2. Unpin the file. The manager will automatically select the previous tab if this was the active one.
                ViewModel.PinnedFilesManager.UnpinFile(vm);
            }
        }

        private void FilePreviewerControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (_isLoaded) return;

            try
            {
                ExplorerPreviewService.Initialize(
                    ImagePreview,
                    WebViewContainer,
                    TextEditorPreview,
                    ViewModel
                );

                Breadcrumbs.NodeClicked += Breadcrumbs_NodeClicked;
                FileGridControl.NodeClicked += FileGridControl_NodeClicked;
                FileGridControl.SelectionActionRequested += FileGridControl_SelectionActionRequested;
                UpdateScrollButtonsVisibility();

                _isLoaded = true;
            }
            catch (Exception ex)
            {
                LogService.LogError(ex, "Error loading FilePreviewerControl");
            }
        }

        private async void FilePreviewerControl_Unloaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _thumbnailCts?.Cancel();
                _thumbnailCts?.Dispose();
                _thumbnailCts = null;

                await ExplorerPreviewService.ResetPreviewAsync();
                
                ViewModel.PinnedFilesManager.PropertyChanged -= PinnedFilesManager_PropertyChanged;
                ViewModel.PinnedFilesManager.PinnedFiles.CollectionChanged -= PinnedFiles_CollectionChanged;
                
                Breadcrumbs.NodeClicked -= Breadcrumbs_NodeClicked;
                FileGridControl.NodeClicked -= FileGridControl_NodeClicked;
                FileGridControl.SelectionActionRequested -= FileGridControl_SelectionActionRequested;
            }
            catch (Exception ex)
            {
                LogService.LogError(ex, "Error cleaning WebView2 on unload");
            }
        }

        public async Task ShowPreviewAsync(FileSystemNodeModel node)
        {
            if (node == null) return;

            _currentNode = node;

            // ONLY auto-pin if it's a previewable file (not a folder or container)
            bool isContainer = node.Type == NodeType.RealDirectory || node.Type == NodeType.VirtualDirectory || node.Type == NodeType.WadFile || node.Type == NodeType.SoundBank || node.Type == NodeType.AudioEvent;

            if (!isContainer)
            {
                // Check if the file is already pinned
                var existingPin = ViewModel.PinnedFilesManager.PinnedFiles.FirstOrDefault(p => p.Node == node);

                if (existingPin == null)
                {
                    // Auto-pin the file so it appears in the tabs and can be closed/managed
                    ViewModel.PinnedFilesManager.PinFile(node);
                    existingPin = ViewModel.PinnedFilesManager.PinnedFiles.FirstOrDefault(p => p.Node == node);
                }

                // Select the tab (this will trigger PinnedFilesManager_PropertyChanged which calls the service)
                if (existingPin != null)
                {
                    ViewModel.PinnedFilesManager.SelectedFile = existingPin;
                }
            }
            else
            {
                // For containers, we just call the service to handle potential keep-alive logic
                await ExplorerPreviewService.ShowPreviewAsync(node);
            }
        }

        public async Task ResetToDefaultState()
        {
            ViewModel.IsGridMode = false;
            
            UpdateSelectedNode(null, null);
            await ExplorerPreviewService.ResetPreviewAsync();
        }

        public void UpdateSelectedNode(FileSystemNodeModel node, ObservableRangeCollection<FileSystemNodeModel> rootNodes)
        {
            _currentNode = node;
            _rootNodes = rootNodes;
            ViewModel.HasSelectedNode = node != null;

            ViewModel.RenamedDiffDetails = node?.ChunkDiff;

            UpdateBreadcrumbs(node);

            bool isContainer = node != null && (node.Type == NodeType.VirtualDirectory || node.Type == NodeType.RealDirectory || node.Type == NodeType.WadFile || node.Type == NodeType.SoundBank || node.Type == NodeType.AudioEvent);
            ViewModel.IsSelectedNodeContainer = isContainer;

            if (isContainer)
            {
                _currentFolderNode = node;

                var gridItems = new ObservableRangeCollection<FileGridViewModel>(
                    (!string.IsNullOrEmpty(_currentSearchFilter)
                        ? node.Children.Where(c => c.Name.IndexOf(_currentSearchFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                        : node.Children)
                    .Select(n => new FileGridViewModel(n)));

                FileGridControl.ItemsSource = gridItems;
                
                // Calculate and set folder analytics
                UpdateFolderAnalytics(node);

                if (!ViewModel.IsGridMode && !ViewModel.HasEverPreviewedAFile)
                {
                    _ = ExplorerPreviewService.ResetPreviewAsync();
                }

                // Sequential Loading Queue with Cancellation
                _thumbnailCts?.Cancel();
                _thumbnailCts = new CancellationTokenSource();
                _ = LoadThumbnailsQueueAsync(gridItems, _thumbnailCts.Token);
            }
            else if (node != null)
            {
                // If it's a file, hide status messages immediately to avoid flickers during load
                ViewModel.IsWelcomeVisible = false;
                ViewModel.IsUnsupportedVisible = false;
            }
        }

        private void UpdateFolderAnalytics(FileSystemNodeModel folderNode)
        {
            if (folderNode == null || folderNode.Children == null) return;

            var analytics = new FileGridAnalyticsModel();
            var children = folderNode.Children;

            int total = 0;
            int imgCount = 0, audioCount = 0, modelCount = 0, dataCount = 0;
            long imgSize = 0, audioSize = 0, dataSize = 0;

            foreach (var c in children)
            {
                if (c.Name.Equals("Loading...")) continue;
                total++;

                string ext = c.Extension;
                long size = (long)(c.ChunkDiff?.NewUncompressedSize ?? 0);

                if (SupportedFileTypes.Images.Contains(ext) || SupportedFileTypes.Textures.Contains(ext) || SupportedFileTypes.VectorImages.Contains(ext))
                {
                    imgCount++;
                    imgSize += size;
                }
                else if (SupportedFileTypes.AudioBank.Contains(ext) || SupportedFileTypes.Media.Contains(ext))
                {
                    audioCount++;
                    audioSize += size;
                }
                else if (SupportedFileTypes.Viewer3D.Contains(ext))
                {
                    modelCount++;
                }
                else if (SupportedFileTypes.Bin.Contains(ext) || SupportedFileTypes.Json.Contains(ext) || SupportedFileTypes.StringTable.Contains(ext) || 
                         SupportedFileTypes.PlainText.Contains(ext) || SupportedFileTypes.Css.Contains(ext) || SupportedFileTypes.JavaScript.Contains(ext) ||
                         SupportedFileTypes.Troybin.Contains(ext) || SupportedFileTypes.Preload.Contains(ext))
                {
                    dataCount++;
                    dataSize += size;
                }
            }

            analytics.TotalFiles = total;
            analytics.ImageCount = imgCount;
            analytics.ImageSize = imgSize;
            analytics.AudioCount = audioCount;
            analytics.AudioSize = audioSize;
            analytics.ModelCount = modelCount;
            analytics.DataCount = dataCount;
            analytics.DataSize = dataSize;

            FileGridControl.Analytics = analytics;
        }

        private async Task LoadThumbnailsQueueAsync(ObservableRangeCollection<FileGridViewModel> items, CancellationToken ct)
        {
            foreach (var vm in items)
            {
                if (ct.IsCancellationRequested) return;

                if (SupportedFileTypes.Images.Contains(vm.Node.Extension) || 
                    SupportedFileTypes.Textures.Contains(vm.Node.Extension) ||
                    SupportedFileTypes.VectorImages.Contains(vm.Node.Extension))
                {
                    await LoadImagePreviewAsync(vm);
                }
            }
        }

        private async Task LoadImagePreviewAsync(FileGridViewModel vm)
        {
            if (vm.ImagePreview != null) return;

            var image = await ExplorerPreviewService.GetImagePreviewAsync(vm.Node);
            if (image != null)
            {
                vm.ImagePreview = image;
            }
        }

        private void FileGridControl_NodeClicked(object sender, NodeClickedEventArgs e)
        {
            BreadcrumbNodeClicked?.Invoke(this, new NodeClickedEventArgs(e.Node));
        }

        private void FileGridControl_SelectionActionRequested(object sender, SelectionActionEventArgs e)
        {
            SelectionActionRequested?.Invoke(this, e);
        }
        
        public void SetBreadcrumbToggleState(bool isToggleOn)
        {
            ViewModel.IsBreadcrumbToggleOn = isToggleOn;
        }

        private void UpdateBreadcrumbs(FileSystemNodeModel selectedNode)
        {
            Breadcrumbs.Nodes.Clear();
            if (selectedNode == null || _rootNodes == null) return;

            var path = TreeUIManager.FindNodePath(_rootNodes, selectedNode);
            if (path == null) return;

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
            BreadcrumbNodeClicked?.Invoke(this, e);
        }
    }
}
