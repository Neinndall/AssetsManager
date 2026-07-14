using System;
using System.Xml;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Collections.Generic;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using AssetsManager.Services.Parsers;
using System.Reflection;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using ICSharpCode.AvalonEdit.Document;
using AssetsManager.Views.Models.Explorer;
using AssetsManager.Views.Models.Settings;
using AssetsManager.Utils;
using AssetsManager.Services.Core;
using AssetsManager.Services.Formatting;
using AssetsManager.Services.Comparator;

namespace AssetsManager.Services.Explorer
{
    public class ExplorerPreviewService
    {
        private enum Previewer { None, Image, WebView, AvalonEdit, StatusPanel }

        private readonly struct PreviewRequest
        {
            public PreviewRequest(long generation, CancellationToken cancellationToken)
            {
                Generation = generation;
                CancellationToken = cancellationToken;
            }

            public long Generation { get; }
            public CancellationToken CancellationToken { get; }
        }

        private Previewer _activeContentPreviewer = Previewer.None;
        private Previewer _activeImagePreviewer = Previewer.None;
        private readonly SemaphoreSlim _thumbnailLoadLimiter = new(4, 4);
        private FileSystemNodeModel _currentContentNode;
        private FileSystemNodeModel _currentImageNode;
        private Image _imagePreview;
        private Grid _webViewContainer;
        private TextEditor _textEditorPreview;
        private FilePreviewerModel _viewModel;
        private IHighlightingDefinition _jsonHighlightingDefinition;
        private CancellationTokenSource _previewCancellationTokenSource;
        private Task<CoreWebView2Environment> _webViewEnvironmentTask;
        private long _previewGeneration;
        private readonly string _mediaTempOwnerId = Guid.NewGuid().ToString("N");
        private string _activeMediaTempFilePath;

        private readonly LogService _logService;
        private readonly DirectoriesCreator _directoriesCreator;
        private readonly ContentFormatterService _contentFormatterService;
        private readonly AudioConversionService _audioConversionService;
        private readonly WadContentProvider _wadContentProvider;
        private readonly SvgParser _svgParser;
        private readonly NarrativeMetadataService _narrativeMetadataService;
        private readonly DiffViewService _diffViewService;
        private readonly AssetMemoryCacheService _assetMemoryCacheService;

        private bool _isGridActive;

        public ExplorerPreviewService(
            LogService logService, 
            DirectoriesCreator directoriesCreator, 
            ContentFormatterService contentFormatterService, 
            AudioConversionService audioConversionService, 
            WadContentProvider wadContentProvider,
            SvgParser svgParser,
            NarrativeMetadataService narrativeMetadataService,
            DiffViewService diffViewService,
            AssetMemoryCacheService assetMemoryCacheService)
        {
            _logService = logService;
            _directoriesCreator = directoriesCreator;
            _contentFormatterService = contentFormatterService;
            _audioConversionService = audioConversionService;
            _wadContentProvider = wadContentProvider;
            _svgParser = svgParser;
            _narrativeMetadataService = narrativeMetadataService;
            _diffViewService = diffViewService;
            _assetMemoryCacheService = assetMemoryCacheService;
        }

        public void Initialize(Image imagePreview, Grid webViewContainer, TextEditor textEditor, FilePreviewerModel viewModel)
        {
            _imagePreview = imagePreview;
            _webViewContainer = webViewContainer;
            _textEditorPreview = textEditor;
            _viewModel = viewModel;
        }

