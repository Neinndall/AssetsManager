using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using AssetsManager.Services.Formatting;
using AssetsManager.Utils;
using AssetsManager.Views.Dialogs;
using AssetsManager.Views.Models.Wad;
using AssetsManager.Services.Comparator;
using AssetsManager.Views.Helpers;
using AssetsManager.Services.Audio;
using AssetsManager.Views.Models.Dialogs.Controls;
using AssetsManager.Views.Models.Audio;

namespace AssetsManager.Services.Core
{
    public class DiffViewService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ContentFormatterService _contentFormatterService;
        private readonly WadDiffProvider _wadDiffProvider;
        private readonly CustomMessageBoxService _customMessageBoxService;
        private readonly LogService _logService;
        private readonly AudioBankService _audioBankService;
        private readonly JsonFormatterService _jsonFormatterService;

        public DiffViewService(
            IServiceProvider serviceProvider,
            ContentFormatterService contentFormatterService,
            WadDiffProvider wadDiffProvider,
            CustomMessageBoxService customMessageBoxService,
            LogService logService,
            AudioBankService audioBankService,
            JsonFormatterService jsonFormatterService)
        {
            _serviceProvider = serviceProvider;
            _contentFormatterService = contentFormatterService;
            _wadDiffProvider = wadDiffProvider;
            _customMessageBoxService = customMessageBoxService;
            _logService = logService;
            _audioBankService = audioBankService;
            _jsonFormatterService = jsonFormatterService;
        }

        public async Task ShowWadDiffAsync(SerializableChunkDiff diff, string oldPbePath, string newPbePath, Window owner, string sourceJsonPath = null)
        {
            var pathForCheck = diff.NewPath ?? diff.OldPath;
            string extension = Path.GetExtension(pathForCheck).ToLowerInvariant();

            var loadingWindow = new LoadingDiffWindow { Owner = owner };
            loadingWindow.Show();

            try
            {
                if (SupportedFileTypes.AudioBank.Contains(extension))
                {
                    await HandleAudioBankDiffAsync(diff, oldPbePath, newPbePath, owner, sourceJsonPath, loadingWindow);
                }
                else if (SupportedFileTypes.IsImage(pathForCheck))
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

        public async Task ShowBatchWadDiffAsync(List<SerializableChunkDiff> diffs, int startIndex, string oldPbePath, string newPbePath, Window owner, string sourceJsonPath = null)
        {
            if (diffs == null || diffs.Count == 0) return;

            if (diffs.Count == 1)
            {
                await ShowWadDiffAsync(diffs[0], oldPbePath, newPbePath, owner, sourceJsonPath);
                return;
            }

            var firstDiff = diffs[startIndex];
            var pathForCheck = firstDiff.NewPath ?? firstDiff.OldPath;

            var loadingWindow = new LoadingDiffWindow { Owner = owner };
            loadingWindow.Show();

            try
            {
                if (SupportedFileTypes.IsImage(pathForCheck))
                {
                    if (!diffs.All(d => SupportedFileTypes.IsImage(d.NewPath ?? d.OldPath)))
                    {
                        loadingWindow.Close();
                        _customMessageBoxService.ShowError("Error", "Batch comparison only supports files of the same category (all Images or all Text/Data).", owner);
                        return;
                    }

                    var preparedImages = new List<(BitmapSource oldImage, BitmapSource newImage, string oldPath, string newPath)>();
                    for (int i = 0; i < diffs.Count; i++)
                    {
                        var diff = diffs[i];
                        loadingWindow.SetBatchIndex(i + 1, diffs.Count);
                        loadingWindow.SetState(DiffLoadingState.BatchLoadingFile);
                        var (dataType, oldD, newD, _, _) = await _wadDiffProvider.PrepareDifferenceDataAsync(diff, oldPbePath, newPbePath);
                        loadingWindow.SetState(DiffLoadingState.BatchFormattingFile);
                        var ext = Path.GetExtension(diff.NewPath ?? diff.OldPath).ToLowerInvariant();
                        var oldImg = TextureUtils.LoadTexture((byte[])oldD, ext);
                        var newImg = TextureUtils.LoadTexture((byte[])newD, ext);
                        preparedImages.Add((oldImg, newImg, diff.OldPath, diff.NewPath));
                        loadingWindow.SetState(DiffLoadingState.BatchFileReady);
                    }

                    // Force a render pass to ensure progress bar paints 100% before displaying the new window
                    await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);

                    var imageDiffWindow = new ImageDiffWindow();
                    imageDiffWindow.Owner = owner;
                    imageDiffWindow.LoadingWindow = loadingWindow;
                    imageDiffWindow.LoadAndDisplayPreloadedBatchAsync(preparedImages, startIndex);
                    imageDiffWindow.ShowDialog();
                    owner?.Activate();
                }
                else
                {
                    if (diffs.Any(d => {
                        var p = d.NewPath ?? d.OldPath;
                        return SupportedFileTypes.IsImage(p) || SupportedFileTypes.AudioBank.Contains(Path.GetExtension(p).ToLowerInvariant());
                    }))
                    {
                        loadingWindow.Close();
                        _customMessageBoxService.ShowError("Error", "Batch comparison only supports files of the same category (all Text/Data).", owner);
                        return;
                    }

                    var preparedData = new List<(string oldText, string newText, string oldPath, string newPath)>();
                    for (int i = 0; i < diffs.Count; i++)
                    {
                        var diff = diffs[i];
                        loadingWindow.SetBatchIndex(i + 1, diffs.Count);
                        loadingWindow.SetState(DiffLoadingState.BatchLoadingFile);
                        var (dataType, oldD, newD, oldPath, newPath) = await _wadDiffProvider.PrepareDifferenceDataAsync(diff, oldPbePath, newPbePath);
                        loadingWindow.SetState(DiffLoadingState.BatchFormattingFile);
                        var (oldText, newText) = await ProcessDataAsync(dataType, (byte[])oldD, (byte[])newD);
                        preparedData.Add((oldText, newText, oldPath ?? diff.OldPath, newPath ?? diff.NewPath));
                        loadingWindow.SetState(DiffLoadingState.BatchFileReady);
                    }

                    // Force a render pass to ensure progress bar paints 100% before displaying the new window
                    await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);

                    var diffWindow = _serviceProvider.GetRequiredService<JsonDiffWindow>();
                    diffWindow.Owner = owner;
                    diffWindow.LoadingWindow = loadingWindow;
                    await diffWindow.LoadAndDisplayPreloadedBatchAsync(preparedData, startIndex);

                    diffWindow.ShowDialog();
                    owner?.Activate();
                }
            }
            catch (Exception ex)
            {
                if (loadingWindow != null)
                {
                    loadingWindow.Close();
                }
                _customMessageBoxService.ShowError("Batch Comparison Error", $"An unexpected error occurred: {ex.Message}", owner);
                _logService.LogError(ex, "Error showing batch WAD diff");
            }
        }

