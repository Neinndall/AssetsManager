using System;
using System.Linq;
using System.Windows;

using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using System.Threading.Tasks;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using AssetsManager.Services.Explorer;
using AssetsManager.Views.Models.Shared;
using AssetsManager.Services.Core;

namespace AssetsManager.Views.Dialogs
{
    public partial class ImageMergerWindow : Window
    {
        public ImageMergerModel ViewModel { get; set; }
        private readonly CustomMessageBoxService _customMessageBox;
        private readonly ImageMergerService _imageMergerService;

        public ImageMergerWindow(CustomMessageBoxService customMessageBoxService, ImageMergerService imageMergerService)
        {
            InitializeComponent();
            _customMessageBox = customMessageBoxService;
            _imageMergerService = imageMergerService;
            
            ViewModel = new ImageMergerModel(_imageMergerService.Items);
            this.DataContext = ViewModel;

            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
            
            // Suscribirse a la colección del servicio directamente para asegurar que los cambios se detecten siempre
            _imageMergerService.Items.CollectionChanged += (s, e) => RequestRender();
            
            RequestRender();
        }

        private void ViewModel_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ImageMergerModel.Columns) || e.PropertyName == nameof(ImageMergerModel.Margin))
            {
                RequestRender();
            }
        }

        private void RequestRender()
        {
            _ = RenderMergedImageAsync();
        }

        private async Task RenderMergedImageAsync()
        {
            if (ViewModel.IsProcessing) return;
            if (ViewModel.Items.Count == 0)
            {
                ViewModel.PreviewImage = null;
                return;
            }

            ViewModel.IsProcessing = true;

            try
            {
                var result = await Task.Run(() =>
                {
                    return CreateMergedBitmap();
                });

                ViewModel.PreviewImage = result;
            }
            finally
            {
                ViewModel.IsProcessing = false;
            }
        }

        private BitmapSource CreateMergedBitmap()
        {
            try
            {
                var items = ViewModel.Items.ToList();
                int columns = ViewModel.Columns;
                int margin = ViewModel.Margin;

                if (items.Count == 0) return null;

                // 1. Calculate dimensions
                int rows = (int)Math.Ceiling((double)items.Count / columns);
                
                // We use the maximum width/height of items to normalize the grid if needed, 
                // or just follow their natural size. For simplicity and better look, 
                // we'll find the max dimensions to make a uniform grid.
                double maxWidth = items.Max(i => i.Image.PixelWidth);
                double maxHeight = items.Max(i => i.Image.PixelHeight);

                int totalWidth = (int)(columns * maxWidth + (columns - 1) * margin);
                int totalHeight = (int)(rows * maxHeight + (rows - 1) * margin);

                // Use DrawingVisual for high performance rendering
                DrawingVisual drawingVisual = new DrawingVisual();
                using (DrawingContext drawingContext = drawingVisual.RenderOpen())
                {
                    for (int i = 0; i < items.Count; i++)
                    {
                        int row = i / columns;
                        int col = i % columns;

                        double x = col * (maxWidth + margin);
                        double y = row * (maxHeight + margin);

                        // Draw image centered in its cell if smaller than max
                        double drawX = x + (maxWidth - items[i].Image.PixelWidth) / 2;
                        double drawY = y + (maxHeight - items[i].Image.PixelHeight) / 2;

                        drawingContext.DrawImage(items[i].Image, new Rect(drawX, drawY, items[i].Image.PixelWidth, items[i].Image.PixelHeight));
                    }
                }

                // Render to bitmap
                RenderTargetBitmap rtb = new RenderTargetBitmap(totalWidth, totalHeight, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(drawingVisual);
                rtb.Freeze();

                return rtb;
            }
            catch
            {
                return null;
            }
        }

        private void AddImage_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog
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
                                bitmap = Utils.TextureUtils.LoadTexture(stream, System.IO.Path.GetExtension(filePath));
                            }
                        }
                        else
                        {
                            bitmap = new BitmapImage(new Uri(filePath));
                        }

                        if (bitmap != null)
                        {
                            _imageMergerService.Items.Add(new ImageMergerItem
                            {
                                Name = System.IO.Path.GetFileName(filePath),
                                Path = filePath,
                                Image = bitmap
                            });
                        }
                    }
                    catch { }
                }
            }
        }

        private void ClearAll_Click(object sender, RoutedEventArgs e)
        {
            _imageMergerService.Clear();
        }

        private void MoveUp_Click(object sender, RoutedEventArgs e)
        {
            var item = (sender as Button)?.Tag as ImageMergerItem;
            if (item == null) return;

            int index = ViewModel.Items.IndexOf(item);
            if (index > 0)
            {
                _imageMergerService.Items.Move(index, index - 1);
            }
        }

        private void MoveDown_Click(object sender, RoutedEventArgs e)
        {
            var item = (sender as Button)?.Tag as ImageMergerItem;
            if (item == null) return;

            int index = ViewModel.Items.IndexOf(item);
            if (index < ViewModel.Items.Count - 1)
            {
                _imageMergerService.Items.Move(index, index + 1);
            }
        }

        private void Remove_Click(object sender, RoutedEventArgs e)
        {
            var item = (sender as Button)?.Tag as ImageMergerItem;
            if (item != null)
            {
                _imageMergerService.Items.Remove(item);
            }
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.PreviewImage == null) return;

            SaveFileDialog saveFileDialog = new SaveFileDialog
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
                        BitmapEncoder encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(ViewModel.PreviewImage));
                        encoder.Save(fileStream);
                    }
                    _customMessageBox.ShowSuccess("Success", "Image exported successfully!", this);
                }
                catch (Exception ex)
                {
                    _customMessageBox.ShowError("Error", $"Failed to export image: {ex.Message}", this);
                }
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void Maximize_Click(object sender, RoutedEventArgs e)
        {
            if (this.WindowState == WindowState.Maximized)
            {
                this.WindowState = WindowState.Normal;
            }
            else
            {
                this.WindowState = WindowState.Maximized;
            }
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }
    }
}
