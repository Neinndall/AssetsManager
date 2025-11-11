using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Audio;
using AssetsManager.Services.Comparator;
using AssetsManager.Services.Explorer;
using AssetsManager.Services.Formatting;
using AssetsManager.Utils;
using AssetsManager.Views.Dialogs;
using AssetsManager.Views.Models;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using AssetsManager.Views.Helpers; // Added this line

namespace AssetsManager.Services.Core
{
    public class DiffViewService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly WadDifferenceService _wadDifferenceService;
        private readonly CustomMessageBoxService _customMessageBoxService;
        private readonly LogService _logService;
        private readonly ContentFormatterService _contentFormatterService;
        private readonly AudioBankLinkerService _audioBankLinkerService;
        private readonly AudioBankService _audioBankService;
        private readonly WadExtractionService _wadExtractionService;

        public DiffViewService(IServiceProvider serviceProvider, WadDifferenceService wadDifferenceService, CustomMessageBoxService customMessageBoxService, LogService logService, ContentFormatterService contentFormatterService, AudioBankLinkerService audioBankLinkerService, AudioBankService audioBankService, WadExtractionService wadExtractionService)
        {
            _serviceProvider = serviceProvider;
            _wadDifferenceService = wadDifferenceService;
            _customMessageBoxService = customMessageBoxService;
            _logService = logService;
            _contentFormatterService = contentFormatterService;
            _audioBankLinkerService = audioBankLinkerService;
            _audioBankService = audioBankService;
            _wadExtractionService = wadExtractionService;
        }

        public async Task ShowWadDiffAsync(SerializableChunkDiff diff, string oldPbePath, string newPbePath, Window owner)
        {
            if (diff == null) return;

            var pathForCheck = diff.NewPath ?? diff.OldPath;
            if (!IsDiffSupported(pathForCheck))
            {
                _customMessageBoxService.ShowInfo("Info", "This file type cannot be displayed in the difference viewer.", owner);
                return;
            }

            string extension = Path.GetExtension(pathForCheck).ToLowerInvariant();

            var loadingWindow = new LoadingDiffWindow { Owner = owner };
            loadingWindow.Show();

            try
            {
                if (SupportedFileTypes.AudioBank.Contains(extension))
                {
                    await HandleAudioBankDiffAsync(diff, oldPbePath, newPbePath, owner);
                }
                else if (SupportedFileTypes.Images.Contains(extension) || SupportedFileTypes.Textures.Contains(extension))
                {
                    await HandleImageDiffAsync(diff, oldPbePath, newPbePath, owner, extension);
                }
                else
                {
                    await HandleTextDiffAsync(diff, oldPbePath, newPbePath, owner);
                }
            }
            catch (Exception ex)
            {
                _customMessageBoxService.ShowError("Comparison Error", $"An unexpected error occurred while preparing the file for comparison. Details: {ex.Message}", owner);
                _logService.LogError(ex, "Error showing WAD diff");
            }
            finally
            {
                loadingWindow.Close();
            }
        }

