using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Formatting;
using AssetsManager.Utils;
using AssetsManager.Utils.Framework;
using AssetsManager.Views.Models.Explorer;
using AssetsManager.Views.Models.Wad;
using AssetsManager.Views.Models.Settings;
using AssetsManager.Views.Models.Dialogs.Controls;

namespace AssetsManager.Services.Downloads
{
    /// <summary>
    /// Orchestrates asset extraction. Picks the export mode (EXTRACT = raw,
    /// SAVE = smart) and maps the user Settings to the target formats; the actual
    /// export work is delegated to <see cref="AssetExportService"/>.
    /// </summary>
    public class ExtractionService
    {
        private readonly AppSettings _appSettings;
        private readonly LogService _logService;
        private readonly DirectoriesCreator _directoriesCreator;
        private readonly AssetExportService _assetExportService;

        private static readonly ExportFormats RawFormats = new(
            WadExportMode.Original, AudioExportFormat.Ogg, ImageExportFormat.Original, DataExportFormat.Original);

        public event Action<int> ExtractionStarted;
        public event Action<int, int, string> ExtractionProgressChanged;
        public event Func<Task> ExtractionCompleted;

        public event Action<int> SavingStarted;
        public event Action<int, int, string> SavingProgressChanged;
        public event Func<Task> SavingCompleted;

        public ExtractionService(
            AppSettings appSettings,
            LogService logService,
            DirectoriesCreator directoriesCreator,
            AssetExportService assetExportService)
        {
            _appSettings = appSettings;
            _logService = logService;
            _directoriesCreator = directoriesCreator;
            _assetExportService = assetExportService;
        }

        #region Public Interface (Save, Extract, Extract New Assets)

        /// <summary>
        /// Auto-extract after a comparison. The only flow driven by the user Settings:
        /// textures use ImageExportFormat, data uses DataExportFormat. Audio is always
        /// exported raw (banks .bnk/.wpk and standalone .wem), because AudioExportFormat
        /// only applies to the Explorer Save flow.
        /// </summary>
        public async Task<List<ExtractResultItem>> ExtractNewFilesFromComparisonAsync(
            List<SerializableChunkDiff> allDiffs,
            string newLolPath,
            CancellationToken cancellationToken)
        {
            var newDiffs = allDiffs.Where(d => d.Type == ChunkDiffType.New).ToList();

            if (newDiffs.Count == 0)
            {
                _logService.Log("No new assets to extract from the comparison.");
                await NotifyExtractionCompletedAsync();
                return new List<ExtractResultItem>();
            }

            var settingsFormats = new ExportFormats(
                WadExportMode.Original,
                _appSettings.AudioExportFormat,
                _appSettings.ImageExportFormat,
                _appSettings.DataExportFormat);

            return await ExtractDiffsCoreAsync(
                newDiffs, null, newLolPath, cancellationToken,
                diff => GetModeFromSettings(diff),
                settingsFormats);
        }

        /// <summary>Manual "Extract" action: raw data, no conversion (same as Explorer Extract).</summary>
        public async Task<List<ExtractResultItem>> ExtractRawAsync(
            List<SerializableChunkDiff> diffs,
            string oldLolPath,
            string newLolPath,
            CancellationToken cancellationToken)
        {
            if (diffs == null || diffs.Count == 0)
            {
                await NotifyExtractionCompletedAsync();
                return new List<ExtractResultItem>();
            }

            return await ExtractDiffsCoreAsync(
                diffs, oldLolPath, newLolPath, cancellationToken,
                _ => WadExportMode.Original,
                RawFormats);
        }

        /// <summary>
        /// Manual "Save (Smart)" action: converts to PNG/JSON/audio (same as Explorer Save).
        /// Audio banks (.bnk/.wpk) are always exported raw, exactly like in the Explorer flow.
        /// </summary>
        public async Task<List<ExtractResultItem>> ExtractSmartAsync(
            List<SerializableChunkDiff> diffs,
            string oldLolPath,
            string newLolPath,
            CancellationToken cancellationToken)
        {
            if (diffs == null || diffs.Count == 0)
            {
                await NotifyExtractionCompletedAsync();
                return new List<ExtractResultItem>();
            }

            return await ExtractDiffsCoreAsync(
                diffs, oldLolPath, newLolPath, cancellationToken,
                diff => GetModeForSmartExport(diff),
                ExportFormats.ExplorerSmart(_appSettings.AudioExportFormat));
        }