        public async Task ShowPreviewAsync(FileSystemNodeModel node)
        {
            // If the node is a directory or container, we check if we should keep the last preview
            if (node == null || node.Type == NodeType.RealDirectory || node.Type == NodeType.VirtualDirectory || node.Type == NodeType.WadFile || node.Type == NodeType.SoundBank || node.Type == NodeType.AudioEvent || SupportedFileTypes.AudioBank.Contains(node.Extension))
            {
                // If we've already started browsing files, we DON'T reset. 
                if (_viewModel.HasEverPreviewedAFile)
                {
                    _isGridActive = true;
                    return;
                }

                await ResetPreviewAsync();
                return;
            }

            Previewer requiredPreviewer = GetRequiredPreviewer(node);
            bool isImage = requiredPreviewer == Previewer.Image;

            // Per-Slot Early Exit:
            // Check if the node is already loaded in its corresponding slot with the correct previewer.
            // This prevents reloads when alternating focus in Dual View, while correctly restoring tabs.
            if (isImage)
            {
                if (_currentImageNode == node && _viewModel.IsImageVisible && _activeImagePreviewer == requiredPreviewer) return;
            }
            else
            {
                if (_currentContentNode == node && _activeContentPreviewer == requiredPreviewer)
                {
                    if (requiredPreviewer == Previewer.StatusPanel)
                    {
                        _viewModel.IsUnsupportedVisible = true;
                        _viewModel.IsContentVisible = true;
                    }
                    else if (requiredPreviewer == Previewer.AvalonEdit)
                    {
                        _viewModel.IsTextVisible = true;
                        _viewModel.IsContentVisible = true;
                    }
                    else if (requiredPreviewer == Previewer.WebView)
                    {
                        _viewModel.IsWebVisible = true;
                        _viewModel.IsContentVisible = true;
                    }
                    return;
                }
            }

            // Step 1: Prepare the correct slot (Image or Content)
            PrepareSlotForFile(node);

            // Step 1b: If transitioning from grid/folder view, hide old content immediately.
            // The grid was showing and the preview content is stale — reset visibility
            // so the new file appears cleanly without flashing old content.
            // For file→file transitions, old content stays as a placeholder during I/O.
            if (_isGridActive)
            {
                _isGridActive = false;
                if (!isImage)
                {
                    _viewModel.IsContentVisible = true;
                    _viewModel.IsTextVisible = false;
                    _viewModel.IsWebVisible = false;
                }
            }

            var previewRequest = BeginPreviewRequest();

            // Step 2: Discovery of technical metadata (e.g., Summoner Icons, Emotes)
            // We only update/clear metadata if the current node is an image. 
            // If it's a text file, we keep the metadata of the image shown in the other slot (Dual View).
            var metadata = await _narrativeMetadataService.GetMetadataAsync(node);
            ThrowIfPreviewIsObsolete(previewRequest);
            if (isImage || metadata != null)
            {
                _viewModel.NarrativeMetadata = metadata;
            }

            // Step 3: SELECTIVE clearing to maintain Dual View
            if (isImage)
            {
                _imagePreview.Source = null;
                _currentImageNode = node;
            }
            else
            {
                // Keep old content visible until new data is ready in SetPreviewerAsync.
                // This prevents both the blank ContentPanel flash (Dual View collapsing)
                // and the empty TextEditor flash during async I/O + parsing.
                _currentContentNode = node;
            }

            try
            {
                byte[] data = null;
                if (node.Type == NodeType.VirtualFile) { data = await _wadContentProvider.GetVirtualFileBytesAsync(node, previewRequest.CancellationToken); }
                else if (node.Type == NodeType.RealFile) { if (File.Exists(node.VirtualPath)) data = await File.ReadAllBytesAsync(node.VirtualPath, previewRequest.CancellationToken); }
                else if (node.Type == NodeType.WemFile) { data = await _wadContentProvider.GetWemFileBytesAsync(node, previewRequest.CancellationToken); }

                ThrowIfPreviewIsObsolete(previewRequest);

                if (data != null) { await DispatchPreview(data, node.Extension, node, previewRequest); }
                else { await ShowUnsupportedPreviewAsync(node.Extension, previewRequest); }
            }
            catch (OperationCanceledException)
            {
                // Handled gracefully: Task was cancelled due to quick navigation
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Failed to preview file '{node.VirtualPath}'.");
                if (IsCurrentPreview(previewRequest))
                {
                    await ShowUnsupportedPreviewAsync(node.Extension, previewRequest);
                }
            }
        }