        private async Task HandleAudioBankDiffAsync(SerializableChunkDiff diff, string oldPbePath, string newPbePath, Window owner)
        {
            string oldJson = "{}";
            string newJson = "{}";

            if (string.IsNullOrEmpty(diff.SourceWadFile))
            {
                _logService.LogWarning("[AUDIO-DIFF] SerializableChunkDiff.SourceWadFile is null. Audio event name resolution will likely fail.");
            }

            if (diff.Type is ChunkDiffType.Modified or ChunkDiffType.Renamed or ChunkDiffType.Removed)
            {
                var tempNodeOld = new FileSystemNodeModel { Name = Path.GetFileName(diff.OldPath), FullPath = diff.OldPath, SourceWadPath = diff.SourceWadFile, ChunkDiff = diff };
                var linkedBankOld = await _audioBankLinkerService.LinkAudioBankForDiffAsync(tempNodeOld, oldPbePath);
                if (linkedBankOld != null)
                {
                    oldJson = await AudioBankToStringAsync(linkedBankOld);
                }
                else
                {
                    _logService.LogWarning("[AUDIO-DIFF] Failed to link old version.");
                }
            }

            if (diff.Type is ChunkDiffType.Modified or ChunkDiffType.Renamed or ChunkDiffType.New)
            {
                var tempNodeNew = new FileSystemNodeModel { Name = Path.GetFileName(diff.NewPath), FullPath = diff.NewPath, SourceWadPath = diff.SourceWadFile, ChunkDiff = diff };
                var linkedBankNew = await _audioBankLinkerService.LinkAudioBankForDiffAsync(tempNodeNew, newPbePath);
                if (linkedBankNew != null)
                {
                    newJson = await AudioBankToStringAsync(linkedBankNew);
                }
                else
                {
                    _logService.LogWarning("[AUDIO-DIFF] Failed to link new version.");
                }
            }

            if (oldJson == newJson)
            {
                _customMessageBoxService.ShowInfo("Info", "No differences found. The two files are identical.", owner);
                return;
            }

            var diffWindow = _serviceProvider.GetRequiredService<JsonDiffWindow>();
            diffWindow.Owner = owner;
            await diffWindow.LoadAndDisplayDiffAsync(oldJson, newJson, diff.OldPath, diff.NewPath);
            diffWindow.ShowDialog();
        }

        private async Task<string> AudioBankToStringAsync(LinkedAudioBank linkedBank)
        {
            if (linkedBank == null) return "{}";

            var wpkData = linkedBank.WpkNode != null ? await _wadExtractionService.GetVirtualFileBytesAsync(linkedBank.WpkNode) : null;
            var audioBnkData = linkedBank.AudioBnkNode != null ? await _wadExtractionService.GetVirtualFileBytesAsync(linkedBank.AudioBnkNode) : null;
            var eventsBnkData = linkedBank.EventsBnkNode != null ? await _wadExtractionService.GetVirtualFileBytesAsync(linkedBank.EventsBnkNode) : null;

            List<AudioEventNode> result;
            if (linkedBank.BinData != null)
            {
                result = _audioBankService.ParseAudioBank(wpkData, audioBnkData, eventsBnkData, linkedBank.BinData, linkedBank.BaseName, linkedBank.BinType);
            }
            else
            {
                result = _audioBankService.ParseGenericAudioBank(wpkData, audioBnkData, eventsBnkData);
            }

            var settings = new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore };
            return await JsonFormatter.FormatJsonAsync(result, settings);
        }

        private async Task HandleTextDiffAsync(SerializableChunkDiff diff, string oldPbePath, string newPbePath, Window owner)
        {
            var (dataType, oldData, newData, oldPath, newPath) = await _wadDifferenceService.PrepareDifferenceDataAsync(diff, oldPbePath, newPbePath);
            var (oldText, newText) = await ProcessDataAsync(dataType, (byte[])oldData, (byte[])newData);

            if (oldText == newText)
            {
                _customMessageBoxService.ShowInfo("Info", "No differences found. The two files are identical.", owner);
                return;
            }

            var diffWindow = _serviceProvider.GetRequiredService<JsonDiffWindow>();
            diffWindow.Owner = owner;
            await diffWindow.LoadAndDisplayDiffAsync(oldText, newText, oldPath, newPath);
            diffWindow.ShowDialog();
        }

        private async Task HandleImageDiffAsync(SerializableChunkDiff diff, string oldPbePath, string newPbePath, Window owner, string extension)
        {
            var (dataType, oldData, newData, oldPath, newPath) = await _wadDifferenceService.PrepareDifferenceDataAsync(diff, oldPbePath, newPbePath);

            var oldImage = ToBitmapSource((byte[])oldData, extension);
            var newImage = ToBitmapSource((byte[])newData, extension);

            var imageDiffWindow = new ImageDiffWindow(oldImage, newImage, oldPath, newPath) { Owner = owner };
            imageDiffWindow.Show();
        }

        public async Task ShowFileDiffAsync(string oldFilePath, string newFilePath, Window owner)
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
                   extension == ".bnk" ||
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