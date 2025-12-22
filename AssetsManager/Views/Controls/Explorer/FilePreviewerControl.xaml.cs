using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
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
        public event EventHandler<bool> ViewModeChanged; // True for Grid, False for Preview

        public PinnedFilesManager ViewModel { get; set; }
        private bool _isLoaded = false;

        private FileSystemNodeModel _currentNode;
        private FileSystemNodeModel _currentFolderNode;
        private ObservableCollection<FileSystemNodeModel> _rootNodes;
        private bool _isShowingTemporaryPreview = false;
        private bool _isGridMode = false;

        public FilePreviewerControl()
        {
            InitializeComponent();
            ViewModel = new PinnedFilesManager();
            ViewModel.PinnedFiles.CollectionChanged += PinnedFiles_CollectionChanged;
            this.DataContext = ViewModel;
            this.Loaded += FilePreviewerControl_Loaded;
            this.Unloaded += FilePreviewerControl_Unloaded;
            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        }

        public void SetViewMode(bool isGridMode)
        {
            if (_isGridMode != isGridMode)
            {
                _isGridMode = isGridMode;
                // No need to fire event here because this is called BY the parent
            }
            // Force update regardless to ensure UI sync
            _isGridMode = isGridMode;

            if (_isGridMode)
            {
                if (_currentFolderNode != null)
                {
                    // This will also set the ItemsSource if it's a new folder
                    UpdateSelectedNode(_currentFolderNode, _rootNodes);
                }
            }
            else // Preview Mode
            {
                SwitchToFilePreview();
                if (_currentNode != null && _currentNode != _currentFolderNode)
                {
                    _ = ShowPreviewAsync(_currentNode);
                }
                else
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
                ScrollLeftButton.Visibility = TabsScrollViewer.HorizontalOffset > 0 ? Visibility.Visible : Visibility.Collapsed;
                ScrollRightButton.Visibility = TabsScrollViewer.HorizontalOffset < TabsScrollViewer.ScrollableWidth ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                ScrollLeftButton.Visibility = Visibility.Collapsed;
                ScrollRightButton.Visibility = Visibility.Collapsed;
            }
        }

        private void FilePreviewerControl_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (TextEditorPreview.Visibility == Visibility.Visible)
                {
                    FindInDocumentControl.Visibility = Visibility.Visible;
                    FindInDocumentControl.FocusInput();
                    e.Handled = true;
                }
            }
        }

        private void FindInDocumentControl_Close(object sender, RoutedEventArgs e)
        {
            FindInDocumentControl.Visibility = Visibility.Collapsed;
            TextEditorPreview.Focus();
        }

        private async void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewModel.SelectedFile))
            {
                if (_isShowingTemporaryPreview) return;

                var selectedPin = ViewModel.SelectedFile;

                if (selectedPin == null)
                {
                    if (!_isShowingTemporaryPreview)
                    {
                        await ExplorerPreviewService.ResetPreviewAsync();
                    }
                    return;
                }
                
                // When a pin is selected, use the helper to ensure the view and buttons are synced
                SwitchToFilePreview();

                if (selectedPin.IsDetailsTab)
                {
                    await ExplorerPreviewService.ResetPreviewAsync();
                    DetailsPreview.DataContext = selectedPin.Node;
                    DetailsPreview.Visibility = Visibility.Visible;
                    PreviewPlaceholder.Visibility = Visibility.Collapsed; // Hide the placeholder
                }
                else
                {
                    DetailsPreview.Visibility = Visibility.Collapsed;
                    await ExplorerPreviewService.ShowPreviewAsync(selectedPin.Node);
                }
            }
        }

        private void Tab_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is PinnedFileModel vm)
            {
                ViewModel.SelectedFile = vm;
            }
        }

        private async void CloseTabButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is PinnedFileModel vm)
            {
                if (ViewModel.SelectedFile == vm)
                {
                    await ExplorerPreviewService.ResetPreviewAsync();
                }
                ViewModel.UnpinFile(vm);
            }
        }

        private void FilePreviewerControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (_isLoaded) return;

            try
            {
                ExplorerPreviewService.Initialize(
                    ImagePreview,
                    WebViewContainer, // Pass the container grid
                    TextEditorPreview,
                    PreviewPlaceholder,
                    SelectFileMessagePanel,
                    UnsupportedFileMessagePanel,
                    UnsupportedFileMessage,
                    DetailsPreview
                );

                Breadcrumbs.NodeClicked += Breadcrumbs_NodeClicked;
                FileGridView.NodeClicked += FileGridView_NodeClicked;
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
                await ExplorerPreviewService.ResetPreviewAsync();
                ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
                ViewModel.PinnedFiles.CollectionChanged -= PinnedFiles_CollectionChanged;
                Breadcrumbs.NodeClicked -= Breadcrumbs_NodeClicked;
                FileGridView.NodeClicked -= FileGridView_NodeClicked;
            }
            catch (Exception ex)
            {
                LogService.LogError(ex, "Error cleaning WebView2 on unload");
            }
        }

        public async Task ShowPreviewAsync(FileSystemNodeModel node)
        {
            _currentNode = node;

            var existingPin = ViewModel.PinnedFiles.FirstOrDefault(p => p.Node == node && !p.IsDetailsTab);

            if (existingPin != null)
            {
                // If the pin is already selected, the PropertyChanged event won't fire.
                // So, we need to explicitly show the preview content again.
                if (ViewModel.SelectedFile == existingPin)
                {
                    await ExplorerPreviewService.ShowPreviewAsync(node);
                }
                else
                {
                    // If a different pin was selected, setting the property will trigger the update.
                    ViewModel.SelectedFile = existingPin;
                }
            }
            else
            {
                _isShowingTemporaryPreview = true;
                ViewModel.SelectedFile = null;

                DetailsPreview.Visibility = Visibility.Collapsed;

                await ExplorerPreviewService.ShowPreviewAsync(node);
                _isShowingTemporaryPreview = false;
            }
        }

        public void UpdateAndEnsureSingleDetailsTab(FileSystemNodeModel node)
        {
            var existingDetailsPin = ViewModel.PinnedFiles.FirstOrDefault(p => p.IsDetailsTab);

            if (existingDetailsPin != null)
            {
                existingDetailsPin.Node = node;
            }
            else
            {
                var newDetailsPin = new PinnedFileModel(node)
                {
                    IsDetailsTab = true
                };
                ViewModel.PinnedFiles.Add(newDetailsPin);
            }
        }

        public async Task ResetToDefaultState()
        {
            FileGridView.Visibility = Visibility.Collapsed;
            PreviewContainer.Visibility = Visibility.Visible;
            _isGridMode = false;
            UpdateSelectedNode(null, null);
            await ExplorerPreviewService.ResetPreviewAsync();
        }

        public void UpdateSelectedNode(FileSystemNodeModel node, ObservableCollection<FileSystemNodeModel> rootNodes)
        {
            _currentNode = node;
            _rootNodes = rootNodes;
            UpdateBreadcrumbs(node);

            if (node != null && (node.Type == NodeType.VirtualDirectory || node.Type == NodeType.RealDirectory || node.Type == NodeType.WadFile))
            {
                _currentFolderNode = node; // Track the last folder
                FileGridView.ItemsSource = node.Children;

                // If the user is on Preview mode, do not switch to GridView automatically.
                if (!_isGridMode)
                {
                    // A folder is selected, so reset the preview to show the default placeholder.
                    _ = ExplorerPreviewService.ResetPreviewAsync();
                }
                else
                {
                    // Otherwise, the user is in GridView, so show the folder's content.
                    SwitchToFolderView();
                }

                // Asynchronously load image previews for the children
                foreach (var child in node.Children)
                {
                    if (SupportedFileTypes.Images.Contains(child.Extension) || SupportedFileTypes.Textures.Contains(child.Extension))
                    {
                        // Fire and forget
                        _ = LoadImagePreviewAsync(child);
                    }
                }
            }
            else
            {
                SwitchToFilePreview();
                // We do NOT force _isGridMode to false here anymore.
                // If the user was in Grid Mode, we show the file, but we keep the "Grid" state active
                // so when they click a folder again, it goes back to Grid view automatically.
            }
        }

        private void SwitchToFolderView()
        {
            PreviewContainer.Visibility = Visibility.Collapsed;
            FileGridView.Visibility = Visibility.Visible;
        }

        private void SwitchToFilePreview()
        {
            FileGridView.Visibility = Visibility.Collapsed;
            PreviewPlaceholder.Visibility = Visibility.Collapsed; // Hide placeholder to prevent flicker
            PreviewContainer.Visibility = Visibility.Visible;
        }


        private async Task LoadImagePreviewAsync(FileSystemNodeModel node)
        {
            if (node.ImagePreview != null) return; // Already loaded

            var image = await ExplorerPreviewService.GetImagePreviewAsync(node);
            if (image != null)
            {
                node.ImagePreview = image;
            }
        }

        private async void FileGridView_NodeClicked(object sender, NodeClickedEventArgs e)
        {
            var node = e.Node;
            if (node.Type == NodeType.VirtualDirectory || node.Type == NodeType.RealDirectory || node.Type == NodeType.WadFile)
            {
                BreadcrumbNodeClicked?.Invoke(this, new NodeClickedEventArgs(node));
            }
            else
            {
                SwitchToFilePreview();
                // Removed _isGridMode = false and ViewModeChanged invoke
                // to keep the Grid View toggle state active.
                await ShowPreviewAsync(node);
            }
        }
        
        public void SetBreadcrumbVisibility(Visibility visibility)
        {
            Breadcrumbs.Visibility = visibility;
            BreadcrumbSeparator.Visibility = visibility;
        }

        private void UpdateBreadcrumbs(FileSystemNodeModel selectedNode)
        {
            Breadcrumbs.Nodes.Clear();
            if (selectedNode == null) return;

            var path = TreeUIManager.FindNodePath(_rootNodes, selectedNode);
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
            BreadcrumbNodeClicked?.Invoke(this, e);
        }
    }
}
