using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using LeagueToolkit.Core.Wad;
using Microsoft.Web.WebView2.Wpf;
using LeagueToolkit.Core.Renderer;
using BCnEncoder.Shared;
using System.Runtime.InteropServices;
using System.Windows;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using System.Text.Json;
using AssetsManager.Views.Models;
using AssetsManager.Utils;
using AssetsManager.Services.Hashes;
using AssetsManager.Views.Helpers;
using AssetsManager.Services.Core;
using AssetsManager.Services.Monitor;
using System.Reflection;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using System.Xml;

namespace AssetsManager.Services.Explorer
{
    public class ExplorerPreviewService
    {
        private enum Previewer { None, Image, WebView, AvalonEdit, Placeholder }
        private Previewer _activePreviewer = Previewer.None;

        private Image _imagePreview;
        private WebView2 _webView2Preview;
        private TextEditor _textEditorPreview;
        private Panel _previewPlaceholder;
        private Panel _selectFileMessagePanel;
        private Panel _unsupportedFileMessagePanel;
        private TextBlock _unsupportedFileTextBlock;

        private readonly LogService _logService;
        private readonly DirectoriesCreator _directoriesCreator;
        private readonly HashResolverService _hashResolverService;
        private readonly JsBeautifierService _jsBeautifierService;

        public ExplorerPreviewService(LogService logService, DirectoriesCreator directoriesCreator, HashResolverService hashResolverService, JsBeautifierService jsBeautifierService)
        {
            _logService = logService;
            _directoriesCreator = directoriesCreator;
            _hashResolverService = hashResolverService;
            _jsBeautifierService = jsBeautifierService;
        }

        public void Initialize(Image imagePreview, WebView2 webView2Preview, TextEditor textEditor, Panel placeholder, Panel selectFileMessage, Panel unsupportedFileMessage, TextBlock unsupportedFileTextBlock)
        {
            _imagePreview = imagePreview;
            _webView2Preview = webView2Preview;
            _textEditorPreview = textEditor;
            _previewPlaceholder = placeholder;
            _selectFileMessagePanel = selectFileMessage;
            _unsupportedFileMessagePanel = unsupportedFileMessage;
            _unsupportedFileTextBlock = unsupportedFileTextBlock;
        }

        public async Task ConfigureWebViewAfterInitializationAsync()
        {
            try
            {
                if (_webView2Preview?.CoreWebView2 == null)
                {
                    _logService.LogWarning("WebView2 not initialized when trying to configure");
                    return;
                }
                
                // Initial page background
                await ClearWebViewAsync();
                
                // Additional configurations
                _webView2Preview.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                _webView2Preview.CoreWebView2.Settings.AreDevToolsEnabled = false;
                _webView2Preview.CoreWebView2.Settings.IsSwipeNavigationEnabled = false;
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to configure WebView2 after initialization");
            }
        }

