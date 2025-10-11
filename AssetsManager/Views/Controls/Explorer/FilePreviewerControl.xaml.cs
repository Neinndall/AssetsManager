using System;
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
using AssetsManager.Views.Models;

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
            this.DataContext = ViewModel;
            this.Loaded += FilePreviewerControl_Loaded;
            this.Unloaded += FilePreviewerControl_Unloaded;
            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        }

        private async void TryPreviewWithoutExt_Click(object sender, RoutedEventArgs e)
        {
            if (_currentNode == null || ExtensionComboBox.SelectedItem == null) return;

            var selectedExtension = ExtensionComboBox.SelectedItem.ToString();

            var tempNode = new FileSystemNodeModel(_currentNode.Name + selectedExtension, false, _currentNode.FullPath + selectedExtension, _currentNode.SourceWadPath)
            {
                ChunkDiff = _currentNode.ChunkDiff,
                SourceChunkPathHash = _currentNode.SourceChunkPathHash
            };

            await ShowPreviewAsync(tempNode);
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

        private async void FilePreviewerControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (_isLoaded) return;

            try
            {
                ExtensionComboBox.ItemsSource = new[] { ".bin", ".dds", ".tex" };
                ExtensionComboBox.SelectedIndex = 0;

                ExplorerPreviewService.Initialize(
                    ImagePreview,
                    WebView2Preview,
                    TextEditorPreview,
                    PreviewPlaceholder,
                    SelectFileMessagePanel,
                    UnsupportedFileMessagePanel,
                    ExtensionlessFilePanel,
                    UnsupportedFileMessage,
                    DetailsPreview
                );

                await InitializeWebView2();
                await ExplorerPreviewService.ConfigureWebViewAfterInitializationAsync();
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

        private async Task InitializeWebView2()
        {
            await DirectoriesCreator.CreateDirTempPreviewAsync();
            try
            {
                var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: DirectoriesCreator.WebView2DataPath);
                await WebView2Preview.EnsureCoreWebView2Async(environment);

                WebView2Preview.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "preview.assets",
                    DirectoriesCreator.TempPreviewPath,
                    CoreWebView2HostResourceAccessKind.Allow
                );
            }
            catch (Exception ex)
            {
                LogService.LogError(ex, "WebView2 initialization failed. Previews will be affected.");
                CustomMessageBoxService.ShowError(
                    "Error",
                    "Could not initialize content viewer. Some previews may not work correctly.",
                    Window.GetWindow(this)
                );
            }
        }
    }
}