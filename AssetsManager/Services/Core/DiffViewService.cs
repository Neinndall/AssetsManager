using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AssetsManager.Services.Comparator;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using AssetsManager.Views.Models;
using AssetsManager.Views.Dialogs;
using AssetsManager.Views.Helpers;

namespace AssetsManager.Services.Core
{
    public class DiffViewService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly WadDifferenceService _wadDifferenceService;
        private readonly CustomMessageBoxService _customMessageBoxService;
        private readonly LogService _logService;
        private readonly JsBeautifierService _jsBeautifierService;
        private readonly CSSParserService _cssParserService;

        private static readonly string[] SupportedImageExtensions = { ".png", ".dds", ".tga", ".jpg", ".jpeg", ".bmp", ".gif", ".ico", ".webp", ".tex" };
        private static readonly string[] SupportedTextExtensions = { ".css", ".json", ".js", ".txt", ".xml", ".yaml", ".html", ".ini", ".log" };

        public DiffViewService(IServiceProvider serviceProvider, WadDifferenceService wadDifferenceService, CustomMessageBoxService customMessageBoxService, LogService logService, JsBeautifierService jsBeautifierService, CSSParserService cssParserService)
        {
            _serviceProvider = serviceProvider;
            _wadDifferenceService = wadDifferenceService;
            _customMessageBoxService = customMessageBoxService;
            _logService = logService;
            _jsBeautifierService = jsBeautifierService;
            _cssParserService = cssParserService;
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
            if (SupportedImageExtensions.Contains(extension))
            {
                await HandleImageDiffAsync(diff, oldPbePath, newPbePath, owner);
                return;
            }

            try
            {
                var diffWindow = _serviceProvider.GetRequiredService<JsonDiffWindow>();
                diffWindow.Owner = owner;
                diffWindow.ShowLoading(true);
                diffWindow.Show();

                var (dataType, oldData, newData, oldPath, newPath) = await _wadDifferenceService.PrepareDifferenceDataAsync(diff, oldPbePath, newPbePath);
                var (oldText, newText) = await ProcessDataAsync(dataType, oldData, newData);

                if (oldText == newText)
                {
                    diffWindow.Close();
                    _customMessageBoxService.ShowInfo("Info", "No differences found. The two files are identical.", owner);
                    return;
                }

                await diffWindow.LoadAndDisplayDiffAsync(oldText, newText, oldPath, newPath);
            }
            catch (Exception ex)
            {
                _customMessageBoxService.ShowError("Comparison Error", $"An unexpected error occurred while preparing the file for comparison. Details: {ex.Message}", owner);
                _logService.LogError(ex, "Error showing WAD diff");
                throw;
            }
        }
        
        private async Task HandleImageDiffAsync(SerializableChunkDiff diff, string oldPbePath, string newPbePath, System.Windows.Window owner)
        {
            try
            {
                var (dataType, oldData, newData, oldPath, newPath) = await _wadDifferenceService.PrepareDifferenceDataAsync(diff, oldPbePath, newPbePath);
                if (dataType == "image")
                {
                    var imageDiffWindow = new ImageDiffWindow((BitmapSource)oldData, (BitmapSource)newData, oldPath, newPath) { Owner = owner };
                    imageDiffWindow.Show();
                }
                else
                {
                    _customMessageBoxService.ShowError("Error", "Expected an image but received a different file type.", owner);
                }
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
            if (SupportedImageExtensions.Contains(extension))
            {
                _customMessageBoxService.ShowInfo("Info", "Image comparison for local files is not implemented yet.", owner);
                return;
            }

            try
            {
                var diffWindow = _serviceProvider.GetRequiredService<JsonDiffWindow>();
                diffWindow.Owner = owner;
                diffWindow.ShowLoading(true);
                diffWindow.Show();

                var (dataType, oldData, newData) = await _wadDifferenceService.PrepareFileDifferenceDataAsync(oldFilePath, newFilePath);
                var (oldText, newText) = await ProcessDataAsync(dataType, oldData, newData);

                if (oldText == newText)
                {
                    diffWindow.Close();
                    _customMessageBoxService.ShowInfo("Info", "No differences found. The two files are identical.", owner);
                    return;
                }
                
                await diffWindow.LoadAndDisplayDiffAsync(oldText, newText, Path.GetFileName(oldFilePath), Path.GetFileName(newFilePath));
            }
            catch (Exception ex)
            {
                _customMessageBoxService.ShowError("Comparison Error", $"An unexpected error occurred while preparing the file for comparison. Details: {ex.Message}", owner);
                _logService.LogError(ex, "Error showing file diff");
                throw;
            }
        }

        private async Task<(string oldText, string newText)> ProcessDataAsync(string dataType, object oldData, object newData)
        {
            string oldText = string.Empty;
            string newText = string.Empty;

            switch (dataType)
            {
                case "bin":
                    if (oldData != null) oldText = await JsonDiffHelper.FormatJsonAsync(oldData);
                    if (newData != null) newText = await JsonDiffHelper.FormatJsonAsync(newData);
                    break;
                case "js":
                    try
                    {
                        if (oldData != null) oldText = await _jsBeautifierService.BeautifyAsync((string)oldData);
                        if (newData != null) newText = await _jsBeautifierService.BeautifyAsync((string)newData);
                    }
                    catch (Exception ex)
                    {
                        _logService.LogWarning($"JS Beautifier failed: {ex.Message}");
                        oldText = (string)oldData ?? string.Empty;
                        newText = (string)newData ?? string.Empty;
                    }
                    break;
                case "json":
                    if (oldData != null) oldText = await JsonDiffHelper.FormatJsonAsync(oldData);
                    if (newData != null) newText = await JsonDiffHelper.FormatJsonAsync(newData);
                    break;
                case "css":
                    if (oldData != null) oldText = _cssParserService.ConvertToJson((string)oldData);
                    if (newData != null) newText = _cssParserService.ConvertToJson((string)newData);
                    break;
                case "text":
                    oldText = (string)oldData ?? string.Empty;
                    newText = (string)newData ?? string.Empty;
                    break;
                default:
                    oldText = oldData?.ToString() ?? string.Empty;
                    newText = newData?.ToString() ?? string.Empty;
                    break;
            }
            return (oldText, newText);
        }

        private bool IsDiffSupported(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return false;

            string extension = Path.GetExtension(filePath).ToLowerInvariant();

            if (SupportedImageExtensions.Contains(extension)) return true;
            if (SupportedTextExtensions.Contains(extension)) return true;
            if (extension == ".bin") return true;

            return false;
        }
    }
}