        public WadExportMode GetModeFromSettings(SerializableChunkDiff diff)
        {
            if (SupportedFileTypes.IsAudioBank(diff.FileName))
                return WadExportMode.Original;

            if (SupportedFileTypes.IsImage(diff.FileName))
                return _appSettings.ImageExportFormat == ImageExportFormat.Original ? WadExportMode.Original : WadExportMode.Smart;

            if (SupportedFileTypes.IsText(diff.FileName))
                return _appSettings.DataExportFormat == DataExportFormat.Original ? WadExportMode.Original : WadExportMode.Smart;

            return WadExportMode.Original;
        }

        private WadExportMode GetModeForSmartExport(SerializableChunkDiff diff)
        {
            if (SupportedFileTypes.IsAudioBank(diff.FileName))
                return WadExportMode.Original;

            if (SupportedFileTypes.IsAudio(diff.FileName) && _appSettings.AudioExportFormat == AudioExportFormat.Ogg)
                return WadExportMode.Original;

            return WadExportMode.Smart;
        }

        /// <summary>
        /// Shared extraction engine. Each file is isolated in its own try/catch so a
        /// single failure no longer aborts the whole batch; every diff produces a
        /// result item with its own status, mode and output folder.
        /// </summary>
        private async Task<List<ExtractResultItem>> ExtractDiffsCoreAsync(
            List<SerializableChunkDiff> diffs,
            string oldLolPath,
            string newLolPath,
            CancellationToken cancellationToken,
            Func<SerializableChunkDiff, WadExportMode> modeSelector,
            ExportFormats smartFormats)
        {
            int totalFiles = diffs.Count;
            ExtractionStarted?.Invoke(totalFiles);

            string destinationRootPath = _directoriesCreator.GetNewSubAssetsDownloadedPath();

            _logService.Log($"Starting extraction of {totalFiles} assets.");

            var results = new List<ExtractResultItem>(totalFiles);
            int processed = 0;

            foreach (var diff in diffs)
            {
                processed++;
                ExtractionProgressChanged?.Invoke(processed, totalFiles, diff.FileName);

                if (cancellationToken.IsCancellationRequested)
                {
                    _logService.LogWarning("Extraction was cancelled by the user.");
                    break;
                }

                var result = new ExtractResultItem { Diff = diff };
                try
                {
                    bool useOldSide = diff.Type == ChunkDiffType.Removed;
                    string sourceRootPath = useOldSide ? oldLolPath : newLolPath;
                    string sourceVirtualPath = useOldSide ? diff.OldPath : diff.NewPath;
                    ulong sourcePathHash = useOldSide ? diff.OldPathHash : diff.NewPathHash;
                    if (string.IsNullOrWhiteSpace(sourceVirtualPath))
                        throw new InvalidDataException($"The selected source path is missing for '{diff.FileName}'.");

                    if (string.IsNullOrEmpty(sourceRootPath) && string.IsNullOrEmpty(diff.BackupChunkPath))
                        throw new InvalidOperationException($"No source root or backup chunk is available for '{diff.FileName}'.");

                    string sourceWadFullPath = string.IsNullOrEmpty(sourceRootPath)
                        ? null
                        : PathUtils.ResolveWadPath(sourceRootPath, diff.SourceWadFile);

                    var node = new FileSystemNodeModel(diff.FileName, false, sourceVirtualPath, sourceWadFullPath)
                    {
                        SourceChunkPathHash = sourcePathHash,
                        ChunkDiff = diff,
                        BackupChunkPath = diff.BackupChunkPath,
                        OldPath = diff.Type == ChunkDiffType.Renamed ? diff.OldPath : null
                    };

                    string fileDestinationDirectory;
                    string sourceDirectory = Path.GetDirectoryName(sourceVirtualPath);
                    if (_appSettings.OrganizeExtractedAssets)
                    {
                        fileDestinationDirectory = string.IsNullOrEmpty(sourceDirectory)
                            ? destinationRootPath
                            : Path.Combine(destinationRootPath, sourceDirectory);
                    }
                    else
                    {
                        fileDestinationDirectory = destinationRootPath;
                    }

                    _directoriesCreator.CreateDirectory(fileDestinationDirectory);

                    result.Mode = modeSelector(diff);
                    result.OutputPath = fileDestinationDirectory;
                    bool outputProduced = false;
                    Action<string> onFileExported = _ => outputProduced = true;

                    if (result.Mode == WadExportMode.Original)
                    {
                        await _assetExportService.ExportAsync(node, fileDestinationDirectory, cancellationToken, onFileExported);
                    }
                    else
                    {
                        await _assetExportService.ExportSmartAsync(node, fileDestinationDirectory, null, sourceRootPath, cancellationToken, onFileExported, smartFormats);
                    }

                    if (!outputProduced)
                        throw new IOException($"No output was produced for '{diff.FileName}'.");

                    result.Success = true;
                }
                catch (OperationCanceledException)
                {
                    _logService.LogWarning("Extraction was cancelled by the user.");
                    break;
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.ErrorMessage = ex.Message;
                    _logService.LogError(ex, $"Failed to extract '{diff.FileName}'.");
                }

                results.Add(result);
            }

            int failedCount = results.Count(r => !r.Success);
            string relativePath = Path.Combine("AssetsDownloaded", Path.GetFileName(destinationRootPath));
            if (failedCount > 0)
            {
                _logService.LogWarning($"Extraction finished: {results.Count - failedCount} exported, {failedCount} failed.");
            }
            else
            {
                _logService.LogInteractiveSuccess($"Extraction completed of {results.Count} assets in", destinationRootPath, relativePath);
            }

            await NotifyExtractionCompletedAsync();
            return results;
        }