        private async Task HandleAudioBankDiffAsync(SerializableChunkDiff diff, string oldPbePath, string newPbePath, Window owner, string sourceJsonPath, LoadingDiffWindow loadingWindow)
        {
            loadingWindow.SetState(DiffLoadingState.AcquiringBinaryData);
            var (dataType, oldData, newData, oldPath, newPath) = await _wadDiffProvider.PrepareDifferenceDataAsync(diff, oldPbePath, newPbePath);

            loadingWindow.SetState(DiffLoadingState.LinkingAudio);
            var oldJson = await DecodeAudioBankAsync((byte[])oldData, diff.OldPath, sourceJsonPath);
            var newJson = await DecodeAudioBankAsync((byte[])newData, diff.NewPath, sourceJsonPath);

            if (oldJson == newJson)
            {
                loadingWindow.Close();
                _customMessageBoxService.ShowInfo("Info", "No differences found in audio bank logic.", owner);
                return;
            }

            loadingWindow.SetState(DiffLoadingState.CalculatingDifferences);
            var diffWindow = _serviceProvider.GetRequiredService<JsonDiffWindow>();
            diffWindow.Owner = owner;
            await diffWindow.LoadAndDisplayDiffAsync(oldJson, newJson, oldPath, newPath, loadingWindow);

            diffWindow.ShowDialog();
            owner?.Activate();
        }

        private async Task<string> DecodeAudioBankAsync(byte[] data, string path, string sourceJsonPath)
        {
            if (data == null || data.Length == 0) return "{}";

            var extension = Path.GetExtension(path).ToLowerInvariant();
            byte[] audioBnkData = null;
            byte[] wpkData = null;

            if (extension == ".bnk") audioBnkData = data;
            else if (extension == ".wpk") wpkData = data;

            // Simple parse for now when using raw bytes without full linker context
            List<AudioEventNode> result = _audioBankService.ParseGenericAudioBank(wpkData, audioBnkData, null);

            if (result == null || result.Count == 0) return "{}";

            var settings = new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
            return await _jsonFormatterService.FormatJsonAsync(result, settings);
        }

