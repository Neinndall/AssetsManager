using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Microsoft.Extensions.DependencyInjection;
using AssetsManager.Views.Models.Dialogs;
using AssetsManager.Views.Dialogs;
using AssetsManager.Utils;
using AssetsManager.Utils.Framework;
using AssetsManager.Services.Core;
using AssetsManager.Views.Models.Explorer;

namespace AssetsManager.Services.Explorer
{
    public class ImageMergerService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly LogService _logService;
        private readonly WadContentProvider _wadContentProvider;
        private ImageMergerWindow _activeWindow;
        private readonly object _renderLock = new();
        private CancellationTokenSource _renderCancellation;
        private int _renderGeneration;

        public ObservableRangeCollection<ImageMergerItem> Items { get; } = new ObservableRangeCollection<ImageMergerItem>();

        public ImageMergerService(IServiceProvider serviceProvider, LogService logService, WadContentProvider wadContentProvider)
        {
            _serviceProvider = serviceProvider;
            _logService = logService;
            _wadContentProvider = wadContentProvider;
        }

        public void AddItem(ImageMergerItem item)
        {
            if (item?.Image != null && item.Image.CanFreeze && !item.Image.IsFrozen)
            {
                item.Image.Freeze();
            }

            Application.Current.Dispatcher.InvokeAsync(() => {
                if (!Items.Any(i => i.Path == item.Path))
                {
                    Items.Add(item);
                }
                
                // Always show the window even if the item was already present
                ShowWindow();
            });
        }

        public async Task<bool> AddNodeAsync(FileSystemNodeModel node)
        {
            if (!(SupportedFileTypes.Images.Contains(node.Extension) || SupportedFileTypes.Textures.Contains(node.Extension)) ||
                !(node.Type == NodeType.VirtualFile || node.Type == NodeType.RealFile))
                return false;

            try
            {
                byte[] data = null;
                if (node.Type == NodeType.VirtualFile)
                    data = await _wadContentProvider.GetVirtualFileBytesAsync(node);
                else if (node.Type == NodeType.RealFile)
                    data = await File.ReadAllBytesAsync(node.VirtualPath);

                if (data == null) return false;

                BitmapSource bitmap = null;
                if (SupportedFileTypes.Textures.Contains(node.Extension))
                {
                    using (var stream = new MemoryStream(data))
                        bitmap = TextureUtils.LoadTexture(stream, node.Extension);
                }
                else
                {
                    using (var stream = new MemoryStream(data))
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.StreamSource = stream;
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.EndInit();
                        bmp.Freeze();
                        bitmap = bmp;
                    }
                }

                if (bitmap != null)
                {
                    AddItem(new ImageMergerItem
                    {
                        Name = node.Name,
                        Path = node.VirtualPath ?? node.Name,
                        Image = bitmap
                    });
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Failed to add image '{node.Name}' to merger.");
            }

            return false;
        }

