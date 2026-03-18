using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using AssetsManager.Services.Audio;
using AssetsManager.Services.Comparator;
using AssetsManager.Services.Explorer;
using AssetsManager.Services.Formatting;
using AssetsManager.Utils;
using AssetsManager.Views.Dialogs;
using AssetsManager.Views.Models.Audio;
using AssetsManager.Views.Models.Dialogs.Controls;
using AssetsManager.Views.Models.Explorer;
using AssetsManager.Views.Models.Wad;

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
        private readonly JsonFormatterService _jsonFormatterService;

        public DiffViewService(IServiceProvider serviceProvider, WadDifferenceService wadDifferenceService, CustomMessageBoxService customMessageBoxService, LogService logService, ContentFormatterService contentFormatterService, AudioBankLinkerService audioBankLinkerService, AudioBankService audioBankService, WadExtractionService wadExtractionService, JsonFormatterService jsonFormatterService)
        {
            _serviceProvider = serviceProvider;
            _wadDifferenceService = wadDifferenceService;
            _customMessageBoxService = customMessageBoxService;
            _logService = logService;
            _contentFormatterService = contentFormatterService;
            _audioBankLinkerService = audioBankLinkerService;
            _audioBankService = audioBankService;
            _wadExtractionService = wadExtractionService;
            _jsonFormatterService = jsonFormatterService;
        }

        public async Task ShowWadDiffAsync(SerializableChunkDiff diff, string oldPbePath, string newPbePath, Window owner, string sourceJsonPath = null)
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
                    await HandleAudioBankDiffAsync(diff, oldPbePath, newPbePath, owner, sourceJsonPath, loadingWindow);
                }
                else if (SupportedFileTypes.Images.Contains(extension) || SupportedFileTypes.Textures.Contains(extension))
                {
                    await HandleImageDiffAsync(diff, oldPbePath, newPbePath, owner, extension, loadingWindow);
                }
                else
                {
                    await HandleTextDiffAsync(diff, oldPbePath, newPbePath, owner, loadingWindow);
                }
            }
            catch (Exception ex)
            {
                loadingWindow.Close();
                _customMessageBoxService.ShowError("Comparison Error", $"An unexpected error occurred while preparing the file for comparison. Details: {ex.Message}", owner);
                _logService.LogError(ex, "Error showing WAD diff");
            }
        }

        public async Task ShowBatchWadDiffAsync(List<SerializableChunkDiff> diffs, int startIndex, string oldPbePath, string newPbePath, Window owner)
        {
            if (diffs == null || diffs.Count == 0) return;

            if (diffs.Count == 1)
            {
                await ShowWadDiffAsync(diffs[0], oldPbePath, newPbePath, owner);
                return;
            }

            // Check the type of the first file to decide which window to open
            var firstDiff = diffs[startIndex];
            var pathForCheck = firstDiff.NewPath ?? firstDiff.OldPath;
            string extension = Path.GetExtension(pathForCheck).ToLowerInvariant();

            if (SupportedFileTypes.Images.Contains(extension) || SupportedFileTypes.Textures.Contains(extension))
            {
                // Verify all files are images
                if (!diffs.All(d => {
                    var p = d.NewPath ?? d.OldPath;
                    var ext = Path.GetExtension(p).ToLowerInvariant();
                    return SupportedFileTypes.Images.Contains(ext) || SupportedFileTypes.Textures.Contains(ext);
                }))
                {
                    _customMessageBoxService.ShowError("Error", "Batch comparison only supports files of the same category (all Images or all Text/Data).", owner);
                    return;
                }

                var imageDiffWindow = new ImageDiffWindow { Owner = owner };
                await imageDiffWindow.LoadAndDisplayBatchDiffAsync(diffs, startIndex, oldPbePath, newPbePath, async (diff, oldPath, newPath) => {
                    var (dataType, oldData, newData, _, _) = await _wadDifferenceService.PrepareDifferenceDataAsync(diff, oldPath, newPath);
                    var ext = Path.GetExtension(diff.NewPath ?? diff.OldPath).ToLowerInvariant();
                    var oldImg = ToBitmapSource((byte[])oldData, ext);
                    var newImg = ToBitmapSource((byte[])newData, ext);
                    return (oldImg, newImg);
                });
                imageDiffWindow.Show();
            }
            else // Default to Text/Data Diff if it's not an image and it's supported
            {
                // Verify all files are NOT images (to allow text/bin/json/etc)
                if (diffs.Any(d => {
                    var p = d.NewPath ?? d.OldPath;
                    var ext = Path.GetExtension(p).ToLowerInvariant();
                    return SupportedFileTypes.Images.Contains(ext) || SupportedFileTypes.Textures.Contains(ext);
                }))
                {
                    _customMessageBoxService.ShowError("Error", "Mixed file types (Images + Text) are not supported in batch comparison.", owner);
                    return;
                }

                var diffWindow = _serviceProvider.GetRequiredService<JsonDiffWindow>();
                diffWindow.Owner = owner;
                await diffWindow.LoadAndDisplayBatchDiffAsync(diffs, startIndex, oldPbePath, newPbePath, async (diff, oldPath, newPath) => {
                    var (dataType, oldData, newData, _, _) = await _wadDifferenceService.PrepareDifferenceDataAsync(diff, oldPath, newPath);
                    var (oldText, newText) = await ProcessDataAsync(dataType, (byte[])oldData, (byte[])newData);
                    return (oldText, newText);
                });
                diffWindow.Show();
            }
        }

        private async Task HandleAudioBankDiffAsync(SerializableChunkDiff diff, string oldPbePath, string newPbePath, Window owner, string sourceJsonPath, LoadingDiffWindow loadingWindow)
        {
            _logService.LogDebug("[HandleAudioBankDiffAsync] Starting audio bank diff process.");
            string oldJson = "{}";
            string newJson = "{}";

            if (string.IsNullOrEmpty(diff.SourceWadFile))
            {
                _logService.LogWarning("[HandleAudioBankDiffAsync] SerializableChunkDiff.SourceWadFile is null. Audio event name resolution will likely fail.");
            }

            string backupRootDir = null;
            if (sourceJsonPath != null)
            {
                backupRootDir = Path.GetDirectoryName(sourceJsonPath);
            }

            if (diff.Type is ChunkDiffType.Modified or ChunkDiffType.Renamed or ChunkDiffType.Removed)
            {
                loadingWindow.SetState(DiffLoadingState.LinkingAudio);
                _logService.LogDebug("[HandleAudioBankDiffAsync] Linking OLD version of the audio bank.");
                string backupChunkPathOld = null;
                if (backupRootDir != null)
                {
                    backupChunkPathOld = Path.Combine(backupRootDir, "wad_chunks", "old", diff.SourceWadFile, $"{diff.OldPathHash:X16}.chunk");
                }
                var tempNodeOld = new FileSystemNodeModel { Name = Path.GetFileName(diff.OldPath), FullPath = diff.OldPath, SourceWadPath = diff.SourceWadFile, ChunkDiff = diff, BackupChunkPath = backupChunkPathOld, Type = NodeType.SoundBank };
                var linkedBankOld = await _audioBankLinkerService.LinkAudioBankForDiffAsync(tempNodeOld, oldPbePath, true, backupRootDir);
                if (linkedBankOld != null)
                {
                    _logService.LogDebug("[HandleAudioBankDiffAsync] OLD version linked successfully. Converting to string.");
                    oldJson = await AudioBankToStringAsync(linkedBankOld);
                }
                else
                {
                    _logService.LogWarning("[HandleAudioBankDiffAsync] Failed to link OLD version.");
                }
            }

            if (diff.Type is ChunkDiffType.Modified or ChunkDiffType.Renamed or ChunkDiffType.New)
            {
                loadingWindow.SetState(DiffLoadingState.AcquiringAudioComponents);
                _logService.LogDebug("[HandleAudioBankDiffAsync] Linking NEW version of the audio bank.");
                string backupChunkPathNew = null;
                if (backupRootDir != null)
                {
                    backupChunkPathNew = Path.Combine(backupRootDir, "wad_chunks", "new", diff.SourceWadFile, $"{diff.NewPathHash:X16}.chunk");
                }
                var tempNodeNew = new FileSystemNodeModel { Name = Path.GetFileName(diff.NewPath), FullPath = diff.NewPath, SourceWadPath = diff.SourceWadFile, ChunkDiff = diff, BackupChunkPath = backupChunkPathNew, Type = NodeType.SoundBank };
                var linkedBankNew = await _audioBankLinkerService.LinkAudioBankForDiffAsync(tempNodeNew, newPbePath, false, backupRootDir);
                if (linkedBankNew != null)
                {
                    loadingWindow.SetState(DiffLoadingState.ParsingAudioHierarchy);
                    _logService.LogDebug("[HandleAudioBankDiffAsync] NEW version linked successfully. Converting to string.");
                    newJson = await AudioBankToStringAsync(linkedBankNew);
                }
                else
                {
                    _logService.LogWarning("[HandleAudioBankDiffAsync] Failed to link NEW version.");
                }
            }

            _logService.LogDebug($"[HandleAudioBankDiffAsync] JSON for old version (first 100 chars): {(oldJson.Length > 100 ? oldJson.Substring(0, 100) : oldJson)}");
            _logService.LogDebug($"[HandleAudioBankDiffAsync] JSON for new version (first 100 chars): {(newJson.Length > 100 ? newJson.Substring(0, 100) : newJson)}");

            if (oldJson == newJson)
            {
                loadingWindow.Close();
                if (diff.Type == ChunkDiffType.Modified)
                {
                    _customMessageBoxService.ShowInfo("Information", "The file is marked as modified and has binary differences, but its parsed content is identical. No semantic changes were found.", owner);
                }
                else
                {
                    _customMessageBoxService.ShowInfo("Information", "No differences found. The two files are identical.", owner);
                }
                _logService.LogDebug("[HandleAudioBankDiffAsync] Parsed content is identical. Aborting diff view.");
                return;
            }

            loadingWindow.SetState(DiffLoadingState.Finalizing);
            var diffWindow = _serviceProvider.GetRequiredService<JsonDiffWindow>();
            diffWindow.Owner = owner;
            await diffWindow.LoadAndDisplayDiffAsync(oldJson, newJson, diff.OldPath, diff.NewPath);

            loadingWindow.SetState(DiffLoadingState.Ready);
            _logService.LogDebug("[HandleAudioBankDiffAsync] Displaying diff window.");
            loadingWindow.Close();
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

            var settings = new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
            return await _jsonFormatterService.FormatJsonAsync(result, settings);
        }

        private async Task HandleTextDiffAsync(SerializableChunkDiff diff, string oldPbePath, string newPbePath, Window owner, LoadingDiffWindow loadingWindow)
        {
            loadingWindow.SetState(DiffLoadingState.AcquiringBinaryData);
            var (dataType, oldData, newData, oldPath, newPath) = await _wadDifferenceService.PrepareDifferenceDataAsync(diff, oldPbePath, newPbePath);
            
            loadingWindow.SetState(DiffLoadingState.ParsingTextContent);
            var (oldText, newText) = await ProcessDataAsync(dataType, (byte[])oldData, (byte[])newData);

            if (oldText == newText)
            {
                loadingWindow.Close();
                _customMessageBoxService.ShowInfo("Info", "No differences found. The two files are identical.", owner);
                return;
            }

            loadingWindow.SetState(DiffLoadingState.CalculatingDifferences);
            var diffWindow = _serviceProvider.GetRequiredService<JsonDiffWindow>();
            diffWindow.Owner = owner;
            await diffWindow.LoadAndDisplayDiffAsync(oldText, newText, oldPath, newPath);

            loadingWindow.SetState(DiffLoadingState.Ready);
            loadingWindow.Close();
            diffWindow.ShowDialog();
        }

        private async Task HandleImageDiffAsync(SerializableChunkDiff diff, string oldPbePath, string newPbePath, Window owner, string extension, LoadingDiffWindow loadingWindow)
        {
            loadingWindow.SetState(DiffLoadingState.AcquiringTextureData);
            var (dataType, oldData, newData, oldPath, newPath) = await _wadDifferenceService.PrepareDifferenceDataAsync(diff, oldPbePath, newPbePath);

            loadingWindow.SetState(DiffLoadingState.DecodingTextures);
            var oldImage = ToBitmapSource((byte[])oldData, extension);
            var newImage = ToBitmapSource((byte[])newData, extension);

            loadingWindow.SetState(DiffLoadingState.Ready);
            loadingWindow.Close();
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
                _customMessageBoxService.ShowInfo("Information", "Image comparison for local files is not implemented yet.", owner);
                return;
            }

            var loadingWindow = new LoadingDiffWindow { Owner = owner };
            loadingWindow.Show();

            try
            {
                loadingWindow.SetState(DiffLoadingState.ReadingLocalFiles);
                var (dataType, oldData, newData) = await _wadDifferenceService.PrepareFileDifferenceDataAsync(oldFilePath, newFilePath);
                
                loadingWindow.SetState(DiffLoadingState.ParsingTextContent);
                var (oldText, newText) = await ProcessDataAsync(dataType, (byte[])oldData, (byte[])newData);

                if (oldText == newText)
                {
                    loadingWindow.Close();
                    _customMessageBoxService.ShowInfo("Information", "No differences found. The two files are identical.", owner);
                    return;
                }

                loadingWindow.SetState(DiffLoadingState.CalculatingDifferences);
                var diffWindow = _serviceProvider.GetRequiredService<JsonDiffWindow>();
                diffWindow.Owner = owner;
                await diffWindow.LoadAndDisplayDiffAsync(oldText, newText, Path.GetFileName(oldFilePath), Path.GetFileName(newFilePath));

                loadingWindow.SetState(DiffLoadingState.Ready);
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
                   SupportedFileTypes.Troybin.Contains(extension) ||
                   SupportedFileTypes.Preload.Contains(extension) ||
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
