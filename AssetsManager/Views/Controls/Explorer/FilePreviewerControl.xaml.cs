using System;
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
using AssetsManager.Utils;
using AssetsManager.Views.Models.Explorer;

namespace AssetsManager.Views.Controls.Explorer
{
    public partial class FilePreviewerControl : UserControl
    {
        public LogService LogService { get; set; }
        public CustomMessageBoxService CustomMessageBoxService { get; set; }
        public DirectoriesCreator DirectoriesCreator { get; set; }
        public ExplorerPreviewService ExplorerPreviewService { get; set; }

        public PinnedFilesManager ViewModel { get; set; }
        private bool _isLoaded = false;

        private FileSystemNodeModel _currentNode;
        private bool _isShowingTemporaryPreview = false;

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
                ViewModel.PropertyChanged -= ViewModel_PropertyChanged; // Unsubscribe to prevent memory leaks
                ViewModel.PinnedFiles.CollectionChanged -= PinnedFiles_CollectionChanged;
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
                ViewModel.SelectedFile = existingPin;
            }
            else
            {
                _isShowingTemporaryPreview = true;
                ViewModel.SelectedFile = null;

                bool wasDetailsVisible = DetailsPreview.Visibility == Visibility.Visible;

                DetailsPreview.Visibility = Visibility.Collapsed;

                if (wasDetailsVisible)
                {
                    PreviewPlaceholder.Visibility = Visibility.Visible;
                }

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
    }
}