        public async Task ExtractNodesAsync(
            List<FileSystemNodeModel> nodes,
            string destinationPath,
            ObservableRangeCollection<FileSystemNodeModel> rootNodes,
            string currentRootPath,
            CancellationToken cancellationToken,
            Action<string> onFileSavedCallback = null)
        {
            if (nodes == null || nodes.Count == 0)
            {
                await NotifyExtractionCompletedAsync();
                return;
            }

            int totalFiles = await _assetExportService.CalculateTotalAsync(nodes, rootNodes, currentRootPath, cancellationToken);
            ExtractionStarted?.Invoke(totalFiles);

            _logService.Log($"Starting extraction of {nodes.Count} selected items.");

            try
            {
                await _assetExportService.ExportNodesAsync(
                    nodes,
                    destinationPath,
                    rootNodes,
                    currentRootPath,
                    cancellationToken,
                    (processed, total, currentFile) =>
                    {
                        ExtractionProgressChanged?.Invoke(processed, total, currentFile);
                    },
                    onFileSavedCallback);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "An unexpected error occurred during extraction.");
                throw;
            }
            finally
            {
                await NotifyExtractionCompletedAsync();
            }
        }

        public async Task SaveNodesAsync(
            List<FileSystemNodeModel> nodes,
            string destinationPath,
            ObservableRangeCollection<FileSystemNodeModel> rootNodes,
            string currentRootPath,
            CancellationToken cancellationToken,
            Action<string> onFileSavedCallback = null)
        {
            if (nodes == null || nodes.Count == 0)
            {
                await NotifySavingCompletedAsync();
                return;
            }

            int totalFiles = await _assetExportService.CalculateTotalSmartAsync(nodes, rootNodes, currentRootPath, cancellationToken);
            SavingStarted?.Invoke(totalFiles);

            _logService.Log($"Starting save of {nodes.Count} selected items.");

            try
            {
                int processedCount = 0;
                foreach (var node in nodes)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    await _assetExportService.ExportSmartAsync(node, destinationPath, rootNodes, currentRootPath, cancellationToken,
                        (path) =>
                        {
                            processedCount++;
                            string fileName = Path.GetFileName(path);
                            SavingProgressChanged?.Invoke(processedCount, totalFiles, fileName);
                            onFileSavedCallback?.Invoke(path);
                        }, ExportFormats.ExplorerSmart(_appSettings.AudioExportFormat));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "An unexpected error occurred during save.");
                throw;
            }
            finally
            {
                await NotifySavingCompletedAsync();
            }
        }

        private async Task NotifyExtractionCompletedAsync()
        {
            if (ExtractionCompleted is not null)
                await ExtractionCompleted();
        }

        private async Task NotifySavingCompletedAsync()
        {
            if (SavingCompleted is not null)
                await SavingCompleted();
        }

        #endregion
    }
}
