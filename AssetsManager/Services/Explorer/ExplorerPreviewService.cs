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
using LeagueToolkit.Core.Wad;
using Microsoft.Web.WebView2.Wpf;
using LeagueToolkit.Core.Renderer;
using BCnEncoder.Shared;
using System.Runtime.InteropServices;
using AssetsManager.Services.Parsers;
using System.Windows;
using System.Text.Json;
using System.Reflection;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using AssetsManager.Views.Models.Explorer;
using AssetsManager.Utils;
using AssetsManager.Services.Core;
using AssetsManager.Services.Formatting;
using AssetsManager.Services.Comparator;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;

namespace AssetsManager.Services.Explorer
{
    public class ExplorerPreviewService
    {
        private enum Previewer { None, Image, WebView, AvalonEdit, Placeholder }
        private Previewer _activePreviewer = Previewer.None;
        private FileSystemNodeModel _currentlyDisplayedNode;

        private Image _imagePreview;
        private Grid _webViewContainer;
        private TextEditor _textEditorPreview;
        private Panel _previewPlaceholder;
        private Panel _selectFileMessagePanel;
        private Panel _unsupportedFileMessagePanel;

        private TextBlock _unsupportedFileTextBlock;
        private UserControl _detailsPreview;

        private IHighlightingDefinition _jsonHighlightingDefinition;

        private readonly LogService _logService;
        private readonly DirectoriesCreator _directoriesCreator;
        private readonly WadDifferenceService _wadDifferenceService;
        private readonly ContentFormatterService _contentFormatterService;
        private readonly WemConversionService _wemConversionService;
        private readonly WadExtractionService _wadExtractionService;

        public ExplorerPreviewService(LogService logService, DirectoriesCreator directoriesCreator, WadDifferenceService wadDifferenceService, ContentFormatterService contentFormatterService, WemConversionService wemConversionService, WadExtractionService wadExtractionService)
        {
            _logService = logService;
            _directoriesCreator = directoriesCreator;
            _wadDifferenceService = wadDifferenceService;
            _contentFormatterService = contentFormatterService;
            _wemConversionService = wemConversionService;
            _wadExtractionService = wadExtractionService;
        }

        public void Initialize(Image imagePreview, Grid webViewContainer, TextEditor textEditor, Panel placeholder, Panel selectFileMessage, Panel unsupportedFileMessage, TextBlock unsupportedFileTextBlock, UserControl detailsPreview)
        {
            _imagePreview = imagePreview;
            _webViewContainer = webViewContainer;
            _textEditorPreview = textEditor;
            _previewPlaceholder = placeholder;
            _selectFileMessagePanel = selectFileMessage;
            _unsupportedFileMessagePanel = unsupportedFileMessage;
            _unsupportedFileTextBlock = unsupportedFileTextBlock;
            _detailsPreview = detailsPreview;
        }

        public async Task ShowPreviewAsync(FileSystemNodeModel node)
        {
            await SetPreviewerAsync(Previewer.None); // Blank the preview area immediately

            _currentlyDisplayedNode = node;

            if (node == null || node.Type == NodeType.RealDirectory || node.Type == NodeType.VirtualDirectory || node.Type == NodeType.WadFile || SupportedFileTypes.AudioBank.Contains(node.Extension))
            {
                await ResetPreviewAsync();
                return;
            }

            try
            {
                // This is a virtual file inside a WAD archive.
                if (node.Type == NodeType.VirtualFile)
                {
                    await PreviewWadFile(node);
                }
                // This is a physical file on the disk, used when in Directory Mode.
                else if (node.Type == NodeType.RealFile)
                {
                    await PreviewRealFile(node);
                }
                // This is a special node representing a WEM sound from an audio bank.
                else if (node.Type == NodeType.WemFile)
                {
                    byte[] wemData = await _wadExtractionService.GetWemFileBytesAsync(node);
                    if (wemData != null)
                    {
                        await DispatchPreview(wemData, ".wem");
                    }
                    else
                    {
                        await ShowUnsupportedPreviewAsync(node.Extension);
                    }
                }
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Failed to preview file '{node.FullPath}'.");
                await ShowUnsupportedPreviewAsync(node.Extension);
            }
        }

        public async Task ResetPreviewAsync()
        {
            _currentlyDisplayedNode = null;
            await SetPreviewerAsync(Previewer.Placeholder);
        }

