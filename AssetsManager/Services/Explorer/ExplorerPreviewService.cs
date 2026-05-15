using System;
using System.Xml;
using System.IO;
using System.Linq;
using System.Text;
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
        private Previewer _activePreviewer = Previewer.None;
        private FileSystemNodeModel _currentContentNode;
        private FileSystemNodeModel _currentImageNode;
        private Image _imagePreview;
        private Grid _webViewContainer;
        private TextEditor _textEditorPreview;
        private FilePreviewerModel _viewModel;
        private IHighlightingDefinition _jsonHighlightingDefinition;

        private readonly LogService _logService;
        private readonly DirectoriesCreator _directoriesCreator;
        private readonly WadDiffProvider _wadDiffProvider;
        private readonly ContentFormatterService _contentFormatterService;
        private readonly AudioConversionService _audioConversionService;
        private readonly WadContentProvider _wadContentProvider;
        private readonly SvgParser _svgParser;

        public ExplorerPreviewService(
            LogService logService, 
            DirectoriesCreator directoriesCreator, 
            WadDiffProvider wadDiffProvider, 
            ContentFormatterService contentFormatterService, 
            AudioConversionService audioConversionService, 
            WadContentProvider wadContentProvider,
            SvgParser svgParser)
        {
            _logService = logService;
            _directoriesCreator = directoriesCreator;
            _wadDiffProvider = wadDiffProvider;
            _contentFormatterService = contentFormatterService;
            _audioConversionService = audioConversionService;
            _wadContentProvider = wadContentProvider;
            _svgParser = svgParser;
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
                    return;
                }

                await ResetPreviewAsync();
                return;
            }

            bool isImage = SupportedFileTypes.Images.Contains(node.Extension) || 
                           SupportedFileTypes.Textures.Contains(node.Extension) || 
                           SupportedFileTypes.VectorImages.Contains(node.Extension);

            // Per-Slot Early Exit:
            // Check if the node is already loaded in its corresponding slot.
            // This prevents reloads when alternating focus in Dual View.
            if (isImage)
            {
                if (_currentImageNode == node && _viewModel.IsImageVisible) return;
            }
            else
            {
                if (_currentContentNode == node && (_viewModel.IsTextVisible || _viewModel.IsWebVisible || _viewModel.IsUnsupportedVisible)) return;
            }

            // Step 1: Tell the ViewModel to prepare the correct slot (Image or Content)
            _viewModel.PrepareSlotForFile(node);

            // Step 2: SELECTIVE clearing to maintain Dual View
            if (isImage)
            {
                _imagePreview.Source = null;
                _currentImageNode = node;
            }
            else
            {
                _textEditorPreview.Clear();
                // Note: WebView cleanup is handled inside SetPreviewerAsync 
                // when the new media is ready to be injected.
                _currentContentNode = node;
            }

            try
            {
                byte[] data = null;
                if (node.Type == NodeType.VirtualFile) { data = await _wadContentProvider.GetVirtualFileBytesAsync(node); }
                else if (node.Type == NodeType.RealFile) { if (File.Exists(node.FullPath)) data = await File.ReadAllBytesAsync(node.FullPath); }
                else if (node.Type == NodeType.WemFile) { data = await _wadContentProvider.GetWemFileBytesAsync(node); }

                if (data != null) { await DispatchPreview(data, node.Extension, node); }
                else { await ShowUnsupportedPreviewAsync(node.Extension); }
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Failed to preview file '{node.FullPath}'.");
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

            // Step 2: Restore the UI state
            await SetPreviewerAsync(Previewer.StatusPanel);
        }

        private async Task PreviewRealFile(FileSystemNodeModel node)
        {
            if (!File.Exists(node.FullPath))
            {
                await ShowUnsupportedPreviewAsync("File not found");
                return;
            }

            byte[] fileData = await File.ReadAllBytesAsync(node.FullPath);
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

            if (SupportedFileTypes.Images.Contains(extension)) { await ShowImagePreviewAsync(data); }
            else if (SupportedFileTypes.Textures.Contains(extension)) { await ShowTexturePreviewAsync(data); }
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
            else if (SupportedFileTypes.Json.Contains(extension) || SupportedFileTypes.JavaScript.Contains(extension) || SupportedFileTypes.Css.Contains(extension) || SupportedFileTypes.Bin.Contains(extension) || SupportedFileTypes.Troybin.Contains(extension) || SupportedFileTypes.StringTable.Contains(extension) || SupportedFileTypes.Preload.Contains(extension) || SupportedFileTypes.PlainText.Contains(extension) || SupportedFileTypes.Lua.Contains(extension)) { await ShowAvalonEditTextPreviewAsync(data, extension); }
            else { await ShowUnsupportedPreviewAsync(extension); }
        }

        private async Task ShowAvalonEditTextPreviewAsync(byte[] data, string extension)
        {
            try
            {
                string dataType = extension.TrimStart('.');
                string textContent = await _contentFormatterService.GetFormattedStringAsync(dataType, data);

                IHighlightingDefinition syntaxHighlighting = null;
                if (dataType == "json" || dataType == "bin" || dataType == "troybin" || dataType == "css" || dataType == "stringtable" || dataType == "js" || dataType == "preload")
                {
                    syntaxHighlighting = GetJsonHighlighting();
                }
                else if (dataType == "luabin64")
                {
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
            _activePreviewer = newPreviewer;

            switch (newPreviewer)
            {
                case Previewer.Image:
                    if (content is ImageSource imageSource)
                    {
                        _imagePreview.Source = imageSource;
                        _viewModel.IsImageVisible = true;
                    }
                    break;

                case Previewer.WebView:
                    if (content is string htmlContent)
                    {
                        var oldWebView = _webViewContainer.Children.OfType<WebView2>().FirstOrDefault();
                        if (oldWebView != null) { oldWebView.Dispose(); _webViewContainer.Children.Remove(oldWebView); }

                        await CreateAndShowWebViewAsync(htmlContent, shouldAutoplay);
                        _viewModel.IsTextVisible = false;
                        _viewModel.IsWebVisible = true;
                    }
                    break;

                case Previewer.AvalonEdit:
                    if (content is ValueTuple<string, IHighlightingDefinition> textData)
                    {
                        var oldWebView = _webViewContainer.Children.OfType<WebView2>().FirstOrDefault();
                        if (oldWebView != null) { oldWebView.Dispose(); _webViewContainer.Children.Remove(oldWebView); }

                        _textEditorPreview.Text = textData.Item1;
                        _textEditorPreview.SyntaxHighlighting = textData.Item2;
                        _viewModel.IsWebVisible = false;
                        _viewModel.IsTextVisible = true;
                        _textEditorPreview.Focus();
                    }
                    break;

                case Previewer.StatusPanel:
                    if (content is string extension)
                    {
                        _viewModel.IsUnsupportedVisible = true;
                        _viewModel.IsContentVisible = true;
                        _viewModel.IsTextVisible = false;
                        _viewModel.IsWebVisible = false;
                        _viewModel.UnsupportedMessage = $"The {extension} format is not supported to preview it";
                    }
                    else
                    {
                        // Global Reset
                        _viewModel.ResetAllVisibility();
                        _imagePreview.Source = null;
                        _textEditorPreview.Clear();
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
                        webView.Dispatcher.Invoke(async () =>
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

        private async Task ShowTexturePreviewAsync(byte[] data)
        {
            var bitmapSource = await Task.Run(() =>
            {
                using var stream = new MemoryStream(data);
                return TextureUtils.LoadTexture(stream, ".tex"); // Extension doesn't matter much here as LoadTexture handles it
            });

            if (bitmapSource != null)
            {
                await SetPreviewerAsync(Previewer.Image, bitmapSource);
            }
            else
            {
                await ShowUnsupportedPreviewAsync(".tex/.dds");
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

        public async Task<ImageSource> GetImagePreviewAsync(FileSystemNodeModel node, int maxWidth = 0)
        {
            if (node == null || (!SupportedFileTypes.Images.Contains(node.Extension) && !SupportedFileTypes.Textures.Contains(node.Extension) && !SupportedFileTypes.VectorImages.Contains(node.Extension)))
            {
                return null;
            }

            try
            {
                byte[] data = node.Type switch
                {
                    NodeType.VirtualFile => await _wadContentProvider.GetVirtualFileBytesAsync(node),
                    NodeType.RealFile => await File.ReadAllBytesAsync(node.FullPath),
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
                _logService.LogError(ex, $"Failed to get image preview for '{node.FullPath}'.");
                return null;
            }

            return null;
        }
    }
}
