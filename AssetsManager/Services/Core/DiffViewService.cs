using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Hashes;
using AssetsManager.Utils;
using BCnEncoder.Shared;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Renderer;
using Microsoft.Extensions.DependencyInjection;
using AssetsManager.Services.Comparator;
using AssetsManager.Views.Models;
using AssetsManager.Views.Dialogs;
using AssetsManager.Views.Helpers;
using AssetsManager.Services.Formatting;

namespace AssetsManager.Services.Core
{
    public class DiffViewService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly WadDifferenceService _wadDifferenceService;
        private readonly CustomMessageBoxService _customMessageBoxService;
        private readonly LogService _logService;
        private readonly ContentFormatterService _contentFormatterService;

        public DiffViewService(IServiceProvider serviceProvider, WadDifferenceService wadDifferenceService, CustomMessageBoxService customMessageBoxService, LogService logService, ContentFormatterService contentFormatterService)
        {
            _serviceProvider = serviceProvider;
            _wadDifferenceService = wadDifferenceService;
            _customMessageBoxService = customMessageBoxService;
            _logService = logService;
            _contentFormatterService = contentFormatterService;
        }

        public async Task ShowWadDiffAsync(SerializableChunkDiff diff, string oldPbePath, string newPbePath, System.Windows.Window owner)
        {
            if (diff == null) return;

            var pathForCheck = diff.NewPath ?? diff.OldPath;
            if (!IsDiffSupported(pathForCheck))
            {
                _customMessageBoxService.ShowInfo("Info", "This file type cannot be displayed in the difference viewer.", owner);
                return;
            }

            string extension = Path.GetExtension(pathForCheck).ToLowerInvariant();
            if (SupportedFileTypes.Images.Contains(extension) || SupportedFileTypes.Textures.Contains(extension))
            {
                await HandleImageDiffAsync(diff, oldPbePath, newPbePath, owner, extension);
                return;
            }

            var loadingWindow = new LoadingDiffWindow { Owner = owner };
            loadingWindow.Show();

            try
            {
                var (dataType, oldData, newData, oldPath, newPath) = await _wadDifferenceService.PrepareDifferenceDataAsync(diff, oldPbePath, newPbePath);
                var (oldText, newText) = await ProcessDataAsync(dataType, (byte[])oldData, (byte[])newData);

                if (oldText == newText)
                {
                    loadingWindow.Close();
                    _customMessageBoxService.ShowInfo("Info", "No differences found. The two files are identical.", owner);
                    return;
                }

                var diffWindow = _serviceProvider.GetRequiredService<JsonDiffWindow>();
                diffWindow.Owner = owner;
                await diffWindow.LoadAndDisplayDiffAsync(oldText, newText, oldPath, newPath);
                
                loadingWindow.Close();
                diffWindow.ShowDialog();
            }
            catch (Exception ex)
            {
                loadingWindow.Close();
                _customMessageBoxService.ShowError("Comparison Error", $"An unexpected error occurred while preparing the file for comparison. Details: {ex.Message}", owner);
                _logService.LogError(ex, "Error showing WAD diff");
            }
        }

        private async Task HandleImageDiffAsync(SerializableChunkDiff diff, string oldPbePath, string newPbePath, System.Windows.Window owner, string extension)
        {
            try
            {
                var (dataType, oldData, newData, oldPath, newPath) = await _wadDifferenceService.PrepareDifferenceDataAsync(diff, oldPbePath, newPbePath);
                
                var oldImage = ToBitmapSource((byte[])oldData, extension);
                var newImage = ToBitmapSource((byte[])newData, extension);

                var imageDiffWindow = new ImageDiffWindow(oldImage, newImage, oldPath, newPath) { Owner = owner };
                imageDiffWindow.Show();
            }
            catch (Exception ex)
            {
                _customMessageBoxService.ShowError("Image Comparison Error", $"An unexpected error occurred while preparing the image for comparison. Details: {ex.Message}", owner);
            }
        }

        public async Task ShowFileDiffAsync(string oldFilePath, string newFilePath, System.Windows.Window owner)
        {
            if (!File.Exists(oldFilePath) && !File.Exists(newFilePath))
            {
                _customMessageBoxService.ShowError("Error", "Neither of the files to compare exist.", owner);
                return;
            }

            string extension = Path.GetExtension(newFilePath ?? oldFilePath).ToLowerInvariant();
            if (SupportedFileTypes.Images.Contains(extension) || SupportedFileTypes.Textures.Contains(extension))
            {
                _customMessageBoxService.ShowInfo("Info", "Image comparison for local files is not implemented yet.", owner);
                return;
            }

            var loadingWindow = new LoadingDiffWindow { Owner = owner };
            loadingWindow.Show();

            try
            {
                var (dataType, oldData, newData) = await _wadDifferenceService.PrepareFileDifferenceDataAsync(oldFilePath, newFilePath);
                var (oldText, newText) = await ProcessDataAsync(dataType, (byte[])oldData, (byte[])newData);

                if (oldText == newText)
                {
                    loadingWindow.Close();
                    _customMessageBoxService.ShowInfo("Info", "No differences found. The two files are identical.", owner);
                    return;
                }

                var diffWindow = _serviceProvider.GetRequiredService<JsonDiffWindow>();
                diffWindow.Owner = owner;
                await diffWindow.LoadAndDisplayDiffAsync(oldText, newText, Path.GetFileName(oldFilePath), Path.GetFileName(newFilePath));

                loadingWindow.Close();
                diffWindow.ShowDialog();
            }
            catch (Exception ex)
            {
                loadingWindow.Close();
                _customMessageBoxService.ShowError("Comparison Error", $"An unexpected error occurred while preparing the file for comparison. Details: {ex.Message}", owner);
                _logService.LogError(ex, "Error showing file diff");
            }
        }
        
        private async Task<(string oldText, string newText)> ProcessDataAsync(string dataType, byte[] oldData, byte[] newData)
        {
            var oldTextTask = _contentFormatterService.GetFormattedStringAsync(dataType, oldData);
            var newTextTask = _contentFormatterService.GetFormattedStringAsync(dataType, newData);

            await Task.WhenAll(oldTextTask, newTextTask);

            return (await oldTextTask, await newTextTask);
        }

        private bool IsDiffSupported(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return false;

            string extension = Path.GetExtension(filePath).ToLowerInvariant();

            return SupportedFileTypes.Images.Contains(extension) ||
                   SupportedFileTypes.Textures.Contains(extension) ||
                   SupportedFileTypes.Json.Contains(extension) ||
                   SupportedFileTypes.JavaScript.Contains(extension) ||
                   SupportedFileTypes.Css.Contains(extension) ||
                   SupportedFileTypes.Bin.Contains(extension) ||
                   SupportedFileTypes.StringTable.Contains(extension) ||
                   SupportedFileTypes.PlainText.Contains(extension);
        }

        private BitmapSource ToBitmapSource(byte[] data, string extension)
        {
            if (data == null || data.Length == 0) return null;

            using (var stream = new MemoryStream(data))
            {
                if (SupportedFileTypes.Textures.Contains(extension))
                {
                    return TextureUtils.LoadTexture(stream, extension);
                }
                else
                {
                    var bitmapImage = new BitmapImage();
                    bitmapImage.BeginInit();
                    bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                    bitmapImage.StreamSource = stream;
                    bitmapImage.EndInit();
                    bitmapImage.Freeze();
                    return bitmapImage;
                }
            }
        }
    }
}