        public async Task AddImagesFromDialogAsync()
        {
            var openFileDialog = new OpenFileDialog
            {
                Multiselect = true,
                Filter = "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.tex;*.dds"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                foreach (string filePath in openFileDialog.FileNames)
                {
                    try
                    {
                        BitmapSource bitmap = null;
                        if (filePath.EndsWith(".tex") || filePath.EndsWith(".dds"))
                        {
                            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                            {
                                bitmap = TextureUtils.LoadTexture(stream, Path.GetExtension(filePath));
                            }
                        }
                        else
                        {
                            var bmp = new BitmapImage();
                            bmp.BeginInit();
                            bmp.UriSource = new Uri(filePath);
                            bmp.CacheOption = BitmapCacheOption.OnLoad;
                            bmp.EndInit();
                            bmp.Freeze();
                            bitmap = bmp;
                        }

                        if (bitmap != null)
                        {
                            if (!Items.Any(i => i.Path == filePath))
                            {
                                Items.Add(new ImageMergerItem
                                {
                                    Name = Path.GetFileName(filePath),
                                    Path = filePath,
                                    Image = bitmap
                                });
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logService.LogError(ex, $"Failed to add image to merger from file: {filePath}");
                    }
                }
            }
        }

        public async Task RenderMergedImageAsync(ImageMergerModel viewModel)
        {
            if (viewModel == null) return;

            CancellationTokenSource currentCancellation;
            CancellationTokenSource previousCancellation;
            int generation;

            lock (_renderLock)
            {
                previousCancellation = _renderCancellation;
                currentCancellation = new CancellationTokenSource();
                _renderCancellation = currentCancellation;
                generation = ++_renderGeneration;
            }

            previousCancellation?.Cancel();

            if (Items.Count == 0)
            {
                viewModel.PreviewImage = null;
                CompleteRender(viewModel, currentCancellation, generation);
                return;
            }

            viewModel.IsProcessing = true;
            try
            {
                var items = Items.ToList();
                var result = await Task.Run(
                    () => CreateMergedBitmap(items, viewModel.Columns, viewModel.Margin, currentCancellation.Token),
                    currentCancellation.Token);

                if (!currentCancellation.IsCancellationRequested && IsCurrentRender(currentCancellation, generation))
                {
                    viewModel.PreviewImage = result;
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when the image grid changes before rendering completes.
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to render merged image.");
            }
            finally
            {
                CompleteRender(viewModel, currentCancellation, generation);
            }
        }

        private bool IsCurrentRender(CancellationTokenSource cancellation, int generation)
        {
            lock (_renderLock)
            {
                return ReferenceEquals(_renderCancellation, cancellation) && _renderGeneration == generation;
            }
        }

        private void CompleteRender(ImageMergerModel viewModel, CancellationTokenSource cancellation, int generation)
        {
            if (IsCurrentRender(cancellation, generation))
            {
                lock (_renderLock)
                {
                    _renderCancellation = null;
                }

                viewModel.IsProcessing = false;
            }

            cancellation.Dispose();
        }

        private static BitmapSource CreateMergedBitmap(IReadOnlyList<ImageMergerItem> items, int columns, int margin, CancellationToken cancellationToken)
        {
            columns = Math.Max(1, columns);
            margin = Math.Max(0, margin);

            var validItems = items.Where(item => item.Image != null).ToList();
            if (validItems.Count == 0) return null;

            int rows = (int)Math.Ceiling((double)validItems.Count / columns);
            double maxWidth = validItems.Max(item => item.Image.PixelWidth);
            double maxHeight = validItems.Max(item => item.Image.PixelHeight);
            int totalWidth = (int)(columns * maxWidth + (columns - 1) * margin);
            int totalHeight = (int)(rows * maxHeight + (rows - 1) * margin);

            cancellationToken.ThrowIfCancellationRequested();
            var drawingVisual = new DrawingVisual();
            using (DrawingContext drawingContext = drawingVisual.RenderOpen())
            {
                for (int i = 0; i < validItems.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int row = i / columns;
                    int col = i % columns;
                    double x = col * (maxWidth + margin);
                    double y = row * (maxHeight + margin);
                    double width = validItems[i].Image.PixelWidth;
                    double height = validItems[i].Image.PixelHeight;
                    double drawX = x + (maxWidth - width) / 2;
                    double drawY = y + (maxHeight - height) / 2;
                    drawingContext.DrawImage(validItems[i].Image, new Rect(drawX, drawY, width, height));
                }
            }

            var bitmap = new RenderTargetBitmap(totalWidth, totalHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(drawingVisual);
            bitmap.Freeze();
            return bitmap;
        }

        public async Task ExportImageAsync(BitmapSource image, Window owner)
        {
            if (image == null) return;

            var saveFileDialog = new SaveFileDialog
            {
                Filter = "PNG Image|*.png",
                FileName = "MergedImage.png"
            };

            if (saveFileDialog.ShowDialog() == true)
            {
                try
                {
                    await ImageExportUtils.SaveBitmapAsPngAsync(image, saveFileDialog.FileName);
                    
                    var logService = _serviceProvider.GetRequiredService<LogService>();
                    var customMessageBox = _serviceProvider.GetRequiredService<CustomMessageBoxService>();
                    
                    customMessageBox.ShowSuccess("Success", "Image exported successfully!", owner);
                    logService.LogInteractiveSuccess("Image exported successfully to", saveFileDialog.FileName, Path.GetFileName(saveFileDialog.FileName));
                }
                catch (Exception ex)
                {
                    var customMessageBox = _serviceProvider.GetRequiredService<CustomMessageBoxService>();
                    customMessageBox.ShowError("Error", $"Failed to export image: {ex.Message}", owner);
                }
            }
        }

        public void ShowWindow()
        {
            Application.Current.Dispatcher.InvokeAsync(() => {
                if (_activeWindow != null && _activeWindow.IsLoaded)
                {
                    // Ensure the window is visible and focused
                    _activeWindow.Show();
                    
                    if (_activeWindow.WindowState == WindowState.Minimized)
                        _activeWindow.WindowState = WindowState.Normal;

                    _activeWindow.Activate();
                    _activeWindow.Focus();
                    return;
                }

                // If window doesn't exist or was destroyed, create a fresh one
                _activeWindow = _serviceProvider.GetRequiredService<ImageMergerWindow>();
                _activeWindow.Owner = Application.Current.MainWindow;
                _activeWindow.Show();
                _activeWindow.Activate();
            });
        }

        public void Clear()
        {
            Items.Clear();
        }
    }
}
