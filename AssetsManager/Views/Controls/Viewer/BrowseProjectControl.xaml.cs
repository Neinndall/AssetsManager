using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Core;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Utils;
using AssetsManager.Utils.Framework;
using AssetsManager.Views.Controls.Explorer;
using AssetsManager.Views.Helpers;
using AssetsManager.Views.Models.Viewer;
using Microsoft.Win32;

namespace AssetsManager.Views.Controls.Viewer
{
    public partial class BrowseProjectControl : UserControl
    {
        private readonly ObservableRangeCollection<ProjectBrowserFile> _files = new();
        private readonly SemaphoreSlim _previewGate = new(1);
        private CancellationTokenSource _browseCancellation;
        private CancellationTokenSource _previewCancellation;
        private CancellationTokenSource _treeCancellation;
        private CancellationTokenSource _thumbnailsCancellation;
        private ProjectBrowserFolder _rootFolder;
        private string _root;
        private string _folder;
        internal LogService LogService { get; set; }
        public event EventHandler CloseRequested;
        public event Action<string> OpenInStudioRequested;
        public static readonly DependencyProperty IsStandaloneProperty = DependencyProperty.Register(
            nameof(IsStandalone), typeof(bool), typeof(BrowseProjectControl), new PropertyMetadata(false));
        public bool IsStandalone
        {
            get => (bool)GetValue(IsStandaloneProperty);
            set => SetValue(IsStandaloneProperty, value);
        }
        public static readonly DependencyProperty IsThumbnailEnabledProperty = DependencyProperty.Register(
            nameof(IsThumbnailEnabled), typeof(bool), typeof(BrowseProjectControl),
            new PropertyMetadata(true, (sender, e) => ((BrowseProjectControl)sender).OnThumbnailModeChanged()));
        public bool IsThumbnailEnabled
        {
            get => (bool)GetValue(IsThumbnailEnabledProperty);
            set => SetValue(IsThumbnailEnabledProperty, value);
        }

        public BrowseProjectControl()
        {
            InitializeComponent();
            FilesList.ItemsSource = _files;
            SelectionBehavior.AddPrimaryActionHandler(FilesList, Files_PrimaryAction);
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            IsVisibleChanged += OnVisibilityChanged;
        }

        internal void SetProject(string path)
        {
            string root = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
            if (string.Equals(_root, root, StringComparison.OrdinalIgnoreCase)) return;
            Clear();
            _root = root;
            _folder = root;
            if (root != null)
            {
                _rootFolder = ProjectBrowserFolder.Create(root);
                _rootFolder.IsExpanded = true;
                FoldersTree.ItemsSource = new[] { _rootFolder };
            }
            SearchBox.Text = string.Empty;
            if (IsLoaded && IsVisible) _ = BrowseAsync();
        }

        private void OnLoaded(object sender, RoutedEventArgs e) => _ = BrowseAsync();
        private void OnUnloaded(object sender, RoutedEventArgs e) => CancelWork();
        private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (IsVisible) _ = BrowseAsync();
            else CancelWork();
        }

        private void CancelWork()
        {
            _browseCancellation?.Cancel();
            _previewCancellation?.Cancel();
            _treeCancellation?.Cancel();
            _treeCancellation?.Dispose();
            _treeCancellation = null;
            CancelThumbnails();
            PreviewImage.Source = null;
        }

        internal void Clear()
        {
            CancelWork();
            _root = _folder = null;
            _rootFolder = null;
            FoldersTree.ItemsSource = null;
            _files.Clear();
            Breadcrumbs.Clear();
            SelectionLabel.Text = string.Empty;
            SummaryText.Text = string.Empty;
            PreviewPanel.Visibility = PreviewSplitter.Visibility = Visibility.Collapsed;
            EmptyMessage.Text = "Open a project to browse its files.";
            EmptyMessage.Visibility = Visibility.Visible;
            PreviewMessage.Text = "Select a texture to preview it.";
            PreviewMessage.Visibility = Visibility.Visible;
        }