        private async Task HandleTextDiffAsync(SerializableChunkDiff diff, string oldPbePath, string newPbePath, Window owner, LoadingDiffWindow loadingWindow)
        {
            loadingWindow.SetState(DiffLoadingState.AcquiringBinaryData);
            var (dataType, oldData, newData, oldPath, newPath) = await _wadDiffProvider.PrepareDifferenceDataAsync(diff, oldPbePath, newPbePath);
            
            await ShowTextComparisonInternal((byte[])oldData, (byte[])newData, dataType, oldPath, newPath, owner, loadingWindow);
        }

        private async Task HandleImageDiffAsync(SerializableChunkDiff diff, string oldPbePath, string newPbePath, Window owner, string extension, LoadingDiffWindow loadingWindow)
        {
            loadingWindow.SetState(DiffLoadingState.AcquiringTextureData);
            var (dataType, oldData, newData, oldPath, newPath) = await _wadDiffProvider.PrepareDifferenceDataAsync(diff, oldPbePath, newPbePath);

            await ShowImageComparisonInternal((byte[])oldData, (byte[])newData, oldPath, newPath, extension, owner, loadingWindow);
        }

        private async Task ShowTextComparisonInternal(byte[] oldData, byte[] newData, string dataType, string oldPath, string newPath, Window owner, LoadingDiffWindow loadingWindow)
        {
            loadingWindow.SetState(DiffLoadingState.ParsingTextContent);
            var (oldText, newText) = await ProcessDataAsync(dataType, oldData, newData);

            if (oldText == newText)
            {
                loadingWindow.Close();
                _customMessageBoxService.ShowInfo("Information", "No differences found. The two files are identical.", owner);
                return;
            }

            loadingWindow.SetState(DiffLoadingState.CalculatingDifferences);
            var diffWindow = _serviceProvider.GetRequiredService<JsonDiffWindow>();
            diffWindow.Owner = owner;
            await diffWindow.LoadAndDisplayDiffAsync(oldText, newText, oldPath, newPath, loadingWindow);

            diffWindow.ShowDialog();
            owner?.Activate();
        }

        private async Task ShowImageComparisonInternal(byte[] oldData, byte[] newData, string oldPath, string newPath, string extension, Window owner, LoadingDiffWindow loadingWindow)
        {
            loadingWindow.SetState(DiffLoadingState.DecodingTextures);
            var oldImage = TextureUtils.LoadTexture(oldData, extension);
            var newImage = TextureUtils.LoadTexture(newData, extension);

            var imageDiffWindow = new ImageDiffWindow(oldImage, newImage, oldPath, newPath) 
            { 
                Owner = owner, 
                LoadingWindow = loadingWindow 
            };
            
            imageDiffWindow.ShowDialog();
            owner?.Activate();
        }

        public async Task ShowFileDiffAsync(string oldFilePath, string newFilePath, Window owner)
        {
            if (!File.Exists(oldFilePath) && !File.Exists(newFilePath))
            {
                _customMessageBoxService.ShowError("Error", "Neither of the files to compare exist.", owner);
                return;
            }

            string extension = Path.GetExtension(newFilePath ?? oldFilePath).ToLowerInvariant();
            
            var loadingWindow = new LoadingDiffWindow { Owner = owner };
            loadingWindow.Show();

            try
            {
                if (SupportedFileTypes.IsImage(newFilePath ?? oldFilePath))
                {
                    loadingWindow.SetState(DiffLoadingState.ReadingLocalFiles);
                    byte[] oldData = File.Exists(oldFilePath) ? await File.ReadAllBytesAsync(oldFilePath) : null;
                    byte[] newData = File.Exists(newFilePath) ? await File.ReadAllBytesAsync(newFilePath) : null;

                    await ShowImageComparisonInternal(oldData, newData, Path.GetFileName(oldFilePath), Path.GetFileName(newFilePath), extension, owner, loadingWindow);
                }
                else
                {
                    loadingWindow.SetState(DiffLoadingState.ReadingLocalFiles);
                    var (dataType, oldData, newData) = await _wadDiffProvider.PrepareFileDifferenceDataAsync(oldFilePath, newFilePath);
                    
                    await ShowTextComparisonInternal((byte[])oldData, (byte[])newData, dataType, Path.GetFileName(oldFilePath), Path.GetFileName(newFilePath), owner, loadingWindow);
                }
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
    }
}
