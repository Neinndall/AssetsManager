using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Core;
using AssetsManager.Services.Explorer;
using AssetsManager.Services.Audio;
using AssetsManager.Services.Formatting;
using AssetsManager.Services.Parsers;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Explorer;
using AssetsManager.Views.Models.Wad;
using AssetsManager.Views.Models.Audio;
using AssetsManager.Views.Models.Settings;
using AssetsManager.Utils.Framework;

namespace AssetsManager.Services.Downloads
{
    public class ExtractionService
    {
        private readonly AppSettings _appSettings;
        private readonly LogService _logService;
        private readonly DirectoriesCreator _directoriesCreator;
        private readonly WadExportService _wadExportService;
        private readonly WadContentProvider _wadContentProvider;
        private readonly ContentFormatterService _contentFormatterService;
        private readonly AudioBankService _audioBankService;
        private readonly AudioBankLinkerService _audioBankLinkerService;
        private readonly AudioConversionService _audioConversionService;
        private readonly WadNodeLoaderService _wadNodeLoaderService;

        private readonly record struct ExportFormats(
            WadExportMode AudioMode,
            AudioExportFormat AudioTarget,
            ImageExportFormat Image,
            DataExportFormat Data)
        {
            public static ExportFormats ExplorerSmart(AudioExportFormat audio) => new(
                WadExportMode.Smart, audio, ImageExportFormat.Png, DataExportFormat.Json);
        }

        public event Action<int> ExtractionStarted;
        public event Action<int, int, string> ExtractionProgressChanged;
        public event Action ExtractionCompleted;

        public event Action<int> SavingStarted;
        public event Action<int, int, string> SavingProgressChanged;
        public event Action SavingCompleted;

        public ExtractionService(
            AppSettings appSettings,
            LogService logService,
            DirectoriesCreator directoriesCreator,
            WadExportService wadExportService,
            WadContentProvider wadContentProvider,
            ContentFormatterService contentFormatterService,
            AudioBankService audioBankService,
            AudioBankLinkerService audioBankLinkerService,
            AudioConversionService audioConversionService,
            WadNodeLoaderService wadNodeLoaderService)
        {
            _appSettings = appSettings;
            _logService = logService;
            _directoriesCreator = directoriesCreator;
            _wadExportService = wadExportService;
            _wadContentProvider = wadContentProvider;
            _contentFormatterService = contentFormatterService;
            _audioBankService = audioBankService;
            _audioBankLinkerService = audioBankLinkerService;
            _audioConversionService = audioConversionService;
            _wadNodeLoaderService = wadNodeLoaderService;
        }

        #region Public Interface (Save, Extract, Extract New Assets)