        public async Task ShowPreviewAsync(FileSystemNodeModel node)
        {
            if (node == null || node.Type == NodeType.RealDirectory || node.Type == NodeType.VirtualDirectory || node.Type == NodeType.WadFile)
            {
                await ResetPreviewAsync();
                return;
            }

            try
            {
                if (node.Type == NodeType.VirtualFile)
                {
                    await PreviewWadFile(node);
                }
                else if (node.Type == NodeType.RealFile)
                {
                    await PreviewRealFile(node);
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
            // Limpiar WebView antes de cambiar al placeholder
            await ClearWebViewAsync();
            
            SetPreviewer(Previewer.Placeholder);
            _selectFileMessagePanel.Visibility = Visibility.Visible;
            _unsupportedFileMessagePanel.Visibility = Visibility.Collapsed;
        }
        
        // Método centralizado para limpiar el WebView
        private async Task ClearWebViewAsync()
        {
            try
            {
                if (_webView2Preview?.CoreWebView2 != null)
                {
                    var blankPage = @"<!DOCTYPE html><html><head><style>html, body {background-color: #252526 !important;margin: 0;padding: 0;height: 100vh;overflow: hidden;}</style></head><body></body></html>";
                    _webView2Preview.CoreWebView2.NavigateToString(blankPage);
                    // Pequeña pausa para que tome efecto
                    await Task.Delay(50);
                }
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to clear WebView");
            }
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
            if (string.IsNullOrEmpty(node.SourceWadPath) || node.SourceChunkPathHash == 0)
            {
                await ShowUnsupportedPreviewAsync(node.Extension);
                return;
            }

            byte[] decompressedData;
            try
            {
                decompressedData = await Task.Run(() =>
                {
                    using var wadFile = new WadFile(node.SourceWadPath);
                    var chunk = wadFile.FindChunk(node.SourceChunkPathHash);
                    using var decompressedOwner = wadFile.LoadChunkDecompressed(chunk);
                    return decompressedOwner.Span.ToArray();
                });
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Failed to decompress chunk for preview: {node.FullPath}");
                await ShowUnsupportedPreviewAsync(node.Extension);
                return;
            }

            await DispatchPreview(decompressedData, node.Extension);
        }

        private async Task DispatchPreview(byte[] data, string extension)
        {
            if (IsImageExtension(extension)) { await ShowImagePreviewAsync(data); }
            else if (IsTextureExtension(extension)) { await ShowTexturePreviewAsync(data); }
            else if (IsVectorImageExtension(extension)) { await ShowSvgPreviewAsync(data); }
            else if (IsJavaScriptExtension(extension)) { await ShowAvalonEditTextPreviewAsync(data, extension); }
            else if (IsTextExtension(extension) || IsBinExtension(extension)) { await ShowAvalonEditTextPreviewAsync(data, extension); }
            else if (IsMediaExtension(extension)) { await ShowAudioVideoPreviewAsync(data, extension); }
            else { await ShowUnsupportedPreviewAsync(extension); }
        }

        private async Task ShowAvalonEditTextPreviewAsync(byte[] data, string extension)
        {
            SetPreviewer(Previewer.AvalonEdit);
            try
            {
                string textContent = string.Empty;

                if (IsBinExtension(extension))
                {
                    using var stream = new MemoryStream(data);
                    var binTree = new BinTree(stream);
                    var binDict = BinUtils.ConvertBinTreeToDictionary(binTree, _hashResolverService);
                    textContent = await JsonDiffHelper.FormatJsonAsync(binDict);
                }
                else
                {
                    textContent = Encoding.UTF8.GetString(data);
                    if (extension == ".js")
                    {
                        textContent = await _jsBeautifierService.BeautifyAsync(textContent);
                    }
                    else
                    {
                        textContent = await JsonDiffHelper.FormatJsonAsync(textContent);
                    }
                }

                if (extension == ".json" || IsBinExtension(extension) || IsJavaScriptExtension(extension))
                {
                    var assembly = Assembly.GetExecutingAssembly();
                    var resourceName = "AssetsManager.Resources.JsonSyntaxHighlighting.xshd";
                    using (var stream = assembly.GetManifestResourceStream(resourceName))
                    {
                        if (stream != null)
                        {
                            using (var reader = new XmlTextReader(stream))
                            {
                                _textEditorPreview.SyntaxHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
                            }
                        }
                        else
                        {
                            _textEditorPreview.SyntaxHighlighting = null;
                        }
                    }
                }
                else
                {
                    _textEditorPreview.SyntaxHighlighting = null;
                }

                _textEditorPreview.Text = textContent;
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Failed to show text preview for extension {extension}");
                _textEditorPreview.Text = $"Error showing {extension} file.";
            }
        }

        // Versión limpia de SetPreviewer
        private void SetPreviewer(Previewer previewer)
        {
            if (_activePreviewer == previewer) return;

            // Limpiar WebView cuando se sale de él o cuando se va a Placeholder
            // if ((_activePreviewer == Previewer.WebView && previewer != Previewer.WebView) || 
            //     previewer == Previewer.Placeholder)
            // {
            //     // Fire and forget - limpieza asíncrona sin bloquear
            //     _ = ClearWebViewAsync();
            // }

            // Manejo especial para transiciones HACIA WebView
            if (previewer == Previewer.WebView && _activePreviewer != Previewer.WebView)
            {
                // Fire and forget - limpieza asíncrona sin bloquear
                _ = ClearWebViewAsync();
            }

            // Ocultar todos los controles primero
            _imagePreview.Visibility = Visibility.Collapsed;
            _webView2Preview.Visibility = Visibility.Collapsed;
            _textEditorPreview.Visibility = Visibility.Collapsed;
            _previewPlaceholder.Visibility = Visibility.Collapsed;

            // Luego mostrar solo el que corresponde
            switch (previewer)
            {
                case Previewer.Image:
                    _imagePreview.Visibility = Visibility.Visible;
                    break;
                case Previewer.WebView:
                    _webView2Preview.Visibility = Visibility.Visible;
                    break;
                case Previewer.AvalonEdit:
                    _textEditorPreview.Visibility = Visibility.Visible;
                    break;
                case Previewer.Placeholder:
                    _previewPlaceholder.Visibility = Visibility.Visible;
                    break;
            }

            _activePreviewer = previewer;
        }

        private async Task ShowImagePreviewAsync(byte[] data)
        {
            SetPreviewer(Previewer.Image);
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
            _imagePreview.Source = bitmap;
        }

        private async Task ShowTexturePreviewAsync(byte[] data)
        {
            SetPreviewer(Previewer.Image);
            var bitmapSource = await Task.Run(() =>
            {
                using var stream = new MemoryStream(data);
                var texture = Texture.Load(stream);
                if (texture.Mips.Length > 0)
                {
                    var mainMip = texture.Mips[0];
                    var width = mainMip.Width;
                    var height = mainMip.Height;
                    if (mainMip.Span.TryGetSpan(out Span<ColorRgba32> pixelSpan))
                    {
                        var pixelBytes = MemoryMarshal.AsBytes(pixelSpan).ToArray();
                        var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixelBytes, width * 4);
                        bmp.Freeze();
                        return bmp;
                    }
                }
                return null;
            });

            if (bitmapSource != null)
            {
                _imagePreview.Source = bitmapSource;
            }
            else
            {
                await ShowUnsupportedPreviewAsync(".tex/.dds");
            }
        }

        private async Task ShowSvgPreviewAsync(byte[] data)
        {
            if (_webView2Preview?.CoreWebView2 == null)
            {
                await ShowUnsupportedPreviewAsync(".svg");
                return;
            }

            try
            {
                // Limpiar ANTES de establecer el previewer para evitar el anterior contenido
                await ClearWebViewAsync();
                
                // Establecer el previewer (esto limpia automáticamente el WebView)
                SetPreviewer(Previewer.WebView);
                
                // Pequeña pausa para permitir que la limpieza tome efecto
                await Task.Delay(30);
            
                string svgContent = Encoding.UTF8.GetString(data);
                var htmlContent = $@"<!DOCTYPE html><html><head><meta charset=""UTF-8""><style>html, body {{background-color: transparent !important;display: flex;justify-content: center;align-items: center;height: 100vh;margin: 0;padding: 20px;box-sizing: border-box;overflow: hidden;}}svg {{width: 90%;height: 90%;object-fit: contain;}}</style></head><body>{svgContent}</body></html>";
                
                _webView2Preview.CoreWebView2.NavigateToString(htmlContent);
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to show SVG preview.");
                await ShowUnsupportedPreviewAsync(".svg");
            }
        }

        private async Task ShowAudioVideoPreviewAsync(byte[] data, string extension)
        {
            if (_webView2Preview?.CoreWebView2 == null)
            {
                await ShowUnsupportedPreviewAsync(extension);
                return;
            }

            try
            {
                // Limpiar ANTES de establecer el previewer para evitar el anterior contenido
                await ClearWebViewAsync();
                
                // Establecer el previewer (esto limpia automáticamente el WebView)
                SetPreviewer(Previewer.WebView);
                
                // Pequeña pausa para permitir que la limpieza tome efecto
                await Task.Delay(30);

                // Limpiar archivos temporales
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

                // Crear el nuevo archivo temporal
                var tempFileName = $"preview_{DateTime.Now.Ticks}{extension}";
                var tempFilePath = Path.Combine(_directoriesCreator.TempPreviewPath, tempFileName);
                await File.WriteAllBytesAsync(tempFilePath, data);

                // Preparar el contenido HTML
                var mimeType = extension switch
                {
                    ".ogg" => "audio/ogg",
                    ".wav" => "audio/wav",
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
                                transition: opacity 0.2s ease-out;
                            }}
                            
                            {tag}.loaded {{
                                opacity: 1;
                            }}
                        </style>
                        <script>
                            document.addEventListener('DOMContentLoaded', function() {{
                                const mediaElement = document.getElementById('mediaElement');
                                
                                // Mostrar el elemento una vez que esté listo
                                mediaElement.addEventListener('loadeddata', function() {{
                                    mediaElement.classList.add('loaded');
                                }});
                                
                                // Manejar errores
                                mediaElement.addEventListener('error', function() {{
                                    console.error('Error loading media');
                                    mediaElement.style.opacity = '1'; // Mostrar aunque haya error
                                }});
                                
                                // Fallback: mostrar después de 1 segundo si no hay evento loadeddata
                                setTimeout(function() {{
                                    if (!mediaElement.classList.contains('loaded')) {{
                                        mediaElement.classList.add('loaded');
                                    }}
                                }}, 1000);
                            }});
                        </script>
                    </head>
                    <body>
                        <{tag} id='mediaElement' controls autoplay {extraAttributes}>
                            <source src='{fileUrl}' type='{mimeType}'>
                            Your browser doesn't support this {(tag == "video" ? "video" : "audio")} format.
                        </{tag}>
                    </body>
                    </html>";

                // Navegar al contenido final
                _webView2Preview.CoreWebView2.NavigateToString(htmlContent);
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Failed to create and show preview for {extension} file.");
                await ShowUnsupportedPreviewAsync(extension);
            }
        }

        private async Task ShowUnsupportedPreviewAsync(string extension)
        {
            SetPreviewer(Previewer.Placeholder);
            _selectFileMessagePanel.Visibility = Visibility.Collapsed;
            _unsupportedFileMessagePanel.Visibility = Visibility.Visible;
            _unsupportedFileTextBlock.Text = $"Preview not available for '{extension}' files.";
            await Task.CompletedTask;
        }

        private bool IsImageExtension(string extension) => extension == ".png" || extension == ".jpg" || extension == ".jpeg";
        private bool IsVectorImageExtension(string extension) => extension == ".svg";
        private bool IsTextureExtension(string extension) => extension == ".tex" || extension == ".dds";
        private bool IsTextExtension(string extension) => extension == ".json" || extension == ".xml" || extension == ".yml" || extension == ".yaml" || extension == ".ini" || extension == ".log" || extension == ".txt";
        private bool IsJavaScriptExtension(string extension) => extension == ".js";
        private bool IsMediaExtension(string extension) => extension == ".ogg" || extension == ".webm";
        private bool IsBinExtension(string extension) => extension == ".bin";
    }
}