        public async Task ResetPreviewAsync()
        {
            CancelCurrentPreview();
            _currentContentNode = null;
            _currentImageNode = null;

            // Step 1: Clean UI controls to release RAM
            if (_textEditorPreview != null)
            {
                // Assigning a new document is the most efficient way to release old large buffers
                _textEditorPreview.Document = new TextDocument();
            }
            _imagePreview.Source = null;
            _viewModel.NarrativeMetadata = null;

            // Step 2: Restore the UI state
            await SetPreviewerAsync(Previewer.StatusPanel);
        }

        private PreviewRequest BeginPreviewRequest()
        {
            _previewCancellationTokenSource?.Cancel();
            _previewCancellationTokenSource?.Dispose();

            _previewCancellationTokenSource = new CancellationTokenSource();
            return new PreviewRequest(Interlocked.Increment(ref _previewGeneration), _previewCancellationTokenSource.Token);
        }

        private void CancelCurrentPreview()
        {
            Interlocked.Increment(ref _previewGeneration);
            _previewCancellationTokenSource?.Cancel();
            _previewCancellationTokenSource?.Dispose();
            _previewCancellationTokenSource = null;
        }

        private bool IsCurrentPreview(PreviewRequest previewRequest)
        {
            return !previewRequest.CancellationToken.IsCancellationRequested &&
                   previewRequest.Generation == Interlocked.Read(ref _previewGeneration);
        }

        private void ThrowIfPreviewIsObsolete(PreviewRequest previewRequest)
        {
            previewRequest.CancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentPreview(previewRequest))
            {
                throw new OperationCanceledException();
            }
        }

        public void PrepareSlotForFile(FileSystemNodeModel node)
        {
            if (node == null) return;

            _viewModel.IsWelcomeVisible = false;
            _viewModel.HasEverPreviewedAFile = true;

            bool isImage = node.Extension != null &&
                (SupportedFileTypes.Images.Contains(node.Extension) ||
                 SupportedFileTypes.Textures.Contains(node.Extension) ||
                 SupportedFileTypes.VectorImages.Contains(node.Extension));

            if (isImage)
            {
                _viewModel.IsImageUnsupportedVisible = false;
            }
            else
            {
                _viewModel.IsUnsupportedVisible = false;
                _viewModel.IsContentVisible = true;
            }
        }

        public void CloseSlotByCategory(FileSystemNodeModel node)
        {
            if (node == null) return;

            bool isImage = node.Extension != null &&
                (SupportedFileTypes.Images.Contains(node.Extension) ||
                 SupportedFileTypes.Textures.Contains(node.Extension) ||
                 SupportedFileTypes.VectorImages.Contains(node.Extension));

            bool hasMoreOfSameCategory = _viewModel.PinnedFilesManager.PinnedFiles.Any(p =>
                p.Node != node &&
                (SupportedFileTypes.Images.Contains(p.Node.Extension) ||
                 SupportedFileTypes.Textures.Contains(p.Node.Extension) ||
                 SupportedFileTypes.VectorImages.Contains(p.Node.Extension)) == isImage);

            _viewModel.UnloadSlotByCategory(isImage, hasMoreOfSameCategory);
        }

        private async Task DispatchPreview(byte[] data, string extension, FileSystemNodeModel node, PreviewRequest previewRequest)
        {
            ThrowIfPreviewIsObsolete(previewRequest);
            // Aseguramos la creacion de la carpeta necesaria
            _directoriesCreator.CreateDirectory(_directoriesCreator.TempPreviewPath);

            if (extension.Equals(".tga", StringComparison.OrdinalIgnoreCase) || SupportedFileTypes.Textures.Contains(extension)) { await ShowTexturePreviewAsync(data, extension, previewRequest); }
            else if (SupportedFileTypes.Images.Contains(extension)) { await ShowImagePreviewAsync(data, extension, previewRequest); }
            else if (SupportedFileTypes.VectorImages.Contains(extension)) { await ShowSvgPreviewAsync(data, extension, previewRequest); }
            else if (SupportedFileTypes.Media.Contains(extension))
            {
                if (extension == ".wem")
                {
                    byte[] oggData = await _audioConversionService.ConvertAudioToFormatAsync(data, ".wem", AudioExportFormat.Ogg);
                    ThrowIfPreviewIsObsolete(previewRequest);
                    if (oggData != null)
                    {
                        await ShowAudioVideoPreviewAsync(oggData, ".ogg", node.Name, previewRequest);
                    }
                    else
                    {
                        await ShowUnsupportedPreviewAsync(".wem", previewRequest);
                    }
                }
                else
                {
                    await ShowAudioVideoPreviewAsync(data, extension, node.Name, previewRequest);
                }
            }
            else if (SupportedFileTypes.IsText(extension)) { await ShowAvalonEditTextPreviewAsync(data, extension, previewRequest); }
            else { await ShowUnsupportedPreviewAsync(extension, previewRequest); }
        }