        private async Task PreviewRealFile(FileSystemNodeModel node)
        {
            if (!File.Exists(node.FullPath))
            {
                await ShowUnsupportedPreviewAsync("File not found");
                return;
            }

            byte[] fileData = await File.ReadAllBytesAsync(node.FullPath);
            await DispatchPreview(fileData, node.Extension);
        }

        private async Task PreviewWadFile(FileSystemNodeModel node)
        {
            byte[] decompressedData = await _wadExtractionService.GetVirtualFileBytesAsync(node);

            if (decompressedData == null)
            {
                await ShowUnsupportedPreviewAsync(node.Extension);
                return;
            }

            await DispatchPreview(decompressedData, node.Extension);
        }

        private async Task DispatchPreview(byte[] data, string extension)
        {
            // Aseguramos la creacion de la carpeta necesaria
            await _directoriesCreator.CreateDirTempPreviewAsync();

            if (SupportedFileTypes.Images.Contains(extension)) { await ShowImagePreviewAsync(data); }
            else if (SupportedFileTypes.Textures.Contains(extension)) { await ShowTexturePreviewAsync(data); }
            else if (SupportedFileTypes.VectorImages.Contains(extension)) { await ShowSvgPreviewAsync(data); }
            else if (SupportedFileTypes.Media.Contains(extension))
            {
                if (extension == ".wem")
                {
                    byte[] oggData = await _wemConversionService.ConvertWemToOggAsync(data);
                    if (oggData != null)
                    {
                        await ShowAudioVideoPreviewAsync(oggData, ".ogg");
                    }
                    else
                    {
                        await ShowUnsupportedPreviewAsync(".wem");
                    }
                }
                else
                {
                    await ShowAudioVideoPreviewAsync(data, extension);
                }
            }
            else if (SupportedFileTypes.Json.Contains(extension) || SupportedFileTypes.JavaScript.Contains(extension) || SupportedFileTypes.Css.Contains(extension) || SupportedFileTypes.Bin.Contains(extension) || SupportedFileTypes.Troybin.Contains(extension) || SupportedFileTypes.StringTable.Contains(extension) || SupportedFileTypes.Preload.Contains(extension) || SupportedFileTypes.PlainText.Contains(extension)) { await ShowAvalonEditTextPreviewAsync(data, extension); }
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
            // Part 1: Hide all static panels and clear their content.
            _imagePreview.Visibility = Visibility.Collapsed;
            _textEditorPreview.Visibility = Visibility.Collapsed;
            _previewPlaceholder.Visibility = Visibility.Collapsed;
            _detailsPreview.Visibility = Visibility.Collapsed;
            _imagePreview.Source = null;
            _textEditorPreview.Clear();

            // Part 2: If the previous previewer was a WebView, destroy it.
            if (_activePreviewer == Previewer.WebView)
            {
                // Find the WebView2 instance in the container, dispose it, and remove it.
                var webView = _webViewContainer.Children.OfType<WebView2>().FirstOrDefault();
                if (webView != null)
                {
                    webView.Dispose();
                    _webViewContainer.Children.Remove(webView);
                }
            }

            _activePreviewer = newPreviewer;

            // Part 3: Show the new content.
            switch (newPreviewer)
            {
                case Previewer.Image:
                    if (content is ImageSource imageSource)
                    {
                        _imagePreview.Source = imageSource;
                        _imagePreview.Visibility = Visibility.Visible;
                    }
                    break;

                case Previewer.WebView:
                    if (content is string htmlContent)
                    {
                        // This is the new "nuclear" option. Create, initialize, and show a new WebView.
                        await CreateAndShowWebViewAsync(htmlContent, shouldAutoplay);
                    }
                    break;

                case Previewer.AvalonEdit:
                    if (content is ValueTuple<string, IHighlightingDefinition> textData)
                    {
                        _textEditorPreview.Text = textData.Item1;
                        _textEditorPreview.SyntaxHighlighting = textData.Item2;
                        _textEditorPreview.Visibility = Visibility.Visible;
                        _textEditorPreview.Focus();
                    }
                    break;

                case Previewer.Placeholder:
                    _previewPlaceholder.Visibility = Visibility.Visible;
                    if (content is string extension)
                    {
                        // This is for unsupported files
                        _selectFileMessagePanel.Visibility = Visibility.Collapsed;
                        _unsupportedFileMessagePanel.Visibility = Visibility.Visible;
                        _unsupportedFileTextBlock.Text = $"Preview not available for '{extension}' files.";
                    }
                    else
                    {
                        // This is for the default "Select a file" message
                        _selectFileMessagePanel.Visibility = Visibility.Visible;
                        _unsupportedFileMessagePanel.Visibility = Visibility.Collapsed;
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
                    DefaultBackgroundColor = System.Drawing.Color.FromArgb(37, 37, 38)
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
                await using var stream = new MemoryStream(data);

                WpfDrawingSettings settings = new WpfDrawingSettings();
                settings.IncludeRuntime = false;
                settings.TextAsGeometry = true;

                StreamSvgConverter converter = new StreamSvgConverter(settings);

                using (MemoryStream dummyOutputStream = new MemoryStream())
                {
                    converter.Convert(stream, dummyOutputStream);
                }

                DrawingGroup drawing = converter.Drawing;

                await SetPreviewerAsync(Previewer.Image, new DrawingImage(drawing));
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to show SVG preview.");
                await ShowUnsupportedPreviewAsync(".svg");
            }
        }

        private async Task ShowAudioVideoPreviewAsync(byte[] data, string extension)
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

                var htmlContent = $@"
                    <!DOCTYPE html>
                    <html>
                    <head>
                        <meta charset='UTF-8'>
                        <style>
                            html, body {{
                                background-color: #252526 !important;
                                margin: 0;
                                padding: 0;
                                height: 100vh;
                                display: flex;
                                justify-content: center;
                                align-items: center;
                                overflow: hidden;
                            }}
                            
                            {tag} {{
                                width: {(tag == "audio" ? "300px" : "auto")};
                                height: {(tag == "audio" ? "80px" : "auto")};
                                max-width: 100%;
                                max-height: 100%;
                                background-color: #252526;
                                object-fit: contain;
                                opacity: 0;
                                transition: opacity 0.1s ease-out;
                            }}
                            
                            {tag}.loaded {{
                                opacity: 1;
                            }}
                        </style>
                    </head>
                    <body>
                        <{tag} id='mediaElement' controls preload='auto' {extraAttributes}>
                            <source src='{fileUrl}' type='{mimeType}'>
                            Your browser doesn't support this {(tag == "video" ? "video" : "audio")} format.
                        </{tag}>
                        <script>
                            const mediaElement = document.getElementById('mediaElement');
                            
                            // Función para reproducir (llamada desde C#)
                            window.playMedia = function() {{
                                if (mediaElement.readyState >= 2) {{
                                    mediaElement.play().catch(e => console.log('Play error:', e));
                                }} else {{
                                    mediaElement.addEventListener('canplay', function() {{
                                        mediaElement.play().catch(e => console.log('Play error:', e));
                                    }}, {{ once: true }});
                                }}
                            }};
                            
                            // Mostrar el elemento una vez que esté listo
                            mediaElement.addEventListener('loadeddata', function() {{
                                mediaElement.classList.add('loaded');
                            }});
                            
                            // Manejar errores
                            mediaElement.addEventListener('error', function() {{
                                console.error('Error loading media');
                                mediaElement.style.opacity = '1';
                            }});
                            
                            // Fallback: mostrar después de 1 segundo si no hay evento loadeddata
                            setTimeout(function() {{
                                if (!mediaElement.classList.contains('loaded')) {{
                                    mediaElement.classList.add('loaded');
                                }}
                            }}, 1000);
                        </script>
                    </body>
                    </html>";

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
            await SetPreviewerAsync(Previewer.Placeholder, extension);
        }

        public async Task<ImageSource> GetImagePreviewAsync(FileSystemNodeModel node)
        {
            if (node == null || (!SupportedFileTypes.Images.Contains(node.Extension) && !SupportedFileTypes.Textures.Contains(node.Extension)))
            {
                return null;
            }

            try
            {
                byte[] data = node.Type switch
                {
                    NodeType.VirtualFile => await _wadExtractionService.GetVirtualFileBytesAsync(node),
                    NodeType.RealFile => await File.ReadAllBytesAsync(node.FullPath),
                    _ => null
                };

                if (data == null) return null;

                if (SupportedFileTypes.Images.Contains(node.Extension))
                {
                    return await Task.Run(() =>
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
                }
                else if (SupportedFileTypes.Textures.Contains(node.Extension))
                {
                    return await Task.Run(() => TextureUtils.LoadTexture(new MemoryStream(data), node.Extension));
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
