using System;
using System.Xml;
using System.IO;
using System.Linq;
using System.Windows;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Media;
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

        private readonly struct MediaPreviewContent
        {
            public MediaPreviewContent(byte[] data, string extension, string displayName)
            {
                Data = data;
                Extension = extension;
                DisplayName = displayName;
            }

            public byte[] Data { get; }
            public string Extension { get; }
            public string DisplayName { get; }
        }

        private Previewer _activeContentPreviewer = Previewer.None;
        private Previewer _activeImagePreviewer = Previewer.None;
        private readonly SemaphoreSlim _thumbnailLoadLimiter = new(4, 4);
        private FileSystemNodeModel _currentContentNode;
        private FileSystemNodeModel _currentImageNode;
        private Image _imagePreview;
        private TextEditor _textEditorPreview;
        private FilePreviewerModel _viewModel;
        private IHighlightingDefinition _jsonHighlightingDefinition;
        private CancellationTokenSource _previewCancellationTokenSource;
        private long _previewGeneration;

        private readonly LogService _logService;
        private readonly ContentFormatterService _contentFormatterService;
        private readonly AudioConversionService _audioConversionService;
        private readonly WadContentProvider _wadContentProvider;
        private readonly SvgParser _svgParser;
        private readonly NarrativeMetadataService _narrativeMetadataService;
        private readonly DiffViewService _diffViewService;
        private readonly MediaWebViewPreviewService _mediaWebViewPreviewService;

        private bool _isGridActive;

        public ExplorerPreviewService(
            LogService logService, 
            ContentFormatterService contentFormatterService, 
            AudioConversionService audioConversionService, 
            WadContentProvider wadContentProvider,
            SvgParser svgParser,
            NarrativeMetadataService narrativeMetadataService,
            DiffViewService diffViewService,
            MediaWebViewPreviewService mediaWebViewPreviewService)
        {
            _logService = logService;
            _contentFormatterService = contentFormatterService;
            _audioConversionService = audioConversionService;
            _wadContentProvider = wadContentProvider;
            _svgParser = svgParser;
            _narrativeMetadataService = narrativeMetadataService;
            _diffViewService = diffViewService;
            _mediaWebViewPreviewService = mediaWebViewPreviewService;
        }

        public void Initialize(Image imagePreview, Grid webViewContainer, TextEditor textEditor, FilePreviewerModel viewModel)
        {
            _imagePreview = imagePreview;
            _textEditorPreview = textEditor;
            _viewModel = viewModel;
            _mediaWebViewPreviewService.Initialize(webViewContainer);
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
            bool isImage = SupportedFileTypes.IsImage(node.Extension);

            // Per-Slot Early Exit:
            // Check if the node is already loaded in its corresponding slot with the correct previewer.
            // This prevents reloads when alternating focus in Dual View, while correctly restoring tabs.
            if (isImage)
            {
                if (_currentImageNode == node && _viewModel.ImagePreviewState == PreviewState.Image && _activeImagePreviewer == requiredPreviewer) return;
            }
            else
            {
                if (_currentContentNode == node && _activeContentPreviewer == requiredPreviewer)
                {
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
                    _viewModel.ShowContentLoading();
                }
            }

            var previewRequest = BeginPreviewRequest();

            try
            {
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
                    await ShowPreviewErrorAsync(node.Extension, previewRequest);
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

            bool isImage = SupportedFileTypes.IsImage(node.Extension);

            if (isImage)
            {
                _viewModel.BeginImageLoading();
            }
            else
            {
                _viewModel.BeginContentLoading(true);
            }
        }

        public void CloseSlotByCategory(FileSystemNodeModel node)
        {
            if (node == null) return;

            bool isImage = SupportedFileTypes.IsImage(node.Extension);

            bool hasMoreOfSameCategory = _viewModel.PinnedFilesManager.PinnedFiles.Any(p =>
                p.Node != node &&
                SupportedFileTypes.IsImage(p.Node.Extension) == isImage);

            _viewModel.UnloadSlotByCategory(isImage, hasMoreOfSameCategory);
        }

        private async Task DispatchPreview(byte[] data, string extension, FileSystemNodeModel node, PreviewRequest previewRequest)
        {
            ThrowIfPreviewIsObsolete(previewRequest);

            if (extension.Equals(".tex", StringComparison.OrdinalIgnoreCase) && FileTypeDetector.IsEncryptedRiotTex(data))
            {
                ShowEncryptedRiotTexturePreview(previewRequest);
                return;
            }

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
                        await SetPreviewerAsync(
                            Previewer.WebView,
                            new MediaPreviewContent(oggData, ".ogg", node.Name),
                            true,
                            previewRequest);
                    }
                    else
                    {
                        await ShowUnsupportedPreviewAsync(".wem", previewRequest);
                    }
                }
                else
                {
                    await SetPreviewerAsync(
                        Previewer.WebView,
                        new MediaPreviewContent(data, extension, node.Name),
                        true,
                        previewRequest);
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
                if (IsCurrentPreview(previewRequest))
                {
                    await ShowPreviewErrorAsync(extension, previewRequest);
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
            if (newPreviewer == Previewer.AvalonEdit)
            {
                _mediaWebViewPreviewService.Deactivate();
            }

            switch (newPreviewer)
            {
                case Previewer.Image:
                    if (content is ImageSource imageSource)
                    {
                        _imagePreview.Source = imageSource;
                        _viewModel.ShowImagePreview();
                        _activeImagePreviewer = Previewer.Image;
                    }
                    break;

                case Previewer.WebView:
                    if (content is MediaPreviewContent mediaContent && previewRequest.HasValue)
                    {
                        if (_activeContentPreviewer == Previewer.WebView)
                        {
                            _activeContentPreviewer = Previewer.None;
                        }

                        bool webViewReady = await _mediaWebViewPreviewService.ShowAsync(
                            mediaContent.Data,
                            mediaContent.Extension,
                            mediaContent.DisplayName,
                            shouldAutoplay,
                            previewRequest.Value.CancellationToken);
                        if (!webViewReady)
                        {
                            ThrowIfPreviewIsObsolete(previewRequest.Value);
                            await ShowUnsupportedPreviewAsync(mediaContent.Extension, previewRequest.Value);
                            return;
                        }

                        ThrowIfPreviewIsObsolete(previewRequest.Value);
                        _viewModel.ShowContentPreview(PreviewState.Media);
                        _activeContentPreviewer = Previewer.WebView;
                    }
                    break;

                case Previewer.AvalonEdit:
                    if (content is ValueTuple<string, IHighlightingDefinition> textData)
                    {
                        _textEditorPreview.Text = textData.Item1;
                        _textEditorPreview.SyntaxHighlighting = textData.Item2;
                        _viewModel.ShowContentPreview(PreviewState.Text);
                        _textEditorPreview.Focus();
                        _activeContentPreviewer = Previewer.AvalonEdit;
                    }
                    break;

                case Previewer.StatusPanel:
                    if (content is string extension)
                    {
                        bool isImageExt = SupportedFileTypes.IsImage(extension);

                        // Check if there is currently a file (valid) in the left panel
                        // (which means Dual View should be maintained and we show the image error on the right)
                        bool isLeftPanelOccupied = _viewModel.ContentPreviewState == PreviewState.Text || _viewModel.ContentPreviewState == PreviewState.Media;

                        if (isImageExt && isLeftPanelOccupied)
                        {
                            // Dual View Scenario: Keep left panel active, show error on the right
                            _viewModel.ShowImageUnsupported(extension);
                            _activeImagePreviewer = Previewer.StatusPanel;
                        }
                        else
                        {
                            // Full Screen or Left-only Scenario: Show error on the left
                            _mediaWebViewPreviewService.Deactivate();

                            _viewModel.ShowContentUnsupported(extension);
                            _activeContentPreviewer = Previewer.StatusPanel;

                            if (isImageExt)
                            {
                                _viewModel.ClearImagePreview();
                                _activeImagePreviewer = Previewer.None;
                            }
                        }
                    }
                    else
                    {
                        _mediaWebViewPreviewService.Deactivate();

                        _viewModel.ResetAllVisibility();
                        _imagePreview.Source = null;
                        _activeContentPreviewer = Previewer.None;
                        _activeImagePreviewer = Previewer.None;
                    }
                    break;
            }
        }

        private async Task ShowImagePreviewAsync(byte[] data, string extension, PreviewRequest previewRequest)
        {
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

            await SetPreviewerAsync(Previewer.Image, bitmap, false, previewRequest);
        }

        private async Task ShowTexturePreviewAsync(byte[] data, string extension, PreviewRequest previewRequest)
        {
            var bitmapSource = await Task.Run(() =>
            {
                previewRequest.CancellationToken.ThrowIfCancellationRequested();
                using var stream = new MemoryStream(data);
                return TextureUtils.LoadTexture(stream, extension);
            }, previewRequest.CancellationToken);

            if (bitmapSource != null)
            {
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
                var drawingImage = await Task.Run(() =>
                {
                    previewRequest.CancellationToken.ThrowIfCancellationRequested();
                    return _svgParser.LoadSvg(data);
                }, previewRequest.CancellationToken);
                if (drawingImage != null)
                {
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
                    await ShowPreviewErrorAsync(".svg", previewRequest);
                }
            }
        }

        private async Task ShowUnsupportedPreviewAsync(string extension, PreviewRequest previewRequest)
        {
            await SetPreviewerAsync(Previewer.StatusPanel, extension, false, previewRequest);
        }

        private void ShowEncryptedRiotTexturePreview(PreviewRequest previewRequest)
        {
            ThrowIfPreviewIsObsolete(previewRequest);
            _viewModel.ShowEncryptedRiotTexture();
            _activeImagePreviewer = Previewer.StatusPanel;
        }

        private Task ShowPreviewErrorAsync(string extension, PreviewRequest previewRequest)
        {
            ThrowIfPreviewIsObsolete(previewRequest);

            bool isImage = SupportedFileTypes.IsImage(extension);
            bool hasContentPreview = _viewModel.ContentPreviewState == PreviewState.Text ||
                                     _viewModel.ContentPreviewState == PreviewState.Media;

            if (isImage && hasContentPreview)
            {
                _viewModel.ShowImageError(extension);
                _activeImagePreviewer = Previewer.StatusPanel;
            }
            else
            {
                _mediaWebViewPreviewService.Deactivate();
                _viewModel.ShowContentError(extension);
                _activeContentPreviewer = Previewer.StatusPanel;

                if (isImage)
                {
                    _viewModel.ClearImagePreview();
                    _activeImagePreviewer = Previewer.None;
                }
            }

            return Task.CompletedTask;
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

        public void ReleaseResources()
        {
            CancelCurrentPreview();
            try
            {
                _mediaWebViewPreviewService.ReleaseResources();
            }
            finally
            {
                _imagePreview = null;
                _textEditorPreview = null;
                _viewModel = null;
            }
        }
    }
}