        private async Task ShowAvalonEditTextPreviewAsync(byte[] data, string extension, PreviewRequest previewRequest)
        {
            try
            {
                string dataType = extension.TrimStart('.');
                string textContent = await _contentFormatterService.GetFormattedStringAsync(dataType, data);
                IHighlightingDefinition syntaxHighlighting = null;

                if (SupportedFileTypes.UsesJsonHighlighting(extension))
                {
                    // Los CSS y JS de League se visualizan con nuestro coloreado personalizado
                    syntaxHighlighting = GetJsonHighlighting();
                }

                await SetPreviewerAsync(Previewer.AvalonEdit, (textContent, syntaxHighlighting), false, previewRequest);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Failed to show text preview for extension {extension}");
                string errorText = $"Error showing {extension} file.";
                if (IsCurrentPreview(previewRequest))
                {
                    await SetPreviewerAsync(Previewer.AvalonEdit, (errorText, (IHighlightingDefinition)null), false, previewRequest);
                }
            }
        }

        private IHighlightingDefinition GetJsonHighlighting()
        {
            if (_jsonHighlightingDefinition == null)
            {
                var assembly = Assembly.GetExecutingAssembly();
                var resourceName = "AssetsManager.Resources.JsonSyntaxHighlighting.xshd";
                using (var stream = assembly.GetManifestResourceStream(resourceName))
                using (var reader = new XmlTextReader(stream))
                {
                    _jsonHighlightingDefinition = HighlightingLoader.Load(reader, HighlightingManager.Instance);
                }
            }
            return _jsonHighlightingDefinition;
        }

        private Task SetPreviewerAsync(Previewer newPreviewer)
        {
            return SetPreviewerAsync(newPreviewer, null, false, null);
        }