        private async Task BrowseAsync()
        {
            _browseCancellation?.Cancel();
            if (string.IsNullOrEmpty(_root) || !IsLoaded || !IsVisible) return;
            var cancellation = new CancellationTokenSource();
            _browseCancellation = cancellation;
            string root = _root, folder = _folder, query = SearchBox.Text;
            try
            {
                CancelPreview();
                CancelThumbnails();
                if (IsThumbnailEnabled) _thumbnailsCancellation = new CancellationTokenSource();
                _files.Clear();
                EmptyMessage.Text = "Reading project files...";
                EmptyMessage.Visibility = Visibility.Visible;
                var chain = new System.Collections.Generic.List<string> { root };
                string relative = Path.GetRelativePath(root, folder);
                if (relative != ".")
                    foreach (string part in relative.Split(Path.DirectorySeparatorChar))
                        chain.Add(Path.Combine(chain[^1], part));
                Breadcrumbs.SetPath(chain, path => Path.GetFileName(Path.TrimEndingDirectorySeparator(path)));
                ProjectBrowserResult result = await Task.Run(() =>
                    ProjectBrowserService.Read(root, folder, query, cancellation.Token), cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                _files.ReplaceRange(result.Files);
                EmptyMessage.Text = result.Files.Count == 0 ? "No matching files." : string.Empty;
                EmptyMessage.Visibility = result.Files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                SummaryText.Text = result.IsTruncated ? "First 1,000 matches · refine your search" : $"{result.Files.Count} items";
                await SynchronizeTreeAsync(folder, TreeToken());
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (cancellation.IsCancellationRequested) return;
                LogService?.LogError(ex, "Failed to browse Studio project files.");
                EmptyMessage.Text = "Unable to read this folder.";
                EmptyMessage.Visibility = Visibility.Visible;
            }
            finally
            {
                if (ReferenceEquals(_browseCancellation, cancellation)) _browseCancellation = null;
                cancellation.Dispose();
            }
        }

        private void CancelPreview()
        {
            _previewCancellation?.Cancel();
            PreviewImage.Source = null;
            PreviewPanel.Visibility = PreviewSplitter.Visibility = Visibility.Collapsed;
            PreviewMessage.Text = "Select a texture to preview it.";
            PreviewMessage.Visibility = Visibility.Visible;
        }

        private async void Files_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            CancelPreview();
            if (FilesList.SelectedItem is not ProjectBrowserFile item) return;
            SelectionLabel.Text = item.Name;
            if (item.IsDirectory || !SupportedFileTypes.IsImage(item.FullPath)) return;
            PreviewPanel.Visibility = PreviewSplitter.Visibility = Visibility.Visible;
            var cancellation = new CancellationTokenSource();
            _previewCancellation = cancellation;
            bool entered = false;
            try
            {
                PreviewMessage.Text = "Loading texture...";
                await _previewGate.WaitAsync(cancellation.Token);
                entered = true;
                BitmapSource image = await Task.Run(() =>
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    var bitmap = TextureUtils.LoadTextureFromFile(item.FullPath);
                    if (bitmap.CanFreeze) bitmap.Freeze();
                    return bitmap;
                }, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                PreviewImage.Source = image;
                PreviewMessage.Visibility = Visibility.Collapsed;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (cancellation.IsCancellationRequested) return;
                LogService?.LogError(ex, $"Failed to preview Studio texture: {item.FullPath}");
                PreviewMessage.Text = "Texture preview unavailable.";
            }
            finally
            {
                if (entered) _previewGate.Release();
                if (ReferenceEquals(_previewCancellation, cancellation)) _previewCancellation = null;
                cancellation.Dispose();
            }
        }

