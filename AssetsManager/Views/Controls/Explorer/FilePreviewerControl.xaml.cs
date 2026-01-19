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

        public FilePreviewerModel ViewModel { get; set; }
        private bool _isLoaded = false;

        private FileSystemNodeModel _currentNode;
        private FileSystemNodeModel _currentFolderNode;
        private ObservableCollection<FileSystemNodeModel> _rootNodes;
        private bool _isShowingTemporaryPreview = false;
        private string _currentSearchFilter = string.Empty;

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
                if (_isShowingTemporaryPreview) return;

                var selectedPin = ViewModel.PinnedFilesManager.SelectedFile;

                if (selectedPin == null)
                {
                    if (!_isShowingTemporaryPreview)
                    {
                        await ExplorerPreviewService.ResetPreviewAsync();
                    }
                    return;
                }
                
                SwitchToFilePreview();
                UpdateBreadcrumbs(selectedPin.Node);

                if (selectedPin.IsDetailsTab)
                {
                    await ExplorerPreviewService.ResetPreviewAsync();
                    DetailsPreview.DataContext = selectedPin.Node;
                    ViewModel.IsDetailsVisible = true;
                }
                else
                {
                    ViewModel.IsDetailsVisible = false;
                    await ExplorerPreviewService.ShowPreviewAsync(selectedPin.Node);
                }
            }
        }

        private void Tab_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is PinnedFileModel vm)
            {
                ViewModel.PinnedFilesManager.SelectedFile = vm;
            }
        }

        private async void CloseTabButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is PinnedFileModel vm)
            {
                if (ViewModel.PinnedFilesManager.SelectedFile == vm)
                {
                    await ExplorerPreviewService.ResetPreviewAsync();
                }
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
                    PreviewStatusPanel,
                    SelectFileStatusPanel,
                    UnsupportedStatusPanel,
                    UnsupportedFileMessage,
                    DetailsPreview,
                    ViewModel
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
                
                ViewModel.PinnedFilesManager.PropertyChanged -= PinnedFilesManager_PropertyChanged;
                ViewModel.PinnedFilesManager.PinnedFiles.CollectionChanged -= PinnedFiles_CollectionChanged;
                
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

            var existingPin = ViewModel.PinnedFilesManager.PinnedFiles.FirstOrDefault(p => p.Node == node && !p.IsDetailsTab);

            if (existingPin != null)
            {
                if (ViewModel.PinnedFilesManager.SelectedFile == existingPin)
                {
                    await ExplorerPreviewService.ShowPreviewAsync(node);
                }
                else
                {
                    ViewModel.PinnedFilesManager.SelectedFile = existingPin;
                }
            }
            else
            {
                _isShowingTemporaryPreview = true;
                ViewModel.PinnedFilesManager.SelectedFile = null;

                DetailsPreview.Visibility = Visibility.Collapsed;

                await ExplorerPreviewService.ShowPreviewAsync(node);
                _isShowingTemporaryPreview = false;
            }
        }

        public void UpdateAndEnsureSingleDetailsTab(FileSystemNodeModel node)
        {
            var existingDetailsPin = ViewModel.PinnedFilesManager.PinnedFiles.FirstOrDefault(p => p.IsDetailsTab);

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
                ViewModel.PinnedFilesManager.PinnedFiles.Add(newDetailsPin);
            }
        }

        public async Task ResetToDefaultState()
        {
            ViewModel.IsGridMode = false;
            
            UpdateSelectedNode(null, null);
            await ExplorerPreviewService.ResetPreviewAsync();
        }

        public void UpdateSelectedNode(FileSystemNodeModel node, ObservableCollection<FileSystemNodeModel> rootNodes)
        {
            _currentNode = node;
            _rootNodes = rootNodes;
            ViewModel.HasSelectedNode = node != null;
            UpdateBreadcrumbs(node);

            if (node != null && (node.Type == NodeType.VirtualDirectory || node.Type == NodeType.RealDirectory || node.Type == NodeType.WadFile || node.Type == NodeType.SoundBank || node.Type == NodeType.AudioEvent))
            {
                _currentFolderNode = node;

                var gridItems = new ObservableCollection<FileGridViewModel>(
                    (!string.IsNullOrEmpty(_currentSearchFilter)
                        ? node.Children.Where(c => c.Name.IndexOf(_currentSearchFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                        : node.Children)
                    .Select(n => new FileGridViewModel(n)));

                FileGridView.ItemsSource = gridItems;

                if (!ViewModel.IsGridMode)
                {
                    _ = ExplorerPreviewService.ResetPreviewAsync();
                }
                else
                {
                    SwitchToFolderView();
                }

                foreach (var vm in gridItems)
                {
                    if (SupportedFileTypes.Images.Contains(vm.Node.Extension) || SupportedFileTypes.Textures.Contains(vm.Node.Extension))
                    {
                        _ = LoadImagePreviewAsync(vm);
                    }
                }
            }
            else
            {
                SwitchToFilePreview();
            }
        }

        private void SwitchToFolderView()
        {
            ViewModel.IsGridMode = true; 
        }

        private void SwitchToFilePreview()
        {
            ViewModel.IsGridMode = false;
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

        private void FileGridView_NodeClicked(object sender, NodeClickedEventArgs e)
        {
            BreadcrumbNodeClicked?.Invoke(this, new NodeClickedEventArgs(e.Node));
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