        private async Task SetPreviewerAsync(Previewer newPreviewer, object content, bool shouldAutoplay, PreviewRequest? previewRequest)
        {
            if (previewRequest.HasValue)
            {
                ThrowIfPreviewIsObsolete(previewRequest.Value);
            }
            // Dispose of WebView only when we are explicitly replacing it in the left Content slot
            if (newPreviewer == Previewer.AvalonEdit && _webViewContainer != null)
            {
                ReleaseActiveMediaPreview();
            }

            switch (newPreviewer)
            {
                case Previewer.Image:
                    if (content is ImageSource imageSource)
                    {
                        _imagePreview.Source = imageSource;
                        _viewModel.IsImageVisible = true;
                        _activeImagePreviewer = Previewer.Image;

                        if (_viewModel.IsUnsupportedVisible && !_viewModel.IsContentVisible)
                        {
                            _viewModel.IsUnsupportedVisible = false;
                            _viewModel.IsContentVisible = false;
                            _activeContentPreviewer = Previewer.None;
                        }
                    }
                    break;

                case Previewer.WebView:
                    if (content is string htmlContent && previewRequest.HasValue)
                    {
                        ReleaseActiveMediaPreview();

                        bool webViewCreated = await CreateAndShowWebViewAsync(htmlContent, shouldAutoplay, previewRequest.Value);
                        if (!webViewCreated)
                        {
                            return;
                        }

                        ThrowIfPreviewIsObsolete(previewRequest.Value);
                        _viewModel.IsContentVisible = true;
                        _viewModel.IsTextVisible = false;
                        _viewModel.IsWebVisible = true;
                        _activeContentPreviewer = Previewer.WebView;
                    }
                    break;

                case Previewer.AvalonEdit:
                    if (content is ValueTuple<string, IHighlightingDefinition> textData)
                    {
                        _textEditorPreview.Text = textData.Item1;
                        _textEditorPreview.SyntaxHighlighting = textData.Item2;
                        _viewModel.IsContentVisible = true;
                        _viewModel.IsWebVisible = false;
                        _viewModel.IsTextVisible = true;
                        _textEditorPreview.Focus();
                        _activeContentPreviewer = Previewer.AvalonEdit;
                    }
                    break;

                case Previewer.StatusPanel:
                    if (content is string extension)
                    {
                        bool isImageExt = extension.Contains("tex") || extension.Contains("dds") || extension.Contains("svg") ||
                                          SupportedFileTypes.Images.Contains(extension) ||
                                          SupportedFileTypes.Textures.Contains(extension) ||
                                          SupportedFileTypes.VectorImages.Contains(extension);

                        // Check if there is currently a file (valid) in the left panel
                        // (which means Dual View should be maintained and we show the image error on the right)
                        bool isLeftPanelOccupied = _viewModel.IsContentVisible && _activeContentPreviewer != Previewer.StatusPanel;

                        if (isImageExt && isLeftPanelOccupied)
                        {
                            // Dual View Scenario: Keep left panel active, show error on the right
                            _viewModel.IsImageVisible = true;
                            _viewModel.IsImageUnsupportedVisible = true;
                            _viewModel.SetUnsupportedStatus(extension, true);
                            _activeImagePreviewer = Previewer.StatusPanel;
                        }
                        else
                        {
                            // Full Screen or Left-only Scenario: Show error on the left
                            // Dispose of left WebView as it is being replaced by the StatusPanel error
                            if (_webViewContainer != null)
                            {
                                ReleaseActiveMediaPreview();
                            }

                            _viewModel.IsUnsupportedVisible = true;
                            _viewModel.IsContentVisible = true;
                            _viewModel.IsTextVisible = false;
                            _viewModel.IsWebVisible = false;
                            _viewModel.SetUnsupportedStatus(extension, false);
                            _activeContentPreviewer = Previewer.StatusPanel;

                            if (isImageExt)
                            {
                                _viewModel.IsImageVisible = false;
                                _viewModel.IsImageUnsupportedVisible = false;
                                _activeImagePreviewer = Previewer.None;
                            }
                        }
                    }
                    else
                    {
                        // Global Reset
                        // Dispose of WebView as we are doing a full clean
                        if (_webViewContainer != null)
                        {
                            ReleaseActiveMediaPreview();
                        }

                        _viewModel.ResetAllVisibility();
                        _imagePreview.Source = null;
                        _activeContentPreviewer = Previewer.None;
                        _activeImagePreviewer = Previewer.None;
                    }
                    break;
            }
        }

