using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using LeagueToolkit.Core.Wad;
using AssetsManager.Services.Formatting;
using AssetsManager.Utils;
using AssetsManager.Views.Dialogs;
using AssetsManager.Views.Models.Wad;
using AssetsManager.Services.Comparator;
using AssetsManager.Services.Explorer;
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
        private readonly WadContentProvider _wadContentProvider;
        private readonly CustomMessageBoxService _customMessageBoxService;
        private readonly LogService _logService;
        private readonly AudioBankService _audioBankService;
        private readonly AudioBankLinkerService _audioBankLinkerService;

        public DiffViewService(
            IServiceProvider serviceProvider,
            ContentFormatterService contentFormatterService,
            WadContentProvider wadContentProvider,
            CustomMessageBoxService customMessageBoxService,
            LogService logService,
            AudioBankService audioBankService,
            AudioBankLinkerService audioBankLinkerService)
        {
            _serviceProvider = serviceProvider;
            _contentFormatterService = contentFormatterService;
            _wadContentProvider = wadContentProvider;
            _customMessageBoxService = customMessageBoxService;
            _logService = logService;
            _audioBankService = audioBankService;
            _audioBankLinkerService = audioBankLinkerService;
        }

        public async Task ShowWadDiffAsync(SerializableChunkDiff diff, string oldPbePath, string newPbePath, Window owner, string sourceJsonPath = null)
        {
            if (diff == null) return;

            var pathForCheck = diff.NewPath ?? diff.OldPath;
            if (!SupportedFileTypes.IsDiffSupported(pathForCheck))
            {
                _customMessageBoxService.ShowInfo("Info", "This file type cannot be displayed in the difference viewer.", owner);
                return;
            }

            string extension = Path.GetExtension(pathForCheck).ToLowerInvariant();

            await RunWithLoadingAsync(owner, async loadingWindow =>
            {
                if (SupportedFileTypes.IsImage(pathForCheck))
                {
                    await HandleImageDiffAsync(diff, oldPbePath, newPbePath, owner, extension, loadingWindow);
                }
                else if (extension == ".bnk")
                {
                    await HandleParsedAudioBankDiffAsync(diff, oldPbePath, newPbePath, owner, loadingWindow, sourceJsonPath);
                }
                else
                {
                    await HandleTextDiffAsync(diff, oldPbePath, newPbePath, owner, loadingWindow);
                }
            }, "WAD diff");
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
            bool isImageBatch = SupportedFileTypes.IsImage(pathForCheck);

            await RunWithLoadingAsync(owner, async loadingWindow =>
            {
                if (isImageBatch)
                {
                    if (!diffs.All(d => SupportedFileTypes.IsImage(d.NewPath ?? d.OldPath)))
                    {
                        loadingWindow.Close();
                        _customMessageBoxService.ShowError("Error", "Batch comparison only supports files of the same category (all Images).", owner);
                        return;
                    }

                    var preparedImages = new List<(BitmapSource oldImage, BitmapSource newImage, string oldPath, string newPath)>();
                    for (int i = 0; i < diffs.Count; i++)
                    {
                        var diff = diffs[i];
                        loadingWindow.SetBatchIndex(i + 1, diffs.Count, "texture");
                        await SetStateAndRenderAsync(loadingWindow, DiffLoadingState.BatchLoadingFile);
                        var (_, oldData, newData, _, _) = await _wadContentProvider.GetFullDiffDataAsync(diff, oldPbePath, newPbePath);
                        
                        await SetStateAndRenderAsync(loadingWindow, DiffLoadingState.BatchFormattingFile);
                        var ext = Path.GetExtension(diff.NewPath ?? diff.OldPath).ToLowerInvariant();
                        var oldImage = await Task.Run(() => TextureUtils.LoadTexture((byte[])oldData, ext));
                        var newImage = await Task.Run(() => TextureUtils.LoadTexture((byte[])newData, ext));
                        preparedImages.Add((oldImage, newImage, diff.OldPath, diff.NewPath));
                        
                        await SetStateAndRenderAsync(loadingWindow, DiffLoadingState.BatchFileReady);
                    }

                    // [PROGRESS] 100% Reached before opening batch window
                    await SetStateAndRenderAsync(loadingWindow, DiffLoadingState.Ready);
                    await Task.Delay(450);

                    await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);

                    var imageDiffWindow = new ImageDiffWindow();
                    imageDiffWindow.Owner = owner;
                    imageDiffWindow.LoadingWindow = loadingWindow;
                    imageDiffWindow.LoadAndDisplayPreloadedBatchAsync(preparedImages, startIndex);
                    imageDiffWindow.ShowDialog();
                }
                else
                {
                    if (diffs.Any(d => SupportedFileTypes.IsImage(d.NewPath ?? d.OldPath)))
                    {
                        loadingWindow.Close();
                        _customMessageBoxService.ShowError("Error", "Batch comparison only supports files of the same category (all Text/Data).", owner);
                        return;
                    }

                    var preparedData = new List<(string oldText, string newText, string oldPath, string newPath)>();
                    for (int i = 0; i < diffs.Count; i++)
                    {
                        var diff = diffs[i];
                        string ext = Path.GetExtension(diff.Path).ToLowerInvariant();
                        string fileType = ext == ".bnk" ? "audio bank" : "file";
                        loadingWindow.SetBatchIndex(i + 1, diffs.Count, fileType);
                        await SetStateAndRenderAsync(loadingWindow, DiffLoadingState.BatchLoadingFile);
                        var (dataType, oldData, newData, oldPath, newPath) = await _wadContentProvider.GetFullDiffDataAsync(diff, oldPbePath, newPbePath);
                        
                        await SetStateAndRenderAsync(loadingWindow, DiffLoadingState.BatchFormattingFile);
                        string oldText, newText;
                        if (ext == ".bnk")
                        {
                            (oldText, newText) = await ProcessAudioBankDataAsync(diff, oldPbePath, newPbePath, (byte[])oldData, (byte[])newData, sourceJsonPath, null);
                        }
                        else
                        {
                            (oldText, newText) = await ProcessDataAsync(dataType, (byte[])oldData, (byte[])newData);
                        }
                        preparedData.Add((oldText, newText, oldPath ?? diff.OldPath, newPath ?? diff.NewPath));
                        
                        await SetStateAndRenderAsync(loadingWindow, DiffLoadingState.BatchFileReady);
                    }

                    // [PROGRESS] 100% Reached before opening batch window
                    await SetStateAndRenderAsync(loadingWindow, DiffLoadingState.Ready);
                    await Task.Delay(450);

                    await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);

                    var diffWindow = _serviceProvider.GetRequiredService<JsonDiffWindow>();
                    diffWindow.Owner = owner;
                    diffWindow.LoadingWindow = loadingWindow;
                    await diffWindow.LoadAndDisplayPreloadedBatchAsync(preparedData, startIndex);
                    diffWindow.ShowDialog();
                }
            }, "Batch WAD diff");
        }

        // Semantic audio bank comparison with sibling resolution (.wpk, .bin).
        private async Task HandleParsedAudioBankDiffAsync(SerializableChunkDiff diff, string oldPbePath, string newPbePath, Window owner, LoadingDiffWindow loadingWindow, string sourceJsonPath)
        {
            byte[] oldData = null;
            byte[] newData = null;
            string oldPath = null;
            string newPath = null;

            try
            {
                await SetStateAndRenderAsync(loadingWindow, DiffLoadingState.AcquiringBinaryData);
                var result = await _wadContentProvider.GetFullDiffDataAsync(diff, oldPbePath, newPbePath);
                oldData = (byte[])result.OldData;
                newData = (byte[])result.NewData;
                oldPath = result.OldPath;
                newPath = result.NewPath;

                if (oldData == null || newData == null)
                {
                    await ShowTextComparisonInternal(oldData, newData, "bnk", oldPath, newPath, owner, loadingWindow);
                    return;
                }

                var (oldJson, newJson) = await ProcessAudioBankDataAsync(diff, oldPbePath, newPbePath, oldData, newData, sourceJsonPath, loadingWindow);

                if (oldJson == newJson)
                {
                    loadingWindow.Close();
                    _customMessageBoxService.ShowInfo("Information", "No differences found in parsed audio bank.", owner);
                    return;
                }

                var diffWindow = _serviceProvider.GetRequiredService<JsonDiffWindow>();
                diffWindow.Owner = owner;
                diffWindow.LoadingWindow = loadingWindow;
                await diffWindow.LoadAndDisplayDiffAsync(oldJson, newJson, oldPath, newPath);

                // [PROGRESS] 100% Reached before opening window
                await SetStateAndRenderAsync(loadingWindow, DiffLoadingState.Ready);
                await Task.Delay(450);
                diffWindow.ShowDialog();
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Error showing parsed audio bank diff; falling back to raw JSON view.");
                try
                {
                    await SetStateAndRenderAsync(loadingWindow, DiffLoadingState.ParsingTextContent);
                    await ShowTextComparisonInternal(oldData, newData, "bnk", oldPath, newPath, owner, loadingWindow);
                }
                catch (Exception)
                {
                    if (loadingWindow != null && loadingWindow.IsVisible) loadingWindow.Close();
                    _customMessageBoxService.ShowError("Error", "Failed to prepare audio bank diff.", owner);
                }
            }
        }

        private async Task<(string oldJson, string newJson)> ProcessAudioBankDataAsync(SerializableChunkDiff diff, string oldPbePath, string newPbePath, byte[] oldData, byte[] newData, string sourceJsonPath, LoadingDiffWindow loadingWindow = null)
        {
            string backupRoot = !string.IsNullOrEmpty(sourceJsonPath) ? Path.GetDirectoryName(sourceJsonPath) : null;
            List<AudioEventNode> oldNodes, newNodes;

            if (string.IsNullOrEmpty(backupRoot))
            {
                if (loadingWindow != null) await SetStateAndRenderAsync(loadingWindow, DiffLoadingState.ParsingAudioHierarchy);
                (oldNodes, newNodes) = await _audioBankLinkerService.ResolveLiveAudioBankDiffAsync(diff, oldPbePath, newPbePath, oldData, newData);
            }
            else
            {
                if (loadingWindow != null) await SetStateAndRenderAsync(loadingWindow, DiffLoadingState.LinkingAudio);
                (oldNodes, newNodes) = await _audioBankLinkerService.ResolveArchivedAudioBankDiffAsync(diff, backupRoot, oldData, newData);
            }

            var settings = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = true };
            var oldJson = await Task.Run(() => JsonSerializer.Serialize(oldNodes, settings));
            var newJson = await Task.Run(() => JsonSerializer.Serialize(newNodes, settings));
            return (oldJson, newJson);
        }

        private async Task HandleTextDiffAsync(SerializableChunkDiff diff, string oldPbePath, string newPbePath, Window owner, LoadingDiffWindow loadingWindow)
        {
            await SetStateAndRenderAsync(loadingWindow, DiffLoadingState.AcquiringBinaryData);
            var (dataType, oldData, newData, oldPath, newPath) = await _wadContentProvider.GetFullDiffDataAsync(diff, oldPbePath, newPbePath);
            await ShowTextComparisonInternal((byte[])oldData, (byte[])newData, dataType, oldPath, newPath, owner, loadingWindow);
        }

        private async Task HandleImageDiffAsync(SerializableChunkDiff diff, string oldPbePath, string newPbePath, Window owner, string extension, LoadingDiffWindow loadingWindow)
        {
            await SetStateAndRenderAsync(loadingWindow, DiffLoadingState.AcquiringTextureData);
            var (dataType, oldData, newData, oldPath, newPath) = await _wadContentProvider.GetFullDiffDataAsync(diff, oldPbePath, newPbePath);
            await ShowImageComparisonInternal((byte[])oldData, (byte[])newData, oldPath, newPath, extension, owner, loadingWindow);
        }

        private async Task ShowTextComparisonInternal(byte[] oldData, byte[] newData, string dataType, string oldPath, string newPath, Window owner, LoadingDiffWindow loadingWindow)
        {
            await SetStateAndRenderAsync(loadingWindow, DiffLoadingState.ParsingTextContent);
            var (oldText, newText) = await ProcessDataAsync(dataType, oldData, newData);

            if (oldText == newText)
            {
                loadingWindow.Close();
                _customMessageBoxService.ShowInfo("Information", "No differences found. The two files are identical.", owner);
                return;
            }

            var diffWindow = _serviceProvider.GetRequiredService<JsonDiffWindow>();
            diffWindow.Owner = owner;
            diffWindow.LoadingWindow = loadingWindow;
            await diffWindow.LoadAndDisplayDiffAsync(oldText, newText, oldPath, newPath);

            // [PROGRESS] 100% Reached before opening window
            await SetStateAndRenderAsync(loadingWindow, DiffLoadingState.Ready);
            await Task.Delay(450);
            diffWindow.ShowDialog();
        }

        private async Task ShowImageComparisonInternal(byte[] oldData, byte[] newData, string oldPath, string newPath, string extension, Window owner, LoadingDiffWindow loadingWindow)
        {
            await SetStateAndRenderAsync(loadingWindow, DiffLoadingState.DecodingTextures);
            var oldImage = await Task.Run(() => TextureUtils.LoadTexture(oldData, extension));
            var newImage = await Task.Run(() => TextureUtils.LoadTexture(newData, extension));

            await SetStateAndRenderAsync(loadingWindow, DiffLoadingState.RenderingUI);
            var imageDiffWindow = new ImageDiffWindow(oldImage, newImage, oldPath, newPath);
            imageDiffWindow.Owner = owner;
            imageDiffWindow.LoadingWindow = loadingWindow;
            
            // [PROGRESS] 100% Reached before opening window
            await SetStateAndRenderAsync(loadingWindow, DiffLoadingState.Ready);
            await Task.Delay(450);
            imageDiffWindow.ShowDialog();
        }

        public async Task ShowFileDiffAsync(string oldFilePath, string newFilePath, Window owner)
        {
            if (!File.Exists(oldFilePath) && !File.Exists(newFilePath))
            {
                _customMessageBoxService.ShowError("Error", "Neither of the files to compare exist.", owner);
                return;
            }

            string extension = Path.GetExtension(newFilePath ?? oldFilePath).ToLowerInvariant();
            bool isImage = SupportedFileTypes.IsImage(newFilePath ?? oldFilePath);

            await RunWithLoadingAsync(owner, async loadingWindow =>
            {
                await SetStateAndRenderAsync(loadingWindow, DiffLoadingState.ReadingLocalFiles);
                if (isImage)
                {
                    byte[] oldData = File.Exists(oldFilePath) ? await File.ReadAllBytesAsync(oldFilePath) : null;
                    byte[] newData = File.Exists(newFilePath) ? await File.ReadAllBytesAsync(newFilePath) : null;
                    await ShowImageComparisonInternal(oldData, newData, Path.GetFileName(oldFilePath), Path.GetFileName(newFilePath), extension, owner, loadingWindow);
                }
                else
                {
                    var (dataType, oldData, newData) = await _wadContentProvider.GetFileDiffDataAsync(oldFilePath, newFilePath);
                    await ShowTextComparisonInternal((byte[])oldData, (byte[])newData, dataType, Path.GetFileName(oldFilePath), Path.GetFileName(newFilePath), owner, loadingWindow);
                }
            }, "File diff");
        }

        private async Task<(string oldText, string newText)> ProcessDataAsync(string dataType, byte[] oldData, byte[] newData)
        {
            // Run sequentially to optimize peak memory. Parsing two massive files concurrently (e.g. 33MB .bin files) 
            // creates two massive object graphs in RAM simultaneously, causing LOH fragmentation and OOM exceptions.
            string oldText = await _contentFormatterService.GetFormattedStringAsync(dataType, oldData);
            string newText = await _contentFormatterService.GetFormattedStringAsync(dataType, newData);
            return (oldText, newText);
        }

        private async Task SetStateAndRenderAsync(LoadingDiffWindow loadingWindow, DiffLoadingState state)
        {
            if (loadingWindow == null) return;
            await loadingWindow.SetStateAndRenderAsync(state);
        }

        // Centraliza el ciclo de vida de la ventana de carga
        private async Task RunWithLoadingAsync(Window owner, Func<LoadingDiffWindow, Task> body, string operationLabel)
        {
            var loadingWindow = new LoadingDiffWindow { Owner = owner };
            loadingWindow.Show();
            try
            {
                await body(loadingWindow);
            }
            catch (Exception ex)
            {
                if (loadingWindow != null && loadingWindow.IsVisible) loadingWindow.Close();
                _logService.LogError(ex, $"Error during {operationLabel}");
                _customMessageBoxService.ShowError("Comparison Error", $"An unexpected error occurred while preparing the file for comparison. Details: {ex.Message}", owner);
            }
        }
    }
}