        private void Search_TextChanged(object sender, RoutedEventArgs e) => _ = BrowseAsync();
        private void Breadcrumbs_ItemClicked(object sender, BreadcrumbItemClickedEventArgs e)
        {
            if (e.Value is string path) Navigate(path);
        }
        private void Navigate(string path)
        {
            if (_root == null || !ProjectBrowserService.IsWithinRoot(_root, path)) return;
            if (string.Equals(_folder, path, StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(SearchBox.Text)) return;
            _folder = path;
            if (string.IsNullOrEmpty(SearchBox.Text)) _ = BrowseAsync();
            else SearchBox.Text = string.Empty;
        }
        private void Up_Click(object sender, RoutedEventArgs e)
        {
            if (_folder != null) Navigate(Directory.GetParent(_folder)?.FullName ?? _folder);
        }
        private void Files_PrimaryAction(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is ListBoxItem { DataContext: ProjectBrowserFile { IsDirectory: true } item }
                && FilesList.Items.Contains(item))
            {
                Navigate(item.FullPath);
                e.Handled = true;
            }
        }
        private void Reveal_Click(object sender, RoutedEventArgs e)
        {
            string path = (FilesList.SelectedItem as ProjectBrowserFile)?.FullPath ?? _folder;
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                start.ArgumentList.Add("/select," + path);
                Process.Start(start);
            }
            catch (Exception ex) { LogService?.LogError(ex, "Failed to reveal Studio project file."); }
        }
        private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

        private CancellationToken TreeToken()
        {
            _treeCancellation ??= new CancellationTokenSource();
            return _treeCancellation.Token;
        }

        private async Task LoadFolderAsync(ProjectBrowserFolder folder, CancellationToken token)
        {
            if (folder.IsLoaded) return;
            if (folder.LoadingTask != null && folder.LoadingToken.IsCancellationRequested)
                await folder.LoadingTask;
            token.ThrowIfCancellationRequested();
            if (folder.IsLoaded) return;
            if (folder.LoadingTask == null)
            {
                folder.LoadingToken = token;
                folder.LoadingTask = ReadFolderAsync(folder, token);
            }
            await folder.LoadingTask;
        }

        private async Task ReadFolderAsync(ProjectBrowserFolder folder, CancellationToken token)
        {
            try
            {
                string root = _root;
                var paths = await Task.Run(() => ProjectBrowserService.ReadFolders(root, folder.FullPath, token), token);
                token.ThrowIfCancellationRequested();
                folder.Children.ReplaceRange(paths.Select(ProjectBrowserFolder.Create));
                folder.IsLoaded = true;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { LogService?.LogError(ex, "Failed to read project folders."); }
            finally { folder.LoadingTask = null; }
        }

        private async Task SynchronizeTreeAsync(string path, CancellationToken token)
        {
            ProjectBrowserFolder current = _rootFolder;
            if (current == null) return;
            string relative = Path.GetRelativePath(current.FullPath, path);
            if (relative != ".")
            {
                foreach (string name in relative.Split(Path.DirectorySeparatorChar))
                {
                    await LoadFolderAsync(current, token);
                    token.ThrowIfCancellationRequested();
                    current.IsExpanded = true;
                    current = current.Children.FirstOrDefault(child => child.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (current == null) return;
                }
            }
            await LoadFolderAsync(current, token);
            token.ThrowIfCancellationRequested();
            ClearTreeSelection(_rootFolder);
            current.IsSelected = current.IsMultiSelected = true;
        }

        private static void ClearTreeSelection(ProjectBrowserFolder folder)
        {
            folder.IsSelected = folder.IsMultiSelected = false;
            foreach (ProjectBrowserFolder child in folder.Children) ClearTreeSelection(child);
        }

        private async void Folder_Expanded(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || !IsVisible || _root == null) return;
            if (e.OriginalSource is TreeViewItem { DataContext: ProjectBrowserFolder { FullPath: not null } folder })
                await LoadFolderAsync(folder, TreeToken());
        }

        private void Folders_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is ProjectBrowserFolder { FullPath: not null } folder) Navigate(folder.FullPath);
        }

        private void CancelThumbnails()
        {
            _thumbnailsCancellation?.Cancel();
            _thumbnailsCancellation?.Dispose();
            _thumbnailsCancellation = null;
            foreach (ProjectBrowserFile file in _files) file.Thumbnail = null;
        }

        private void OnThumbnailModeChanged()
        {
            if (FilesList == null) return;
            CancelThumbnails();
            if (IsThumbnailEnabled) _thumbnailsCancellation = new CancellationTokenSource();
        }

        private void Thumbnail_Loaded(object sender, RoutedEventArgs e) => RequestThumbnail(sender as FrameworkElement);
        private void Thumbnail_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is true) RequestThumbnail(sender as FrameworkElement);
        }

        private async void RequestThumbnail(FrameworkElement image)
        {
            if (!IsThumbnailEnabled || !IsVisible || image?.IsVisible != true || _thumbnailsCancellation == null ||
                image.DataContext is not ProjectBrowserFile { IsDirectory: false } file ||
                file.HasThumbnail || file.IsThumbnailLoading || !SupportedFileTypes.IsImage(file.FullPath)) return;
            CancellationToken token = _thumbnailsCancellation.Token;
            file.IsThumbnailLoading = true;
            bool entered = false;
            try
            {
                await _previewGate.WaitAsync(token);
                entered = true;
                BitmapSource bitmap = await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    var texture = TextureUtils.LoadTextureFromFile(file.FullPath, 96, 96);
                    if (texture?.CanFreeze == true) texture.Freeze();
                    return texture;
                }, token);
                token.ThrowIfCancellationRequested();
                file.Thumbnail = bitmap;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { LogService?.LogError(ex, $"Failed to load project thumbnail: {file.FullPath}"); }
            finally
            {
                if (entered) _previewGate.Release();
                file.IsThumbnailLoading = false;
                if (token.IsCancellationRequested && _thumbnailsCancellation != null) RequestThumbnail(image);
            }
        }

        private void ClosePreview_Click(object sender, RoutedEventArgs e) => CancelPreview();
        private void ChangeProject_Click(object sender, RoutedEventArgs e)
        {
            if (!IsStandalone) return;
            var dialog = new OpenFolderDialog { Title = "Browse extracted project files", InitialDirectory = _root ?? string.Empty };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true) SetProject(dialog.FolderName);
        }
        private void OpenStudio_Click(object sender, RoutedEventArgs e)
        {
            if (IsStandalone && _root != null) OpenInStudioRequested?.Invoke(_root);
        }
    }
}
