using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Home;
using AssetsManager.Views.Models.Settings;
using Microsoft.Win32;

namespace AssetsManager.Views.Dialogs
{
    public partial class TextureConverterWindow : Window
    {
        public TextureConverterModel ViewModel { get; set; }
        private readonly CustomMessageBoxService _customMessageBox;

        public TextureConverterWindow(CustomMessageBoxService customMessageBoxService)
        {
            InitializeComponent();
            _customMessageBox = customMessageBoxService;
            ViewModel = new TextureConverterModel();
            this.DataContext = ViewModel;
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                this.DragMove();
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => this.WindowState = WindowState.Minimized;
        private void Close_Click(object sender, RoutedEventArgs e) => this.Close();

        private void DropArea_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                DropAreaUI.Stroke = (Brush)FindResource("AccentBrush");
                DropAreaUI.Fill = (Brush)FindResource("HoverColor");
            }
        }

        private void DropArea_DragLeave(object sender, DragEventArgs e)
        {
            DropAreaUI.Stroke = (Brush)FindResource("BorderColor");
            DropAreaUI.Fill = (Brush)FindResource("SidebarBackground");
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            DropAreaUI.Stroke = (Brush)FindResource("BorderColor");
            DropAreaUI.Fill = (Brush)FindResource("SidebarBackground");

            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                ProcessFiles(files);
            }
        }

        protected override void OnPreviewDragOver(DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            }
            base.OnPreviewDragOver(e);
        }

        private void ProcessFiles(string[] files)
        {
            foreach (string file in files)
            {
                string ext = Path.GetExtension(file).ToLower();
                if (ext == ".tex" || ext == ".dds")
                {
                    if (!ViewModel.Items.Any(i => i.FilePath == file))
                    {
                        ViewModel.Items.Add(new TextureConverterItem
                        {
                            FileName = Path.GetFileName(file),
                            FilePath = file
                        });
                    }
                }
            }
        }

        private void RemoveItem_Click(object sender, RoutedEventArgs e)
        {
            var item = (sender as FrameworkElement)?.Tag as TextureConverterItem;
            if (item != null) ViewModel.Items.Remove(item);
        }

        private void ClearAll_Click(object sender, RoutedEventArgs e) => ViewModel.Items.Clear();

        private void Format_Checked(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var tag = (sender as FrameworkElement)?.Tag?.ToString();
            if (tag == "Png") ViewModel.SelectedFormat = ImageExportFormat.Png;
            else if (tag == "Jpeg") ViewModel.SelectedFormat = ImageExportFormat.Jpeg;
        }

        private async void Convert_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.Items.Count == 0 || ViewModel.IsProcessing) return;

            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select destination folder for converted textures",
                UseDescriptionForTitle = true
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                string destinationPath = dialog.SelectedPath;
                ViewModel.IsProcessing = true;

                try
                {
                    await Task.Run(async () =>
                    {
                        foreach (var item in ViewModel.Items.ToList())
                        {
                            if (item.Status == "Done") continue;

                            item.Status = "Processing...";
                            try
                            {
                                using (var stream = new FileStream(item.FilePath, FileMode.Open, FileAccess.Read))
                                {
                                    var bitmap = TextureUtils.LoadTexture(stream, Path.GetExtension(item.FilePath));
                                    if (bitmap != null)
                                    {
                                        TextureUtils.SaveBitmapSourceAsImage(bitmap, item.FileName, destinationPath, ViewModel.SelectedFormat, null);
                                        item.Status = "Done";
                                    }
                                    else
                                    {
                                        item.Status = "Failed";
                                    }
                                }
                            }
                            catch
                            {
                                item.Status = "Error";
                            }
                        }
                    });

                    _customMessageBox.ShowSuccess("Conversion Complete", "All textures have been processed.", this);
                }
                finally
                {
                    ViewModel.IsProcessing = false;
                }
            }
        }
    }
}
