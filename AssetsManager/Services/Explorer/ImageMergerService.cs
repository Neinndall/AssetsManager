using System;
using System.IO;
using System.Linq;
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

namespace AssetsManager.Services.Explorer
{
    public class ImageMergerService
    {
        private readonly IServiceProvider _serviceProvider;
        private ImageMergerWindow _activeWindow;

        public ObservableRangeCollection<ImageMergerItem> Items { get; } = new ObservableRangeCollection<ImageMergerItem>();

        public ImageMergerService(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public void AddItem(ImageMergerItem item)
        {
            if (Items.Any(i => i.Path == item.Path)) return;
            
            Application.Current.Dispatcher.Invoke(() => {
                Items.Add(item);
                ShowWindow();
            });
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
                            bitmap = new BitmapImage(new Uri(filePath));
                        }

                        if (bitmap != null)
                        {
                            Items.Add(new ImageMergerItem
                            {
                                Name = Path.GetFileName(filePath),
                                Path = filePath,
                                Image = bitmap
                            });
                        }
                    }
                    catch { }
                }
            }
        }

        public async Task RenderMergedImageAsync(ImageMergerModel viewModel)
        {
            if (viewModel == null || viewModel.IsProcessing) return;
            if (Items.Count == 0)
            {
                viewModel.PreviewImage = null;
                return;
            }

            viewModel.IsProcessing = true;
            try
            {
                var result = await Task.Run(() => CreateMergedBitmap(viewModel.Columns, viewModel.Margin));
                viewModel.PreviewImage = result;
            }
            finally
            {
                viewModel.IsProcessing = false;
            }
        }

        private BitmapSource CreateMergedBitmap(int columns, int margin)
        {
            try
            {
                var items = Items.ToList();
                if (items.Count == 0) return null;

                int rows = (int)Math.Ceiling((double)items.Count / columns);
                double maxWidth = items.Max(i => i.Image.PixelWidth);
                double maxHeight = items.Max(i => i.Image.PixelHeight);

                int totalWidth = (int)(columns * maxWidth + (columns - 1) * margin);
                int totalHeight = (int)(rows * maxHeight + (rows - 1) * margin);

                DrawingVisual drawingVisual = new DrawingVisual();
                using (DrawingContext drawingContext = drawingVisual.RenderOpen())
                {
                    for (int i = 0; i < items.Count; i++)
                    {
                        int row = i / columns;
                        int col = i % columns;
                        double x = col * (maxWidth + margin);
                        double y = row * (maxHeight + margin);
                        double drawX = x + (maxWidth - items[i].Image.PixelWidth) / 2;
                        double drawY = y + (maxHeight - items[i].Image.PixelHeight) / 2;
                        drawingContext.DrawImage(items[i].Image, new Rect(drawX, drawY, items[i].Image.PixelWidth, items[i].Image.PixelHeight));
                    }
                }

                RenderTargetBitmap rtb = new RenderTargetBitmap(totalWidth, totalHeight, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(drawingVisual);
                rtb.Freeze();
                return rtb;
            }
            catch { return null; }
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
                    using (var fileStream = new FileStream(saveFileDialog.FileName, FileMode.Create))
                    {
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(image));
                        encoder.Save(fileStream);
                    }
                    
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
            if (_activeWindow != null && _activeWindow.IsLoaded)
            {
                _activeWindow.Activate();
                if (_activeWindow.WindowState == WindowState.Minimized)
                    _activeWindow.WindowState = WindowState.Normal;
                return;
            }

            _activeWindow = _serviceProvider.GetRequiredService<ImageMergerWindow>();
            _activeWindow.Owner = Application.Current.MainWindow;
            _activeWindow.Show();
        }

        public void Clear()
        {
            Items.Clear();
        }
    }
}