        public async Task ExtractNewFilesFromComparisonAsync(
            List<SerializableChunkDiff> allDiffs,
            string newLolPath,
            CancellationToken cancellationToken)
        {
            var newDiffs = allDiffs.Where(d => d.Type == ChunkDiffType.New).ToList();

            if (!newDiffs.Any())
            {
                _logService.Log("No new assets to extract from the comparison.");
                ExtractionCompleted?.Invoke();
                return;
            }

            int totalFiles = newDiffs.Count;

            ExtractionStarted?.Invoke(totalFiles);

            string destinationRootPath = _directoriesCreator.GetNewSubAssetsDownloadedPath();
            int extractedCount = 0;
            var exportFormats = new ExportFormats(
                WadExportMode.Original,
                AudioExportFormat.Ogg,
                _appSettings.ImageExportFormat,
                _appSettings.DataExportFormat);

            _logService.Log($"Starting extraction of {totalFiles} new assets.");

            try
            {
                foreach (var diff in newDiffs)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    extractedCount++;
                    string progressMessage = $"{diff.FileName}";
                    ExtractionProgressChanged?.Invoke(extractedCount, totalFiles, progressMessage);

                    string sourceWadFullPath = PathUtils.ResolveWadPath(newLolPath, diff.SourceWadFile);
                    var node = new FileSystemNodeModel(diff.FileName, false, diff.NewPath, sourceWadFullPath)
                    {
                        SourceChunkPathHash = diff.NewPathHash,
                        Status = DiffStatus.New
                    };

                    string fileDestinationDirectory;
                    if (_appSettings.OrganizeExtractedAssets)
                    {
                        fileDestinationDirectory = Path.Combine(destinationRootPath, Path.GetDirectoryName(diff.NewPath));
                    }
                    else
                    {
                        fileDestinationDirectory = destinationRootPath;
                    }

                    _directoriesCreator.CreateDirectory(fileDestinationDirectory);

                    string ext = Path.GetExtension(diff.FileName).ToLower();
                    var mode = (ext == ".bnk" || ext == ".wpk") ? WadExportMode.Original : WadExportMode.Smart;

                    if (mode == WadExportMode.Original)
                    {
                        await _wadExportService.ExportAsync(node, fileDestinationDirectory, cancellationToken, null);
                    }
                    else
                    {
                        await ExportSmartAsync(node, fileDestinationDirectory, null, newLolPath, cancellationToken, null, exportFormats);
                    }
                }

                string relativePath = Path.Combine("AssetsDownloaded", Path.GetFileName(destinationRootPath));
                _logService.LogInteractiveSuccess($"Extraction completed of {extractedCount} assets in", destinationRootPath, relativePath);
            }
            catch (OperationCanceledException)
            {
                _logService.LogWarning("Extraction was cancelled by the user.");
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "An unexpected error occurred during extraction.");
            }
            finally
            {
                ExtractionCompleted?.Invoke();
            }
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
                ExtractionCompleted?.Invoke();
                return;
            }

            int totalFiles = await _wadExportService.CalculateTotalAsync(nodes, rootNodes, currentRootPath, cancellationToken);
            ExtractionStarted?.Invoke(totalFiles);

            _logService.Log($"Starting extraction of {nodes.Count} selected items.");

            try
            {
                await _wadExportService.ExportNodesAsync(
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
                ExtractionCompleted?.Invoke();
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
                SavingCompleted?.Invoke();
                return;
            }

            int totalFiles = await CalculateTotalSmartAsync(nodes, rootNodes, currentRootPath, cancellationToken);
            SavingStarted?.Invoke(totalFiles);

            _logService.Log($"Starting save of {nodes.Count} selected items.");

            try
            {
                int processedCount = 0;
                foreach (var node in nodes)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    await ExportSmartAsync(node, destinationPath, rootNodes, currentRootPath, cancellationToken,
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
                SavingCompleted?.Invoke();
            }
        }

        #endregion

        #region Traversal & Smart Total Calculation

        private async Task<int> CalculateTotalSmartAsync(IEnumerable<FileSystemNodeModel> nodes, ObservableRangeCollection<FileSystemNodeModel> rootNodes, string currentRootPath, CancellationToken cancellationToken)
        {
            int count = 0;
            foreach (var node in nodes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (node.Type == NodeType.VirtualFile || node.Type == NodeType.RealFile || node.Type == NodeType.WemFile)
                {
                    count++;
                }
                else if (node.Type == NodeType.SoundBank)
                {
                    if (!SupportedFileTypes.IsExpandableAudioBank(node.Name) || node.Children == null || node.Children.Count == 0) continue;

                    if (node.Children.Count > 1 || (node.Children.Count == 1 && node.Children[0].Name != "Loading..."))
                    {
                        count += CountSoundsInAudioTree(node.Children);
                    }
                    else
                    {
                        var linkedBank = await _audioBankLinkerService.LinkAudioBankAsync(node, rootNodes, currentRootPath);
                        if (linkedBank != null)
                        {
                            byte[] wpkData = linkedBank.WpkNode != null ? await _wadContentProvider.GetVirtualFileBytesAsync(linkedBank.WpkNode, cancellationToken) : null;
                            byte[] audioBnkData = linkedBank.AudioBnkNode != null ? await _wadContentProvider.GetVirtualFileBytesAsync(linkedBank.AudioBnkNode, cancellationToken) : null;
                            byte[] eventsData = linkedBank.EventsBnkNode != null ? await _wadContentProvider.GetVirtualFileBytesAsync(linkedBank.EventsBnkNode, cancellationToken) : null;

                            List<AudioEventNode> audioTree;
                            if (linkedBank.BinData != null)
                                audioTree = _audioBankService.ParseAudioBank(wpkData, audioBnkData, eventsData, linkedBank.BinData, linkedBank.BaseName, linkedBank.BinType);
                            else
                                audioTree = _audioBankService.ParseGenericAudioBank(wpkData, audioBnkData, eventsData);

                            int soundsCount = 0;
                            foreach (var eventNode in audioTree)
                            {
                                if (eventNode.IsTechnicalNode) continue;
                                soundsCount += eventNode.Sounds.Count;
                                foreach (var containerNode in eventNode.Containers)
                                {
                                    soundsCount += containerNode.Sounds.Count;
                                }
                            }

                            count += (soundsCount > 0) ? soundsCount : 1;
                        }
                        else count++;
                    }
                }
                else if (node.Type == NodeType.AudioEvent || node.Type == NodeType.VirtualDirectory || node.Type == NodeType.RealDirectory || node.Type == NodeType.WadFile)
                {
                    if ((node.Type == NodeType.VirtualDirectory || node.Type == NodeType.WadFile) &&
                        node.Children.Count == 1 && node.Children[0].Name == "Loading...")
                    {
                        var loadedChildren = await _wadNodeLoaderService.LoadChildrenAsync(node, cancellationToken);
                        node.Children.ReplaceRange(loadedChildren);
                    }
                    count += await CalculateTotalSmartAsync(node.Children, rootNodes, currentRootPath, cancellationToken);
                }
            }
            return count;
        }

        private int CountSoundsInAudioTree(IEnumerable<FileSystemNodeModel> nodes)
        {
            int count = 0;
            foreach (var node in nodes)
            {
                if (node.Type == NodeType.WemFile) count++;
                else if (node.Children != null) count += CountSoundsInAudioTree(node.Children);
            }
            return count;
        }

        #endregion

        #region Smart Export Logic & Handlers

        private async Task ExportSmartAsync(FileSystemNodeModel node, string destinationPath, ObservableRangeCollection<FileSystemNodeModel> rootNodes, string currentRootPath, CancellationToken cancellationToken, Action<string> onFileSavedCallback, ExportFormats formats)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (node.Type == NodeType.WadFile || node.Type == NodeType.VirtualDirectory || node.Type == NodeType.RealDirectory)
            {
                string cleanName = PathUtils.GetLogName(node.Name);
                string currentDestinationPath = Path.Combine(destinationPath, PathUtils.SanitizeName(cleanName));
                _directoriesCreator.CreateDirectory(currentDestinationPath);

                if (node.Children.Count == 1 && node.Children[0].Name == "Loading...")
                {
                    var loadedChildren = await _wadNodeLoaderService.LoadChildrenAsync(node, cancellationToken);
                    node.Children.ReplaceRange(loadedChildren);
                }

                foreach (var child in node.Children)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await ExportSmartAsync(child, currentDestinationPath, rootNodes, currentRootPath, cancellationToken, onFileSavedCallback, formats);
                }
                return;
            }

            if (node.Type == NodeType.AudioEvent)
            {
                if (node.IsTechnicalNode) return;
                if (formats.AudioMode == WadExportMode.Original) return;

                string eventPath = Path.Combine(destinationPath, PathUtils.SanitizeName(node.Name));
                _directoriesCreator.CreateDirectory(eventPath);

                async Task ExportSoundsRecursiveAsync(FileSystemNodeModel parentNode, string currentPath)
                {
                    foreach (var childNode in parentNode.Children)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (childNode.Type == NodeType.WemFile)
                        {
                            await HandleWemFileAsync(childNode, currentPath, formats.AudioTarget, cancellationToken, onFileSavedCallback);
                        }
                        else if (childNode.Type == NodeType.VirtualDirectory)
                        {
                            string subFolderPath = Path.Combine(currentPath, PathUtils.SanitizeName(childNode.Name));
                            _directoriesCreator.CreateDirectory(subFolderPath);
                            await ExportSoundsRecursiveAsync(childNode, subFolderPath);
                        }
                    }
                }

                await ExportSoundsRecursiveAsync(node, eventPath);
                return;
            }

            // Single File Smart Handling
            string extension = Path.GetExtension(node.Name).ToLower();
            switch (extension)
            {
                case ".wpk":
                case ".bnk":
                    if (SupportedFileTypes.IsExpandableAudioBank(node.Name) && node.Children.Count > 0)
                    {
                        if (formats.AudioMode == WadExportMode.Smart)
                        {
                            await HandleAudioBankFile(node, destinationPath, rootNodes, currentRootPath, formats.AudioTarget, cancellationToken, onFileSavedCallback);
                        }
                        else
                        {
                            await _wadExportService.ExportAsync(node, destinationPath, cancellationToken, onFileSavedCallback);
                        }
                    }
                    break;

                case ".tex":
                case ".dds":
                    await HandleTextureFile(node, destinationPath, formats.Image, cancellationToken, onFileSavedCallback);
                    break;

                case ".bin":
                case ".stringtable":
                case ".css":
                case ".troybin":
                case ".preload":
                    await HandleDataFile(node, destinationPath, extension.TrimStart('.'), formats.Data, cancellationToken, onFileSavedCallback);
                    break;

                case ".luabin64":
                    await HandleLuaFile(node, destinationPath, formats.Data, cancellationToken, onFileSavedCallback);
                    break;

                case ".js":
                    await HandleJsFile(node, destinationPath, formats.Data, cancellationToken, onFileSavedCallback);
                    break;

                case ".wem":
                    if (formats.AudioMode == WadExportMode.Smart)
                    {
                        await HandleWemFileAsync(node, destinationPath, formats.AudioTarget, cancellationToken, onFileSavedCallback);
                    }
                    else
                    {
                        await _wadExportService.ExportAsync(node, destinationPath, cancellationToken, onFileSavedCallback);
                    }
                    break;

                case ".ogg":
                    if (formats.AudioMode == WadExportMode.Smart)
                    {
                        await HandleStandardAudioFileAsync(node, destinationPath, formats.AudioTarget, cancellationToken, onFileSavedCallback);
                    }
                    else
                    {
                        await _wadExportService.ExportAsync(node, destinationPath, cancellationToken, onFileSavedCallback);
                    }
                    break;

                default:
                    // Fall back to raw export via WadExportService
                    await _wadExportService.ExportAsync(node, destinationPath, cancellationToken, onFileSavedCallback);
                    break;
            }
        }

        private async Task HandleTextureFile(FileSystemNodeModel node, string destinationPath, ImageExportFormat format, CancellationToken cancellationToken, Action<string> onFileSavedCallback)
        {
            if (format == ImageExportFormat.Original)
            {
                await _wadExportService.ExportAsync(node, destinationPath, cancellationToken, onFileSavedCallback);
                return;
            }

            var fileBytes = await _wadContentProvider.GetVirtualFileBytesAsync(node, cancellationToken);
            if (fileBytes == null) return;

            using (var memoryStream = new MemoryStream(fileBytes))
            {
                var bitmapSource = TextureUtils.LoadTexture(memoryStream, Path.GetExtension(node.Name));
                if (bitmapSource != null)
                {
                    await TextureUtils.SaveBitmapSourceAsImageAsync(bitmapSource, node.Name, destinationPath, format, onFileSavedCallback, cancellationToken);
                }
            }
        }

        private async Task HandleDataFile(FileSystemNodeModel node, string destinationPath, string type, DataExportFormat format, CancellationToken cancellationToken, Action<string> onFileSavedCallback)
        {
            if (format == DataExportFormat.Original)
            {
                await _wadExportService.ExportAsync(node, destinationPath, cancellationToken, onFileSavedCallback);
                return;
            }

            var fileBytes = await _wadContentProvider.GetVirtualFileBytesAsync(node, cancellationToken);
            if (fileBytes == null) return;

            var formattedContent = await _contentFormatterService.GetFormattedStringAsync(type, fileBytes);
            string fileName = Path.ChangeExtension(node.Name, ".json");
            string filePath = PathUtils.GetUniqueFilePath(destinationPath, fileName);

            await File.WriteAllTextAsync(filePath, formattedContent, cancellationToken);
            onFileSavedCallback?.Invoke(filePath);
        }

        private async Task HandleJsFile(FileSystemNodeModel node, string destinationPath, DataExportFormat format, CancellationToken cancellationToken, Action<string> onFileSavedCallback)
        {
            if (format == DataExportFormat.Original)
            {
                await _wadExportService.ExportAsync(node, destinationPath, cancellationToken, onFileSavedCallback);
                return;
            }

            var fileBytes = await _wadContentProvider.GetVirtualFileBytesAsync(node, cancellationToken);
            if (fileBytes == null) return;

            var formattedContent = await _contentFormatterService.GetFormattedStringAsync("js", fileBytes);
            string filePath = PathUtils.GetUniqueFilePath(destinationPath, node.Name);

            await File.WriteAllTextAsync(filePath, formattedContent, cancellationToken);
            onFileSavedCallback?.Invoke(filePath);
        }

        private async Task HandleLuaFile(FileSystemNodeModel node, string destinationPath, DataExportFormat format, CancellationToken cancellationToken, Action<string> onFileSavedCallback)
        {
            if (format == DataExportFormat.Original)
            {
                await _wadExportService.ExportAsync(node, destinationPath, cancellationToken, onFileSavedCallback);
                return;
            }

            var fileBytes = await _wadContentProvider.GetVirtualFileBytesAsync(node, cancellationToken);
            if (fileBytes == null) return;

            var formattedContent = await _contentFormatterService.GetFormattedStringAsync("luabin64", fileBytes);
            string fileName = Path.ChangeExtension(node.Name, ".json");
            string filePath = PathUtils.GetUniqueFilePath(destinationPath, fileName);

            await File.WriteAllTextAsync(filePath, formattedContent, cancellationToken);
            onFileSavedCallback?.Invoke(filePath);
        }

        private async Task HandleStandardAudioFileAsync(FileSystemNodeModel node, string destinationPath, AudioExportFormat targetFormat, CancellationToken cancellationToken, Action<string> onFileSavedCallback)
        {
            var fileBytes = await _wadContentProvider.GetVirtualFileBytesAsync(node, cancellationToken);
            if (fileBytes == null) return;

            string currentExtension = Path.GetExtension(node.Name).ToLower();
            
            if (targetFormat != AudioExportFormat.Ogg || currentExtension != ".ogg")
            {
                byte[] convertedData = await _audioConversionService.ConvertAudioToFormatAsync(fileBytes, ".wem", targetFormat, cancellationToken);
                if (convertedData != null)
                {
                    string extension = targetFormat switch { AudioExportFormat.Wav => ".wav", AudioExportFormat.Mp3 => ".mp3", _ => ".ogg" };
                    string filePath = PathUtils.GetUniqueFilePath(destinationPath, Path.ChangeExtension(node.Name, extension));
                    await File.WriteAllBytesAsync(filePath, convertedData, cancellationToken);
                    onFileSavedCallback?.Invoke(filePath);
                    return;
                }
            }

            string fallbackPath = PathUtils.GetUniqueFilePath(destinationPath, node.Name);
            await File.WriteAllBytesAsync(fallbackPath, fileBytes, cancellationToken);
            onFileSavedCallback?.Invoke(fallbackPath);
        }

        private async Task HandleWemFileAsync(FileSystemNodeModel node, string destinationPath, AudioExportFormat format, CancellationToken cancellationToken, Action<string> onFileSavedCallback)
        {
            var wemData = await _wadContentProvider.GetWemFileBytesAsync(node, cancellationToken);
            if (wemData == null) return;

            byte[] convertedData = await _audioConversionService.ConvertAudioToFormatAsync(wemData, ".wem", format, cancellationToken);
            if (convertedData != null)
            {
                string extension = format switch { AudioExportFormat.Wav => ".wav", AudioExportFormat.Mp3 => ".mp3", _ => ".ogg" };
                string filePath = PathUtils.GetUniqueFilePath(destinationPath, Path.ChangeExtension(node.Name, extension));
                await File.WriteAllBytesAsync(filePath, convertedData, cancellationToken);
                onFileSavedCallback?.Invoke(filePath);
            }
        }

        private async Task HandleAudioBankFile(FileSystemNodeModel node, string destinationPath, ObservableRangeCollection<FileSystemNodeModel> rootNodes, string currentRootPath, AudioExportFormat format, CancellationToken cancellationToken, Action<string> onFileSavedCallback)
        {
            var linkedBank = await _audioBankLinkerService.LinkAudioBankAsync(node, rootNodes, currentRootPath);
            if (linkedBank == null) return;

            string audioBankPath = Path.Combine(destinationPath, PathUtils.SanitizeName(Path.GetFileNameWithoutExtension(node.Name)));
            _directoriesCreator.CreateDirectory(audioBankPath);

            var eventsData = linkedBank.EventsBnkNode != null ? await _wadContentProvider.GetVirtualFileBytesAsync(linkedBank.EventsBnkNode, cancellationToken) : null;
            byte[] wpkData = linkedBank.WpkNode != null ? await _wadContentProvider.GetVirtualFileBytesAsync(linkedBank.WpkNode, cancellationToken) : null;
            byte[] audioBnkFileData = linkedBank.AudioBnkNode != null ? await _wadContentProvider.GetVirtualFileBytesAsync(linkedBank.AudioBnkNode, cancellationToken) : null;
            
            List<AudioEventNode> audioTree;
            if (linkedBank.BinData != null)
                audioTree = _audioBankService.ParseAudioBank(wpkData, audioBnkFileData, eventsData, linkedBank.BinData, linkedBank.BaseName, linkedBank.BinType);
            else
                audioTree = _audioBankService.ParseGenericAudioBank(wpkData, audioBnkFileData, eventsData);

            async Task ExportSoundNodeAsync(WemFileNode soundNode, string eventPath)
            {
                byte[] wemData = null;
                if (soundNode.Source == AudioSourceType.Wpk && wpkData != null)
                {
                    wemData = wpkData.AsSpan((int)soundNode.Offset, (int)soundNode.Size).ToArray();
                }
                else if (audioBnkFileData != null)
                {
                    wemData = audioBnkFileData.AsSpan((int)soundNode.Offset, (int)soundNode.Size).ToArray();
                }

                if (wemData != null)
                {
                    byte[] convertedData = await _audioConversionService.ConvertAudioToFormatAsync(wemData, ".wem", format, cancellationToken);
                    if (convertedData != null)
                    {
                        string extension = format switch { AudioExportFormat.Wav => ".wav", AudioExportFormat.Mp3 => ".mp3", _ => ".ogg" };
                        string filePath = PathUtils.GetUniqueFilePath(eventPath, Path.ChangeExtension(soundNode.Name, extension));
                        await File.WriteAllBytesAsync(filePath, convertedData, cancellationToken);
                        onFileSavedCallback?.Invoke(filePath);
                    }
                }
            }

            foreach (var eventNode in audioTree)
            {
                if (eventNode.IsTechnicalNode) continue;

                string eventPath = Path.Combine(audioBankPath, PathUtils.SanitizeName(eventNode.Name));
                _directoriesCreator.CreateDirectory(eventPath);

                // Export root-level sounds
                foreach (var soundNode in eventNode.Sounds)
                {
                    await ExportSoundNodeAsync(soundNode, eventPath);
                }

                // Export sounds in sub-containers (families)
                foreach (var containerNode in eventNode.Containers)
                {
                    string containerPath = Path.Combine(eventPath, PathUtils.SanitizeName(containerNode.Name));
                    _directoriesCreator.CreateDirectory(containerPath);
                    foreach (var soundNode in containerNode.Sounds)
                    {
                        await ExportSoundNodeAsync(soundNode, containerPath);
                    }
                }
            }
        }

        #endregion
    }
}
