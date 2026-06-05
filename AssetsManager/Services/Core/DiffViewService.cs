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
        private readonly AudioBankLinkerService _audioBankLinkerService;

        public DiffViewService(
            IServiceProvider serviceProvider,
            ContentFormatterService contentFormatterService,
            WadDiffProvider wadDiffProvider,
            CustomMessageBoxService customMessageBoxService,
            LogService logService,
            AudioBankService audioBankService,
            AudioBankLinkerService audioBankLinkerService)
        {
            _serviceProvider = serviceProvider;
            _contentFormatterService = contentFormatterService;
            _wadDiffProvider = wadDiffProvider;
            _customMessageBoxService = customMessageBoxService;
            _logService = logService;
            _audioBankService = audioBankService;
            _audioBankLinkerService = audioBankLinkerService;
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
                if (SupportedFileTypes.Images.Contains(extension) || SupportedFileTypes.Textures.Contains(extension))
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

        // Parsed audio-bank diff: locates the bank siblings (audio.bnk / .wpk / .bin)
        // via AudioBankLinkerService (the same 5-strategy .bin resolver + sibling
        // detection used by WadPackagingService.CreateLeanWadPackageAsync), then
        // reads the bytes exclusively from the comparison's wad_chunks/old|new
        // directory. Falls back to the standard raw-JSON text view when the bank
        // cannot be linked, parsed, or the comparison has not been archived yet.
        private async Task HandleParsedAudioBankDiffAsync(SerializableChunkDiff diff, string oldPbePath, string newPbePath, Window owner, LoadingDiffWindow loadingWindow, string sourceJsonPath)
        {
            try
            {
                loadingWindow.SetState(DiffLoadingState.AcquiringBinaryData);
                var (dataType, oldData, newData, oldPath, newPath) = await _wadDiffProvider.PrepareDifferenceDataAsync(diff, oldPbePath, newPbePath);

                if (oldData is not byte[] oldBnk || newData is not byte[] newBnkBytes)
                {
                    // Defensive: if either side is missing, fall through to raw text view.
                    await ShowTextComparisonInternal((byte[])oldData, (byte[])newData, "bnk", oldPath, newPath, owner, loadingWindow);
                    return;
                }

                string backupRoot = !string.IsNullOrEmpty(sourceJsonPath) ? Path.GetDirectoryName(sourceJsonPath) : null;
                if (string.IsNullOrEmpty(backupRoot))
                {
                    loadingWindow.SetState(DiffLoadingState.ParsingAudioHierarchy);
                    List<AudioEventNode> liveOldNodes;
                    List<AudioEventNode> liveNewNodes;
                    try
                    {
                        (liveOldNodes, liveNewNodes) = await _audioBankLinkerService.ResolveLiveAudioBankDiffAsync(diff, oldPbePath, newPbePath, oldBnk, newBnkBytes);
                    }
                    catch (Exception ex)
                    {
                        _logService.LogError(ex, "Error resolving live audio bank diff; falling back to raw view.");
                        await ShowTextComparisonInternal(oldBnk, newBnkBytes, "bnk", oldPath, newPath, owner, loadingWindow);
                        return;
                    }

                    if (liveOldNodes == null || liveNewNodes == null)
                    {
                        await ShowTextComparisonInternal(oldBnk, newBnkBytes, "bnk", oldPath, newPath, owner, loadingWindow);
                        return;
                    }

                    loadingWindow.SetState(DiffLoadingState.CalculatingDifferences);

                    var settings = new JsonSerializerOptions
                    {
                        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                        WriteIndented = true
                    };
                    string oldJson = JsonSerializer.Serialize(liveOldNodes, settings);
                    string newJson = JsonSerializer.Serialize(liveNewNodes, settings);

                    if (oldJson == newJson)
                    {
                        loadingWindow.Close();
                        _customMessageBoxService.ShowInfo("Information", "No differences found in parsed audio bank.", owner);
                        return;
                    }

                    var diffWindow = _serviceProvider.GetRequiredService<JsonDiffWindow>();
                    diffWindow.Owner = owner;
                    await diffWindow.LoadAndDisplayDiffAsync(oldJson, newJson, oldPath, newPath, loadingWindow);
                    diffWindow.ShowDialog();
                    owner?.Activate();
                    return;
                }

                loadingWindow.SetState(DiffLoadingState.LinkingAudio);

                {
                    List<AudioDependencyInfo> resolvedDeps = _audioBankLinkerService.ResolveAudioBankDependencies(diff);
                    Dictionary<string, AssociatedDependency> depByPath = (diff.Dependencies ?? new List<AssociatedDependency>())
                        .GroupBy(d => d.Path, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                    bool clickedIsEventsBnk = (diff.NewPath ?? diff.OldPath ?? string.Empty).Contains("_events", StringComparison.OrdinalIgnoreCase);
                    string fileName = Path.GetFileName(diff.NewPath ?? diff.OldPath);
                    string baseName = Path.GetFileNameWithoutExtension(StripBankSuffix(fileName));

                    byte[] oldEventsBnk = null, oldAudioBnk = null, oldWpk = null, oldBin = null;
                    byte[] newEventsBnk = null, newAudioBnk = null, newWpk = null, newBin = null;
                    foreach (var dep in resolvedDeps)
                    {
                        if (!depByPath.TryGetValue(dep.Path, out var assoc) || assoc == null)
                        {
                            continue;
                        }

                        byte[] oldBytes = await TryReadBackupChunkAsync(backupRoot, dep.SourceWad, assoc.OldPathHash, assoc.CompressionType, isOld: true);
                        byte[] newBytes = await TryReadBackupChunkAsync(backupRoot, dep.SourceWad, assoc.NewPathHash, assoc.CompressionType, isOld: false);

                        switch (dep.Type)
                        {
                            case AudioDependencyType.EventsBnk:
                                oldEventsBnk = oldBytes;
                                newEventsBnk = newBytes;
                                break;
                            case AudioDependencyType.AudioBnk:
                                oldAudioBnk = oldBytes;
                                newAudioBnk = newBytes;
                                break;
                            case AudioDependencyType.AudioWpk:
                                oldWpk = oldBytes;
                                newWpk = newBytes;
                                break;
                            case AudioDependencyType.Bin:
                                oldBin = oldBytes;
                                newBin = newBytes;
                                break;
                        }
                    }

                    if (clickedIsEventsBnk)
                    {
                        oldEventsBnk = oldEventsBnk ?? oldBnk;
                        newEventsBnk = newEventsBnk ?? newBnkBytes;
                    }
                    else
                    {
                        oldAudioBnk = oldAudioBnk ?? oldBnk;
                        newAudioBnk = newAudioBnk ?? newBnkBytes;
                    }

                    if (oldEventsBnk == null && oldAudioBnk == null)
                    {
                        await ShowTextComparisonInternal(oldBnk, newBnkBytes, "bnk", oldPath, newPath, owner, loadingWindow);
                        return;
                    }

                    var oldNodes = _audioBankService.ParseAudioBank(
                        wpkData: oldWpk,
                        audioBnkData: oldAudioBnk,
                        eventsData: oldEventsBnk,
                        binData: oldBin,
                        baseName: baseName,
                        binType: BinType.Unknown);

                    var newNodes = _audioBankService.ParseAudioBank(
                        wpkData: newWpk,
                        audioBnkData: newAudioBnk,
                        eventsData: newEventsBnk,
                        binData: newBin,
                        baseName: baseName,
                        binType: BinType.Unknown);

                    if (oldNodes == null || newNodes == null)
                    {
                        await ShowTextComparisonInternal(oldBnk, newBnkBytes, "bnk", oldPath, newPath, owner, loadingWindow);
                        return;
                    }

                    loadingWindow.SetState(DiffLoadingState.CalculatingDifferences);

                    var settings = new JsonSerializerOptions
                    {
                        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                        WriteIndented = true
                    };
                    string oldJson = JsonSerializer.Serialize(oldNodes, settings);
                    string newJson = JsonSerializer.Serialize(newNodes, settings);

                    if (oldJson == newJson)
                    {
                        loadingWindow.Close();
                        _customMessageBoxService.ShowInfo("Information", "No differences found in parsed audio bank.", owner);
                        return;
                    }

                    var diffWindow = _serviceProvider.GetRequiredService<JsonDiffWindow>();
                    diffWindow.Owner = owner;
                    await diffWindow.LoadAndDisplayDiffAsync(oldJson, newJson, oldPath, newPath, loadingWindow);
                    diffWindow.ShowDialog();
                    owner?.Activate();
                }
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Error showing parsed audio bank diff; falling back to raw JSON view.");
                try
                {
                    loadingWindow.SetState(DiffLoadingState.ParsingTextContent);
                    var (dataType, oldData, newData, oldPath, newPath) = await _wadDiffProvider.PrepareDifferenceDataAsync(diff, oldPbePath, newPbePath);
                    await ShowTextComparisonInternal((byte[])oldData, (byte[])newData, "bnk", oldPath, newPath, owner, loadingWindow);
                }
                catch
                {
                    loadingWindow.Close();
                    _customMessageBoxService.ShowError("Error", "Failed to prepare audio bank diff.", owner);
                }
            }
        }

        // Reads a saved backup chunk from wad_chunks/old|new/{SourceWad}/{hash:X16}.chunk
        // and decompresses it using the dependency's original compression type.
        // Returns null if the chunk is missing on disk. NEVER reads from the live
        // WADs (MAIN/backups) — those are continuous-update directories and would
        // yield results inconsistent with the comparison snapshot.
        private Task<byte[]> TryReadBackupChunkAsync(string backupRoot, string sourceWad, ulong hash, WadChunkCompression? compressionType, bool isOld)
        {
            return Task.Run(() =>
            {
                if (string.IsNullOrEmpty(backupRoot) || hash == 0) return null;
                string chunkDir = isOld ? "old" : "new";
                string chunkPath = Path.Combine(backupRoot, "wad_chunks", chunkDir, sourceWad ?? string.Empty, $"{hash:X16}.chunk");
                if (!File.Exists(chunkPath)) return null;
                byte[] compressedData = File.ReadAllBytes(chunkPath);
                return WadChunkUtils.DecompressChunk(compressedData, compressionType ?? WadChunkCompression.None);
            });
        }

        // Strips trailing ".bnk" or "_events.bnk" from a file name to obtain the
        // common base name (e.g. "lux_skin18_vo_events.bnk" → "lux_skin18_vo_events",
        // then the caller uses Path.GetFileNameWithoutExtension to drop ".bnk").
        private static string StripBankSuffix(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return fileName;
            const string eventsSuffix = "_events.bnk";
            if (fileName.EndsWith(eventsSuffix, StringComparison.OrdinalIgnoreCase))
                return fileName.Substring(0, fileName.Length - eventsSuffix.Length);
            if (fileName.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase))
                return fileName.Substring(0, fileName.Length - 4);
            return fileName;
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
    }
}
