using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AssetsManager.Services.Core;
using AssetsManager.Services.Formatting;
using AssetsManager.Utils;
using AssetsManager.Views.Helpers;
using AssetsManager.Views.Models.Home;
using AssetsManager.Views.Models.Settings;
using Microsoft.Win32;

namespace AssetsManager.Views.Dialogs
{
    public partial class ConverterWindow : HudWindow
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
            if (ViewModel.IsProcessing) return;
            foreach (string file in files)
            {
                if (ViewModel.Items.Any(i => i.FilePath == file)) continue;

                ConverterFileType? type = ConverterModel.GetFileType(file);

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
            else if (tag == "Flac") ViewModel.SelectedAudioFormat = AudioExportFormat.Flac;
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
                var items = ViewModel.Items.ToList();

                try
                {
                    await Task.Run(async () =>
                    {
                        foreach (var item in items)
                        {
                            if (item.Status == "Done") continue;

                            item.Status = "Processing...";
                            item.ErrorMessage = null;
                            try
                            {
                                if (item.FileType == ConverterFileType.Image)
                                {
                                    await ProcessImageItem(item, destinationPath);
                                }
                                else if (item.FileType == ConverterFileType.Video)
                                {
                                    await ProcessVideoItem(item, destinationPath);
                                }
                                else
                                {
                                    await ProcessAudioItem(item, destinationPath);
                                }
                            }
                            catch (Exception ex)
                            {
                                item.Status = "Error";
                                item.ErrorMessage = ex.Message;
                                _logService.LogError(ex, $"Failed to convert '{item.FileName}'.");
                            }
                        }
                    });

                    int failed = items.Count(item => item.Status != "Done");
                    if (failed == 0)
                    {
                        _customMessageBox.ShowSuccess("Conversion Complete", "All files have been converted.", this);
                        _logService.LogInteractiveSuccess($"Universal conversion completed. Files saved in {destinationPath}", destinationPath);
                    }
                    else
                    {
                        _customMessageBox.ShowInfo("Conversion Finished", $"{items.Count - failed} files converted, {failed} failed. Hover over an error status for details.", this);
                        _logService.LogWarning($"Universal conversion finished: {items.Count - failed} converted, {failed} failed.");
                    }
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
                    await TextureUtils.SaveBitmapSourceAsImageAsync(bitmap, item.FileName, destinationPath, ViewModel.SelectedImageFormat, null);
                    item.Status = "Done";
                }
                else
                {
                    item.Status = "Failed";
                }
            }
        }

        private async Task ProcessVideoItem(ConverterItem item, string destinationPath)
        {
            string outputPath = PathUtils.GetUniqueFilePath(destinationPath, Path.ChangeExtension(item.FileName, ".mp4"));
            try
            {
                await WindowsVideoTranscoder.ConvertFileAsync(item.FilePath, outputPath, CancellationToken.None);
                item.Status = "Done";
            }
            catch (Exception ex)
            {
                throw new IOException($"Windows could not convert '{item.FileName}' to MP4. The required media codec may be unavailable. {ex.Message}", ex);
            }
        }

        private async Task ProcessAudioItem(ConverterItem item, string destinationPath)
        {
            byte[] audioData = await File.ReadAllBytesAsync(item.FilePath);
            string inputExt = Path.GetExtension(item.FilePath);
            byte[] convertedData = await _audioConversionService.ConvertAudioToFormatAsync(audioData, inputExt, ViewModel.SelectedAudioFormat);

            if (convertedData != null)
            {
                string extension = ViewModel.SelectedAudioFormat.GetExtension();

                string outFileName = Path.GetFileNameWithoutExtension(item.FileName) + extension;
                string outPath = PathUtils.GetUniqueFilePath(destinationPath, outFileName);
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
