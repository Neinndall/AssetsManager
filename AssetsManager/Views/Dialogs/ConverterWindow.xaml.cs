using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AssetsManager.Services.Core;
using AssetsManager.Services.Formatting;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Home;
using AssetsManager.Views.Models.Settings;
using Microsoft.Win32;
using MahApps.Metro.Controls;

namespace AssetsManager.Views.Dialogs
{
    public partial class ConverterWindow : MetroWindow
    {
        public ConverterModel ViewModel { get; set; }
        private readonly CustomMessageBoxService _customMessageBox;
        private readonly AudioConversionService _audioConversionService;
        private readonly LogService _logService;

        public ConverterWindow(CustomMessageBoxService customMessageBoxService, AudioConversionService audioConversionService, LogService logService)
        {
            InitializeComponent();
            _customMessageBox = customMessageBoxService;
            _audioConversionService = audioConversionService;
            _logService = logService;
            ViewModel = new ConverterModel();
            this.DataContext = ViewModel;
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                this.DragMove();
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
        private void Close_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

        private void DropArea_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                DropAreaUI.Stroke = (Brush)FindResource("AccentTeal");
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
                if (ViewModel.Items.Any(i => i.FilePath == file)) continue;

                string ext = Path.GetExtension(file).ToLower();
                ConverterFileType? type = null;

                if (ext == ".tex" || ext == ".dds") type = ConverterFileType.Image;
                else if (ext == ".wem" || ext == ".ogg" || ext == ".mp3" || ext == ".wav") type = ConverterFileType.Audio;

                if (type.HasValue)
                {
                    ViewModel.Items.Add(new ConverterItem
                    {
                        FileName = Path.GetFileName(file),
                        FilePath = file,
                        FileType = type.Value
                    });
                }
            }
        }

        private void RemoveItem_Click(object sender, RoutedEventArgs e)
        {
            var item = (sender as FrameworkElement)?.Tag as ConverterItem;
            if (item != null) ViewModel.Items.Remove(item);
        }

        private void ClearAll_Click(object sender, RoutedEventArgs e) => ViewModel.Items.Clear();

        private void ImageFormat_Checked(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var tag = (sender as FrameworkElement)?.Tag?.ToString();
            if (tag == "Png") ViewModel.SelectedImageFormat = ImageExportFormat.Png;
            else if (tag == "Jpeg") ViewModel.SelectedImageFormat = ImageExportFormat.Jpeg;
        }

        private void AudioFormat_Checked(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var tag = (sender as FrameworkElement)?.Tag?.ToString();
            if (tag == "Ogg") ViewModel.SelectedAudioFormat = AudioExportFormat.Ogg;
            else if (tag == "Wav") ViewModel.SelectedAudioFormat = AudioExportFormat.Wav;
            else if (tag == "Mp3") ViewModel.SelectedAudioFormat = AudioExportFormat.Mp3;
        }

        private async void Convert_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.Items.Count == 0 || ViewModel.IsProcessing) return;

            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select destination folder for converted assets",
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
                                if (item.FileType == ConverterFileType.Image)
                                {
                                    await ProcessImageItem(item, destinationPath);
                                }
                                else
                                {
                                    await ProcessAudioItem(item, destinationPath);
                                }
                            }
                            catch
                            {
                                item.Status = "Error";
                            }
                        }
                    });

                    _customMessageBox.ShowSuccess("Conversion Complete", "All files have been processed.", this);
                    _logService.LogInteractiveSuccess($"Universal conversion completed. Files saved in {destinationPath}", destinationPath);
                }
                finally
                {
                    ViewModel.IsProcessing = false;
                }
            }
        }

        private async Task ProcessImageItem(ConverterItem item, string destinationPath)
        {
            using (var stream = new FileStream(item.FilePath, FileMode.Open, FileAccess.Read))
            {
                var bitmap = TextureUtils.LoadTexture(stream, Path.GetExtension(item.FilePath));
                if (bitmap != null)
                {
                    TextureUtils.SaveBitmapSourceAsImage(bitmap, item.FileName, destinationPath, ViewModel.SelectedImageFormat, null);
                    item.Status = "Done";
                }
                else
                {
                    item.Status = "Failed";
                }
            }
        }

        private async Task ProcessAudioItem(ConverterItem item, string destinationPath)
        {
            byte[] audioData = await File.ReadAllBytesAsync(item.FilePath);
            string inputExt = Path.GetExtension(item.FilePath);
            byte[] convertedData = await _audioConversionService.ConvertAudioToFormatAsync(audioData, inputExt, ViewModel.SelectedAudioFormat);

            if (convertedData != null)
            {
                string extension = ViewModel.SelectedAudioFormat switch
                {
                    AudioExportFormat.Wav => ".wav",
                    AudioExportFormat.Mp3 => ".mp3",
                    _ => ".ogg"
                };

                string outFileName = Path.GetFileNameWithoutExtension(item.FileName) + extension;
                string outPath = Path.Combine(destinationPath, outFileName);
                await File.WriteAllBytesAsync(outPath, convertedData);
                item.Status = "Done";
            }
            else
            {
                item.Status = "Failed";
            }
        }
    }
}
