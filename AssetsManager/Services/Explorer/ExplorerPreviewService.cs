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
using AssetsManager.Views.Models;
using AssetsManager.Utils;
using AssetsManager.Services.Core;
using AssetsManager.Services.Formatting;
using AssetsManager.Services.Comparator;

namespace AssetsManager.Services.Explorer
{
    public class ExplorerPreviewService
    {
        private enum Previewer { None, Image, WebView, AvalonEdit, Placeholder }
        private Previewer _activePreviewer = Previewer.None;
        private FileSystemNodeModel _currentlyDisplayedNode;

        private Image _imagePreview;
        private WebView2 _webView2Preview;
        private TextEditor _textEditorPreview;
        private Panel _previewPlaceholder;
        private Panel _selectFileMessagePanel;
        private Panel _unsupportedFileMessagePanel;
        private Panel _extensionlessFilePanel;
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

        public void Initialize(Image imagePreview, WebView2 webView2Preview, TextEditor textEditor, Panel placeholder, Panel selectFileMessage, Panel unsupportedFileMessage, Panel extensionlessFilePanel, TextBlock unsupportedFileTextBlock, UserControl detailsPreview)
        {
            _imagePreview = imagePreview;
            _webView2Preview = webView2Preview;
            _textEditorPreview = textEditor;
            _previewPlaceholder = placeholder;
            _selectFileMessagePanel = selectFileMessage;
            _unsupportedFileMessagePanel = unsupportedFileMessage;
            _extensionlessFilePanel = extensionlessFilePanel;
            _unsupportedFileTextBlock = unsupportedFileTextBlock;
            _detailsPreview = detailsPreview;
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
            if (node != null && _currentlyDisplayedNode != null && _currentlyDisplayedNode.FullPath == node.FullPath)
            {
                return;
            }
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
                    // The AudioSource property, set during tree creation, tells us whether the WEM
                    // is sourced from a WPK (standard VO) or a BNK (SFX or VO fallback).
                    if (node.AudioSource == AudioSourceType.Bnk)
                    {
                        await PreviewWemFromBnkAsync(node);
                    }
                    else
                    {
                        await PreviewWemFromWpkAsync(node);
                    }
                }
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Failed to preview file '{node.FullPath}'.");
                await ShowUnsupportedPreviewAsync(node.Extension);
            }
        }

        /// <summary>
        /// Extracts and previews a WEM file that is embedded inside a BNK file.
        /// </summary>
        private async Task PreviewWemFromBnkAsync(FileSystemNodeModel node)
        {
            if (string.IsNullOrEmpty(node.SourceWadPath) || node.WemSize == 0)
            {
                await ShowUnsupportedPreviewAsync(node.Extension);
                return;
            }

            try
            {
                // 1. Get the parent BNK file's data from the WAD.
                byte[] bnkData;
                using (var wadFile = new WadFile(node.SourceWadPath))
                {
                    var chunk = wadFile.FindChunk(node.SourceChunkPathHash);
                    using var decompressedOwner = wadFile.LoadChunkDecompressed(chunk);
                    bnkData = decompressedOwner.Span.ToArray();
                }

                // 2. Extract the specific WEM data from the BNK data using the absolute offset and size.
                byte[] wemData = new byte[node.WemSize];
                Array.Copy(bnkData, node.WemOffset, wemData, 0, node.WemSize);

                // 3. Dispatch for conversion and playback.
                await DispatchPreview(wemData, ".wem");
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Failed to preview audio sound from BNK: {node.Name}");
                await ShowUnsupportedPreviewAsync(node.Extension);
            }
        }

        private async Task PreviewWemFromWpkAsync(FileSystemNodeModel node)
        {
            if (string.IsNullOrEmpty(node.SourceWadPath) || node.SourceChunkPathHash == 0 || node.WemId == 0)
            {
                await ShowUnsupportedPreviewAsync(node.Extension);
                return;
            }

            try
            {
                // Step 1: Get the parent WPK data from the WAD
                byte[] wpkData;
                using (var wadFile = new WadFile(node.SourceWadPath))
                {
                    var chunk = wadFile.FindChunk(node.SourceChunkPathHash);
                    using var decompressedOwner = wadFile.LoadChunkDecompressed(chunk);
                    wpkData = decompressedOwner.Span.ToArray();
                }

                // Step 2: Parse the WPK and extract the WEM data
                byte[] wemData = null;
                await Task.Run(() =>
                {
                    using var wpkStream = new MemoryStream(wpkData);
                    var wpk = WpkParser.Parse(wpkStream, _logService);
                    var wem = wpk.Wems.FirstOrDefault(w => w.Id == node.WemId);
                    if (wem != null)
                    {
                        using var reader = new BinaryReader(wpkStream);
                        wpkStream.Seek(wem.Offset, SeekOrigin.Begin);
                        wemData = reader.ReadBytes((int)wem.Size);
                    }
                });

                if (wemData != null)
                {
                    // Step 3: Dispatch to the media player
                    await DispatchPreview(wemData, ".wem");
                }
                else
                {
                    _logService.LogWarning($"WEM file with ID {node.WemId} not found inside its parent WPK.");
                    await ShowUnsupportedPreviewAsync(node.Extension);
                }
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Failed to preview audio sound: {node.Name}");
                await ShowUnsupportedPreviewAsync(node.Extension);
            }
        }

        public async Task ResetPreviewAsync()
        {
            _currentlyDisplayedNode = null;
            await SetPreviewerAsync(Previewer.Placeholder);
        }

        // Método centralizado para limpiar el WebView
        private async Task ClearWebViewAsync()
        {
            try
            {
                if (_webView2Preview?.CoreWebView2 != null)
                {
                    // Using about:blank is faster and more reliable than NavigateToString
                    _webView2Preview.CoreWebView2.Navigate("about:blank");
                    // A minimal delay might still be useful for visual consistency
                    await Task.Delay(1);
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
            else if (SupportedFileTypes.Json.Contains(extension) || SupportedFileTypes.JavaScript.Contains(extension) || SupportedFileTypes.Css.Contains(extension) || SupportedFileTypes.Bin.Contains(extension) || SupportedFileTypes.StringTable.Contains(extension) || SupportedFileTypes.PlainText.Contains(extension)) { await ShowAvalonEditTextPreviewAsync(data, extension); }
            else { await ShowUnsupportedPreviewAsync(extension); }
        }

        private async Task ShowAvalonEditTextPreviewAsync(byte[] data, string extension)
        {
            try
            {
                string dataType = extension.TrimStart('.');
                string textContent = await _contentFormatterService.GetFormattedStringAsync(dataType, data);

                IHighlightingDefinition syntaxHighlighting = null;
                if (dataType == "json" || dataType == "bin" || dataType == "css" || dataType == "stringtable" || dataType == "js")
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

        private async Task SetPreviewerAsync(Previewer newPreviewer, object content = null)
        {
            // Step 1: Clean the canvas. Hide all panels and clear their content.
            _imagePreview.Visibility = Visibility.Collapsed;
            _webView2Preview.Visibility = Visibility.Collapsed;
            _textEditorPreview.Visibility = Visibility.Collapsed;
            _previewPlaceholder.Visibility = Visibility.Collapsed;
            _detailsPreview.Visibility = Visibility.Collapsed;

            _imagePreview.Source = null;
            _textEditorPreview.Clear();

            if (_activePreviewer == Previewer.WebView)
            {
                await ClearWebViewAsync();
            }

            _activePreviewer = newPreviewer;

            // Step 2: Assign content and reveal the correct panel.
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
                    if (content is string htmlContent && _webView2Preview.CoreWebView2 != null)
                    {
                        // To prevent white flashes, we make the control visible only after navigation is complete.
                        void OnNavigationCompleted(object sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs args)
                        {
                            _webView2Preview.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
                            
                            // This needs to be run on the UI thread.
                            _webView2Preview.Dispatcher.Invoke(() => 
                            {
                                _webView2Preview.Visibility = Visibility.Visible;
                            });
                        }

                        _webView2Preview.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
                        _webView2Preview.CoreWebView2.NavigateToString(htmlContent);
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
                        if (string.IsNullOrEmpty(extension))
                        {
                            _unsupportedFileMessagePanel.Visibility = Visibility.Collapsed;
                            _extensionlessFilePanel.Visibility = Visibility.Visible;
                        }
                        else
                        {
                            _extensionlessFilePanel.Visibility = Visibility.Collapsed;
                            _unsupportedFileMessagePanel.Visibility = Visibility.Visible;
                            _unsupportedFileTextBlock.Text = $"Preview not available for '{extension}' files.";
                        }
                    }
                    else
                    {
                        // This is for the default "Select a file" message
                        _selectFileMessagePanel.Visibility = Visibility.Visible;
                        _unsupportedFileMessagePanel.Visibility = Visibility.Collapsed;
                        _extensionlessFilePanel.Visibility = Visibility.Collapsed;
                    }
                    break;
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
                var texture = Texture.Load(stream);
                if (texture.Mips.Length > 0)
                {
                    var mainMip = texture.Mips[0];
                    var width = mainMip.Width;
                    var height = mainMip.Height;
                    if (mainMip.Span.TryGetSpan(out Span<ColorRgba32> pixelSpan))
                    {
                        var pixelBytes = MemoryMarshal.AsBytes(pixelSpan).ToArray();
                        for (int i = 0; i < pixelBytes.Length; i += 4)
                        {
                            var r = pixelBytes[i];
                            var b = pixelBytes[i + 2];
                            pixelBytes[i] = b;
                            pixelBytes[i + 2] = r;
                        }
                        var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixelBytes, width * 4);
                        bmp.Freeze();
                        return bmp;
                    }
                }
                return null;
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
            if (_webView2Preview?.CoreWebView2 == null)
            {
                await ShowUnsupportedPreviewAsync(".svg");
                return;
            }

            try
            {
                string svgContent = Encoding.UTF8.GetString(data);
                var htmlContent = $@"<!DOCTYPE html><html><head><meta charset=""UTF-8""/><style>html, body {{background-color: transparent !important;display: flex;justify-content: center;align-items: center;height: 100vh;margin: 0;padding: 20px;box-sizing: border-box;overflow: hidden;}}svg {{width: 90%;height: 90%;object-fit: contain;}}</style></head><body>{svgContent}</body></html>";

                await SetPreviewerAsync(Previewer.WebView, htmlContent);
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
                    <!DOCTYPE html><html><head><meta charset='UTF-8'>
                    <style>
                        html, body {{ background-color: #252526 !important; margin: 0; padding: 0; height: 100vh; display: flex; justify-content: center; align-items: center; overflow: hidden; }}
                        {tag} {{ width: {(tag == "audio" ? "300px" : "auto")}; height: {(tag == "audio" ? "80px" : "auto")}; max-width: 100%; max-height: 100%; background-color: #252526; object-fit: contain; }}
                    </style>
                    </head><body>
                        <{tag} id='mediaElement' controls autoplay {extraAttributes} src='{fileUrl}' type='{mimeType}'>
                            Your browser doesn't support this {(tag == "video" ? "video" : "audio")} format.
                        </{tag}>
                    </body></html>";

                await SetPreviewerAsync(Previewer.WebView, htmlContent);
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

        public async Task ShowPreviewForRealFileWithTemporaryExtension(FileSystemNodeModel node, string tempExtension)
        {
            if (!File.Exists(node.FullPath))
            {
                await ShowUnsupportedPreviewAsync("File not found");
                return;
            }

            byte[] fileData = await File.ReadAllBytesAsync(node.FullPath);
            await DispatchPreview(fileData, tempExtension);
        }

    }
}