        private async Task<bool> CreateAndShowWebViewAsync(string htmlContent, bool shouldAutoplay, PreviewRequest previewRequest)
        {
            WebView2 webView = null;
            try
            {
                ThrowIfPreviewIsObsolete(previewRequest);

                webView = new WebView2()
                {
                    DefaultBackgroundColor = System.Drawing.Color.Transparent
                };

                _webViewContainer.Children.Add(webView);

                // Initialize CoreWebView2
                var environment = await GetWebViewEnvironmentAsync();
                ThrowIfPreviewIsObsolete(previewRequest);
                await webView.EnsureCoreWebView2Async(environment);
                ThrowIfPreviewIsObsolete(previewRequest);

                // Set settings and mappings
                webView.CoreWebView2.SetVirtualHostNameToFolderMapping("preview.assets", _directoriesCreator.TempPreviewPath, CoreWebView2HostResourceAccessKind.Allow);
                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                webView.CoreWebView2.Settings.IsSwipeNavigationEnabled = false;

                // Navigate and handle autoplay
                void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs args)
                {
                    webView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
                    if (shouldAutoplay && IsCurrentPreview(previewRequest))
                    {
                        _ = webView.Dispatcher.InvokeAsync(async () =>
                        {
                            try
                            {
                                await webView.CoreWebView2.ExecuteScriptAsync("playMedia();");
                            }
                            catch (Exception ex)
                            {
                                _logService.LogError(ex, "Failed to autoplay media");
                            }
                        });
                    }
                }

                webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
                webView.CoreWebView2.NavigateToString(htmlContent);
                return true;
            }
            catch (OperationCanceledException)
            {
                DisposeWebView(webView);
                return false;
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to create, initialize, and show WebView2.");
                DisposeWebView(webView);
                if (IsCurrentPreview(previewRequest))
                {
                    await ShowUnsupportedPreviewAsync(".media", previewRequest);
                }
                return false;
            }
        }

        private async Task<CoreWebView2Environment> GetWebViewEnvironmentAsync()
        {
            _webViewEnvironmentTask ??= CoreWebView2Environment.CreateAsync(
                userDataFolder: _directoriesCreator.WebView2DataPath);

            try
            {
                return await _webViewEnvironmentTask;
            }
            catch
            {
                _webViewEnvironmentTask = null;
                throw;
            }
        }

        private void DisposeWebView(WebView2 webView)
        {
            if (webView == null)
            {
                return;
            }

            if (_webViewContainer?.Children.Contains(webView) == true)
            {
                _webViewContainer.Children.Remove(webView);
            }

            webView.Dispose();
        }

        private void ReleaseActiveMediaPreview()
        {
            var webView = _webViewContainer?.Children.OfType<WebView2>().FirstOrDefault();
            if (webView != null)
            {
                DisposeWebView(webView);
            }

            if (_activeContentPreviewer == Previewer.WebView)
            {
                _activeContentPreviewer = Previewer.None;
            }

            DeleteMediaTempFile(_activeMediaTempFilePath);
            _activeMediaTempFilePath = null;
        }

        private void DeleteMediaTempFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return;
            }

            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Failed to remove media preview temp file '{filePath}'.");
            }
        }

        private async Task ShowImagePreviewAsync(byte[] data, string extension, PreviewRequest previewRequest)
        {
            string imageCacheKey = _assetMemoryCacheService.CreateImageKey(data, extension, 0, 0);
            if (_assetMemoryCacheService.TryGetImage(imageCacheKey, out ImageSource cachedImage))
            {
                await SetPreviewerAsync(Previewer.Image, cachedImage, false, previewRequest);
                return;
            }

            var bitmap = await Task.Run(() =>
            {
                previewRequest.CancellationToken.ThrowIfCancellationRequested();
                using var stream = new MemoryStream(data);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = stream;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }, previewRequest.CancellationToken);

            _assetMemoryCacheService.SetImage(imageCacheKey, bitmap);
            await SetPreviewerAsync(Previewer.Image, bitmap, false, previewRequest);
        }

        private async Task ShowTexturePreviewAsync(byte[] data, string extension, PreviewRequest previewRequest)
        {
            string imageCacheKey = _assetMemoryCacheService.CreateImageKey(data, extension, 0, 0);
            if (_assetMemoryCacheService.TryGetImage(imageCacheKey, out ImageSource cachedImage))
            {
                await SetPreviewerAsync(Previewer.Image, cachedImage, false, previewRequest);
                return;
            }

            var bitmapSource = await Task.Run(() =>
            {
                previewRequest.CancellationToken.ThrowIfCancellationRequested();
                using var stream = new MemoryStream(data);
                return TextureUtils.LoadTexture(stream, extension);
            }, previewRequest.CancellationToken);

            if (bitmapSource != null)
            {
                _assetMemoryCacheService.SetImage(imageCacheKey, bitmapSource);
                await SetPreviewerAsync(Previewer.Image, bitmapSource, false, previewRequest);
            }
            else
            {
                await ShowUnsupportedPreviewAsync(extension, previewRequest);
            }
        }

        private async Task ShowSvgPreviewAsync(byte[] data, string extension, PreviewRequest previewRequest)
        {
            try
            {
                string imageCacheKey = _assetMemoryCacheService.CreateImageKey(data, extension, 0, 0);
                if (_assetMemoryCacheService.TryGetImage(imageCacheKey, out ImageSource cachedImage))
                {
                    await SetPreviewerAsync(Previewer.Image, cachedImage, false, previewRequest);
                    return;
                }

                var drawingImage = await Task.Run(() =>
                {
                    previewRequest.CancellationToken.ThrowIfCancellationRequested();
                    return _svgParser.LoadSvg(data);
                }, previewRequest.CancellationToken);
                if (drawingImage != null)
                {
                    _assetMemoryCacheService.SetImage(imageCacheKey, drawingImage);
                    await SetPreviewerAsync(Previewer.Image, drawingImage, false, previewRequest);
                }
                else
                {
                    await ShowUnsupportedPreviewAsync(".svg", previewRequest);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to show SVG preview.");
                if (IsCurrentPreview(previewRequest))
                {
                    await ShowUnsupportedPreviewAsync(".svg", previewRequest);
                }
            }
        }

        private async Task ShowAudioVideoPreviewAsync(byte[] data, string extension, string displayName, PreviewRequest previewRequest)
        {
            if (_webViewContainer == null)
            {
                await ShowUnsupportedPreviewAsync(extension, previewRequest);
                return;
            }

            string tempFilePath = null;
            try
            {
                ThrowIfPreviewIsObsolete(previewRequest);

                var tempFileName = $"preview_{_mediaTempOwnerId}_{previewRequest.Generation}{extension}";
                tempFilePath = Path.Combine(_directoriesCreator.TempPreviewPath, tempFileName);
                await File.WriteAllBytesAsync(tempFilePath, data);
                ThrowIfPreviewIsObsolete(previewRequest);

                var mimeType = extension switch
                {
                    ".ogg" => "audio/ogg",
                    ".webm" => "video/webm",
                    _ => "application/octet-stream"
                };

                string tag = mimeType.StartsWith("video/") ? "video" : "audio";
                string extraAttributes = tag == "video" ? "muted" : "";
                var fileUrl = $"https://preview.assets/{tempFileName}";

                string htmlContent;

                if (tag == "audio")
                {
                    var assembly = Assembly.GetExecutingAssembly();
                    var resourceName = "AssetsManager.Resources.AudioPlayer.html";
                    using (var stream = assembly.GetManifestResourceStream(resourceName))
                    using (var reader = new StreamReader(stream))
                    {
                        htmlContent = await reader.ReadToEndAsync();
                    }
                    ThrowIfPreviewIsObsolete(previewRequest);

                    htmlContent = htmlContent.Replace("{{DISPLAY_NAME}}", displayName)
                                             .Replace("{{FILE_EXTENSION}}", extension.ToUpper().TrimStart('.'))
                                             .Replace("{{FILE_URL}}", fileUrl);
                }
                else
                {
                    // MODERN VIDEO PLAYER
                    htmlContent = $@"
                    <!DOCTYPE html>
                    <html>
                    <head>
                        <meta charset='UTF-8'>
                        <style>
                            html, body {{
                                background-color: transparent !important;
                                margin: 0; padding: 0; height: 100vh;
                                display: flex; justify-content: center; align-items: center; overflow: hidden;
                            }}
                            video {{
                                max-width: 90%; max-height: 90%;
                                border-radius: 12px; 
                                box-shadow: 0 4px 12px rgba(0,0,0,0.20); /* Adjusted for subtlety */
                                background-color: #000;
                                opacity: 0;
                                transition: opacity 0.3s ease-out;
                            }}
                            video.loaded {{
                                opacity: 1;
                            }}
                        </style>
                    </head>
                    <body>
                        <video id='mediaElement' controls preload='auto' {extraAttributes}>
                            <source src='{fileUrl}' type='{mimeType}'>
                        </video>
                        <script>
                            const mediaElement = document.getElementById('mediaElement');
                            window.playMedia = () => {{
                                mediaElement.play().catch(e => console.log('Play error:', e));
                            }};
                            mediaElement.addEventListener('loadeddata', () => mediaElement.classList.add('loaded'));
                            setTimeout(() => mediaElement.classList.add('loaded'), 1000); // Fallback
                        </script>
                    </body>
                    </html>";
                }

                await SetPreviewerAsync(Previewer.WebView, htmlContent, true, previewRequest);
                ThrowIfPreviewIsObsolete(previewRequest);
                if (_activeContentPreviewer == Previewer.WebView)
                {
                    _activeMediaTempFilePath = tempFilePath;
                    tempFilePath = null;
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Failed to create and show preview for {extension} file.");
                if (IsCurrentPreview(previewRequest))
                {
                    await ShowUnsupportedPreviewAsync(extension, previewRequest);
                }
            }
            finally
            {
                DeleteMediaTempFile(tempFilePath);
            }
        }

        private async Task ShowUnsupportedPreviewAsync(string extension, PreviewRequest previewRequest)
        {
            await SetPreviewerAsync(Previewer.StatusPanel, extension, false, previewRequest);
        }

        private Previewer GetRequiredPreviewer(FileSystemNodeModel node)
        {
            if (node == null) return Previewer.None;
            string extension = node.Extension.ToLowerInvariant();
            
            if (SupportedFileTypes.IsImage(extension))
            {
                return Previewer.Image;
            }
            
            if (SupportedFileTypes.Media.Contains(extension))
            {
                return Previewer.WebView;
            }
            
            if (SupportedFileTypes.IsText(extension))
            {
                return Previewer.AvalonEdit;
            }
            
            return Previewer.StatusPanel;
        }

        public async Task<ImageSource> GetImagePreviewAsync(FileSystemNodeModel node, int maxWidth = 0, CancellationToken cancellationToken = default)
        {
            if (node == null || !SupportedFileTypes.IsImage(node.Extension))
            {
                return null;
            }

            var acquiredSlot = false;
            try
            {
                await _thumbnailLoadLimiter.WaitAsync(cancellationToken);
                acquiredSlot = true;
                byte[] data = node.Type switch
                {
                    NodeType.VirtualFile => await _wadContentProvider.GetVirtualFileBytesAsync(node, cancellationToken),
                    NodeType.RealFile => await File.ReadAllBytesAsync(node.VirtualPath, cancellationToken),
                    _ => null
                };

                if (data == null) return null;

                int? size = maxWidth > 0 ? maxWidth : null;
                string imageCacheKey = _assetMemoryCacheService.CreateImageKey(data, node.Extension, maxWidth, maxWidth);
                if (_assetMemoryCacheService.TryGetImage(imageCacheKey, out ImageSource cachedImage))
                {
                    return cachedImage;
                }

                ImageSource image = null;

                if (SupportedFileTypes.Images.Contains(node.Extension))
                {
                    image = await Task.Run(() =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        using var stream = new MemoryStream(data);
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.StreamSource = stream;
                        if (size.HasValue) bmp.DecodePixelWidth = size.Value;
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.EndInit();
                        bmp.Freeze();
                        return bmp;
                    }, cancellationToken);
                }
                else if (SupportedFileTypes.Textures.Contains(node.Extension))
                {
                    image = await Task.Run(() =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        using var stream = new MemoryStream(data);
                        return TextureUtils.LoadTexture(stream, node.Extension, size, size);
                    }, cancellationToken);
                }
                else if (SupportedFileTypes.VectorImages.Contains(node.Extension))
                {
                    image = await Task.Run(() =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return _svgParser.LoadSvg(data);
                    }, cancellationToken);
                }

                _assetMemoryCacheService.SetImage(imageCacheKey, image);
                return image;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Failed to get image preview for '{node.VirtualPath}'.");
                return null;
            }
            finally
            {
                if (acquiredSlot)
                {
                    _thumbnailLoadLimiter.Release();
                }
            }

        }

        public async Task ShowFileDiffAsync(string oldPath, string newPath, Window owner)
        {
            await _diffViewService.ShowFileDiffAsync(oldPath, newPath, owner);
        }
    }
}
