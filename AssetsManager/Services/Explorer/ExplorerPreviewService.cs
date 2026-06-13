using System;
using System.Xml;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Collections.Generic;
using System.Windows.Media;
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
        private Previewer _activeContentPreviewer = Previewer.None;
        private Previewer _activeImagePreviewer = Previewer.None;
        private FileSystemNodeModel _currentContentNode;
        private FileSystemNodeModel _currentImageNode;
        private Image _imagePreview;
        private Grid _webViewContainer;
        private TextEditor _textEditorPreview;
        private FilePreviewerModel _viewModel;
        private IHighlightingDefinition _jsonHighlightingDefinition;

        private readonly LogService _logService;
        private readonly DirectoriesCreator _directoriesCreator;
        private readonly ContentFormatterService _contentFormatterService;
        private readonly AudioConversionService _audioConversionService;
        private readonly WadContentProvider _wadContentProvider;
        private readonly SvgParser _svgParser;
        private readonly NarrativeMetadataService _narrativeMetadataService;
        private readonly DiffViewService _diffViewService;

        private bool _isGridActive;

        public ExplorerPreviewService(
            LogService logService, 
            DirectoriesCreator directoriesCreator, 
            ContentFormatterService contentFormatterService, 
            AudioConversionService audioConversionService, 
            WadContentProvider wadContentProvider,
            SvgParser svgParser,
            NarrativeMetadataService narrativeMetadataService,
            DiffViewService diffViewService)
        {
            _logService = logService;
            _directoriesCreator = directoriesCreator;
            _contentFormatterService = contentFormatterService;
            _audioConversionService = audioConversionService;
            _wadContentProvider = wadContentProvider;
            _svgParser = svgParser;
            _narrativeMetadataService = narrativeMetadataService;
            _diffViewService = diffViewService;
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
                }
                _viewModel.IsTextVisible = false;
                _viewModel.IsWebVisible = false;
            }

            // Step 2: Discovery of technical metadata (e.g., Summoner Icons, Emotes)
            // We only update/clear metadata if the current node is an image. 
            // If it's a text file, we keep the metadata of the image shown in the other slot (Dual View).
            var metadata = await _narrativeMetadataService.GetMetadataAsync(node);
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
                if (node.Type == NodeType.VirtualFile) { data = await _wadContentProvider.GetVirtualFileBytesAsync(node); }
                else if (node.Type == NodeType.RealFile) { if (File.Exists(node.VirtualPath)) data = await File.ReadAllBytesAsync(node.VirtualPath); }
                else if (node.Type == NodeType.WemFile) { data = await _wadContentProvider.GetWemFileBytesAsync(node); }

                if (data != null) { await DispatchPreview(data, node.Extension, node); }
                else { await ShowUnsupportedPreviewAsync(node.Extension); }
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Failed to preview file '{node.VirtualPath}'.");
                await ShowUnsupportedPreviewAsync(node.Extension);
            }
        }

        public async Task ResetPreviewAsync()
        {
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

        private async Task PreviewRealFile(FileSystemNodeModel node)
        {
            if (!File.Exists(node.VirtualPath))
            {
                await ShowUnsupportedPreviewAsync("File not found");
                return;
            }

            byte[] fileData = await File.ReadAllBytesAsync(node.VirtualPath);
            await DispatchPreview(fileData, node.Extension, node);
        }

        private async Task PreviewWadFile(FileSystemNodeModel node)
        {
            byte[] decompressedData = await _wadContentProvider.GetVirtualFileBytesAsync(node);

            if (decompressedData == null)
            {
                await ShowUnsupportedPreviewAsync(node.Extension);
                return;
            }

            await DispatchPreview(decompressedData, node.Extension, node);
        }

        private async Task DispatchPreview(byte[] data, string extension, FileSystemNodeModel node)
        {
            // Aseguramos la creacion de la carpeta necesaria
            _directoriesCreator.CreateDirectory(_directoriesCreator.TempPreviewPath);

            if (extension.Equals(".tga", StringComparison.OrdinalIgnoreCase) || SupportedFileTypes.Textures.Contains(extension)) { await ShowTexturePreviewAsync(data, extension); }
            else if (SupportedFileTypes.Images.Contains(extension)) { await ShowImagePreviewAsync(data); }
            else if (SupportedFileTypes.VectorImages.Contains(extension)) { await ShowSvgPreviewAsync(data); }
            else if (SupportedFileTypes.Media.Contains(extension))
            {
                if (extension == ".wem")
                {
                    byte[] oggData = await _audioConversionService.ConvertAudioToFormatAsync(data, ".wem", AudioExportFormat.Ogg);
                    if (oggData != null)
                    {
                        await ShowAudioVideoPreviewAsync(oggData, ".ogg", node.Name);
                    }
                    else
                    {
                        await ShowUnsupportedPreviewAsync(".wem");
                    }
                }
                else
                {
                    await ShowAudioVideoPreviewAsync(data, extension, node.Name);
                }
            }
            else if (SupportedFileTypes.IsText(extension)) { await ShowAvalonEditTextPreviewAsync(data, extension); }
            else { await ShowUnsupportedPreviewAsync(extension); }
        }

        private async Task ShowAvalonEditTextPreviewAsync(byte[] data, string extension)
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

                await SetPreviewerAsync(Previewer.AvalonEdit, (textContent, syntaxHighlighting));
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Failed to show text preview for extension {extension}");
                string errorText = $"Error showing {extension} file.";
                await SetPreviewerAsync(Previewer.AvalonEdit, (errorText, (IHighlightingDefinition)null));
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

        private async Task SetPreviewerAsync(Previewer newPreviewer, object content = null, bool shouldAutoplay = false)
        {
            // Dispose of WebView only when we are explicitly replacing it in the left Content slot
            if (newPreviewer == Previewer.AvalonEdit && _webViewContainer != null)
            {
                var oldWebView = _webViewContainer.Children.OfType<WebView2>().FirstOrDefault();
                if (oldWebView != null)
                {
                    oldWebView.Dispose();
                    _webViewContainer.Children.Remove(oldWebView);
                }
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
                    if (content is string htmlContent)
                    {
                        var oldWebView = _webViewContainer.Children.OfType<WebView2>().FirstOrDefault();
                        if (oldWebView != null) { oldWebView.Dispose(); _webViewContainer.Children.Remove(oldWebView); }

                        await CreateAndShowWebViewAsync(htmlContent, shouldAutoplay);
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

                        // Check if there is currently a file (valid or showing error) in the left panel
                        // (which means Dual View should be maintained and we show the image error on the right)
                        bool isLeftPanelOccupied = _viewModel.IsContentVisible;

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
                                var oldWebView = _webViewContainer.Children.OfType<WebView2>().FirstOrDefault();
                                if (oldWebView != null) { oldWebView.Dispose(); _webViewContainer.Children.Remove(oldWebView); }
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
                            var oldWebView = _webViewContainer.Children.OfType<WebView2>().FirstOrDefault();
                            if (oldWebView != null) { oldWebView.Dispose(); _webViewContainer.Children.Remove(oldWebView); }
                        }

                        _viewModel.ResetAllVisibility();
                        _imagePreview.Source = null;
                        _activeContentPreviewer = Previewer.None;
                        _activeImagePreviewer = Previewer.None;
                    }
                    break;
            }
        }

        private async Task CreateAndShowWebViewAsync(string htmlContent, bool shouldAutoplay)
        {
            try
            {
                var webView = new WebView2()
                {
                    DefaultBackgroundColor = System.Drawing.Color.Transparent
                };

                _webViewContainer.Children.Add(webView);

                // Initialize CoreWebView2
                var environment = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(userDataFolder: _directoriesCreator.WebView2DataPath);
                await webView.EnsureCoreWebView2Async(environment);

                // Set settings and mappings
                webView.CoreWebView2.SetVirtualHostNameToFolderMapping("preview.assets", _directoriesCreator.TempPreviewPath, Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                webView.CoreWebView2.Settings.IsSwipeNavigationEnabled = false;

                // Navigate and handle autoplay
                void OnNavigationCompleted(object sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs args)
                {
                    webView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
                    if (shouldAutoplay)
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
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to create, initialize, and show WebView2.");
                // Clean up a failed creation
                var webView = _webViewContainer.Children.OfType<WebView2>().FirstOrDefault();
                if (webView != null)
                {
                    webView.Dispose();
                    _webViewContainer.Children.Remove(webView);
                }
                await ShowUnsupportedPreviewAsync(".media"); // Show a generic error
            }
        }

        private async Task ShowImagePreviewAsync(byte[] data)
        {
            var bitmap = await Task.Run(() =>
            {
                using var stream = new MemoryStream(data);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = stream;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            });

            await SetPreviewerAsync(Previewer.Image, bitmap);
        }

        private async Task ShowTexturePreviewAsync(byte[] data, string extension)
        {
            var bitmapSource = await Task.Run(() =>
            {
                using var stream = new MemoryStream(data);
                return TextureUtils.LoadTexture(stream, extension);
            });

            if (bitmapSource != null)
            {
                await SetPreviewerAsync(Previewer.Image, bitmapSource);
            }
            else
            {
                await ShowUnsupportedPreviewAsync(extension);
            }
        }

        private async Task ShowSvgPreviewAsync(byte[] data)
        {
            try
            {
                var drawingImage = await Task.Run(() => _svgParser.LoadSvg(data));
                if (drawingImage != null)
                {
                    await SetPreviewerAsync(Previewer.Image, drawingImage);
                }
                else
                {
                    await ShowUnsupportedPreviewAsync(".svg");
                }
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to show SVG preview.");
                await ShowUnsupportedPreviewAsync(".svg");
            }
        }

        private async Task ShowAudioVideoPreviewAsync(byte[] data, string extension, string displayName)
        {
            if (_webViewContainer == null)
            {
                await ShowUnsupportedPreviewAsync(extension);
                return;
            }

            try
            {
                // Clean up previous temp files before creating a new one.
                await Task.Run(() =>
                {
                    try
                    {
                        foreach (var file in Directory.GetFiles(_directoriesCreator.TempPreviewPath))
                        {
                            File.Delete(file);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logService.LogError(ex, "Failed to clean temp files");
                    }
                });

                var tempFileName = $"preview_{DateTime.Now.Ticks}{extension}";
                var tempFilePath = Path.Combine(_directoriesCreator.TempPreviewPath, tempFileName);
                await File.WriteAllBytesAsync(tempFilePath, data);

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

                await SetPreviewerAsync(Previewer.WebView, htmlContent, shouldAutoplay: true);
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Failed to create and show preview for {extension} file.");
                await ShowUnsupportedPreviewAsync(extension);
            }
        }

        private async Task ShowUnsupportedPreviewAsync(string extension)
        {
            await SetPreviewerAsync(Previewer.StatusPanel, extension);
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

        public async Task<ImageSource> GetImagePreviewAsync(FileSystemNodeModel node, int maxWidth = 0)
        {
            if (node == null || !SupportedFileTypes.IsImage(node.Extension))
            {
                return null;
            }

            try
            {
                byte[] data = node.Type switch
                {
                    NodeType.VirtualFile => await _wadContentProvider.GetVirtualFileBytesAsync(node),
                    NodeType.RealFile => await File.ReadAllBytesAsync(node.VirtualPath),
                    _ => null
                };

                if (data == null) return null;

                int? size = maxWidth > 0 ? maxWidth : null;

                if (SupportedFileTypes.Images.Contains(node.Extension))
                {
                    return await Task.Run(() =>
                    {
                        using var stream = new MemoryStream(data);
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.StreamSource = stream;
                        if (size.HasValue) bmp.DecodePixelWidth = size.Value;
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.EndInit();
                        bmp.Freeze();
                        return bmp;
                    });
                }
                else if (SupportedFileTypes.Textures.Contains(node.Extension))
                {
                    return await Task.Run(() => TextureUtils.LoadTexture(new MemoryStream(data), node.Extension, size, size));
                }
                else if (SupportedFileTypes.VectorImages.Contains(node.Extension))
                {
                    return await Task.Run(() => _svgParser.LoadSvg(data));
                }
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Failed to get image preview for '{node.VirtualPath}'.");
                return null;
            }

            return null;
        }

        public async Task ShowFileDiffAsync(string oldPath, string newPath, Window owner)
        {
            await _diffViewService.ShowFileDiffAsync(oldPath, newPath, owner);
        }
    }
}
