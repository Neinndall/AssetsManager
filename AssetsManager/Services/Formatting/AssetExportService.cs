using System;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Threading;
using AssetsManager.Utils;
using AssetsManager.Utils.Framework;
using AssetsManager.Services.Core;
using AssetsManager.Services.Explorer;
using AssetsManager.Services.Audio;
using AssetsManager.Services.Formatting;
using AssetsManager.Views.Models.Explorer;
using AssetsManager.Views.Models.Audio;
using AssetsManager.Views.Models.Settings;

namespace AssetsManager.Services.Formatting
{
    /// <summary>
    /// Defines how the assets should be exported to the disk.
    /// </summary>
    public enum WadExportMode
    {
        /// <summary>
        /// Preserves the original format (raw bytes from the WAD/Chunk).
        /// </summary>
        Original,

        /// <summary>
        /// Performs smart conversions (e.g., textures to PNG, audio to MP3/OGG).
        /// </summary>
        Smart
    }

    /// <summary>
    /// Per-type target formats for a smart export (audio, texture, data).
    /// </summary>
    public readonly record struct ExportFormats(
        WadExportMode AudioMode,
        AudioExportFormat AudioTarget,
        ImageExportFormat Image,
        DataExportFormat Data)
    {
        public static ExportFormats ExplorerSmart(AudioExportFormat audio) => new(
            WadExportMode.Smart, audio, ImageExportFormat.Png, DataExportFormat.Json);
    }

    /// <summary>
    /// Unified asset export service. EXTRACT is just EXPORT in Original mode
    /// (raw bytes, no conversion); SAVE/SMART applies per-type conversions.
    /// </summary>
    public class AssetExportService
    {
        private readonly LogService _logService;
        private readonly WadContentProvider _wadContentProvider;
        private readonly WadNodeLoaderService _wadNodeLoaderService;
        private readonly DirectoriesCreator _directoriesCreator;
        private readonly AudioBankService _audioBankService;
        private readonly AudioBankLinkerService _audioBankLinkerService;
        private readonly AudioConversionService _audioConversionService;
        private readonly ContentFormatterService _contentFormatterService;

        public AssetExportService(
            LogService logService,
            WadContentProvider wadContentProvider,
            WadNodeLoaderService wadNodeLoaderService,
            DirectoriesCreator directoriesCreator,
            AudioBankService audioBankService,
            AudioBankLinkerService audioBankLinkerService,
            AudioConversionService audioConversionService,
            ContentFormatterService contentFormatterService)
        {
            _logService = logService;
            _wadContentProvider = wadContentProvider;
            _wadNodeLoaderService = wadNodeLoaderService;
            _directoriesCreator = directoriesCreator;
            _audioBankService = audioBankService;
            _audioBankLinkerService = audioBankLinkerService;
            _audioConversionService = audioConversionService;
            _contentFormatterService = contentFormatterService;
        }

        #region Raw Export (EXTRACT)

        public async Task<int> CalculateTotalAsync(
            IEnumerable<FileSystemNodeModel> nodes,
            ObservableRangeCollection<FileSystemNodeModel> rootNodes,
            string currentRootPath,
            CancellationToken cancellationToken)
        {
            int count = 0;
            foreach (var node in nodes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (node.Type == NodeType.VirtualFile || node.Type == NodeType.RealFile || node.Type == NodeType.WemFile || node.Type == NodeType.SoundBank)
                {
                    count++;
                }
                else if (node.Type == NodeType.AudioEvent || node.Type == NodeType.VirtualDirectory || node.Type == NodeType.RealDirectory || node.Type == NodeType.WadFile)
                {
                    if ((node.Type == NodeType.VirtualDirectory || node.Type == NodeType.WadFile) &&
                        node.Children.Count == 1 && node.Children[0].Name == "Loading...")
                    {
                        var loadedChildren = await _wadNodeLoaderService.LoadChildrenAsync(node, cancellationToken);
                        node.Children.ReplaceRange(loadedChildren);
                    }
                    count += await CalculateTotalAsync(node.Children, rootNodes, currentRootPath, cancellationToken);
                }
            }
            return count;
        }

        public async Task<int> ExportNodesAsync(
            List<FileSystemNodeModel> nodes,
            string destinationPath,
            ObservableRangeCollection<FileSystemNodeModel> rootNodes,
            string currentRootPath,
            CancellationToken cancellationToken,
            Action<int, int, string> onProgress = null,
            Action<string> onFileSaved = null)
        {
            int totalFiles = await CalculateTotalAsync(nodes, rootNodes, currentRootPath, cancellationToken);
            onProgress?.Invoke(0, totalFiles, null);

            int processedCount = 0;
            foreach (var node in nodes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await ExportAsync(node, destinationPath, cancellationToken,
                    (path) =>
                    {
                        processedCount++;
                        string fileName = Path.GetFileName(path);
                        onProgress?.Invoke(processedCount, totalFiles, fileName);
                        onFileSaved?.Invoke(path);
                    });
            }

            return processedCount;
        }

        /// <summary>Raw export (EXTRACT): writes the original bytes, no conversion.</summary>
        public async Task ExportAsync(
            FileSystemNodeModel node,
            string destinationPath,
            CancellationToken cancellationToken,
            Action<string> onFileSavedCallback = null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 1. Directory Traversal
            if (node.Type == NodeType.WadFile || node.Type == NodeType.VirtualDirectory || node.Type == NodeType.RealDirectory)
            {
                string cleanName = PathUtils.GetLogName(node.Name);
                string currentDestinationPath = Path.Combine(destinationPath, PathUtils.SanitizeName(cleanName));
                _directoriesCreator.CreateDirectory(currentDestinationPath);

                // Ensure children are loaded
                if (node.Children.Count == 1 && node.Children[0].Name == "Loading...")
                {
                    var loadedChildren = await _wadNodeLoaderService.LoadChildrenAsync(node, cancellationToken);
                    node.Children.ReplaceRange(loadedChildren);
                }

                foreach (var child in node.Children)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await ExportAsync(child, currentDestinationPath, cancellationToken, onFileSavedCallback);
                }
                return;
            }

            // 2. Single Raw File Handling
            await HandleRawFileExtractionAsync(node, destinationPath, cancellationToken, onFileSavedCallback);
        }

        private async Task HandleRawFileExtractionAsync(
            FileSystemNodeModel node,
            string destinationPath,
            CancellationToken cancellationToken,
            Action<string> onFileSavedCallback)
        {
            cancellationToken.ThrowIfCancellationRequested();

            byte[] fileBytes;
            if (node.Type == NodeType.RealFile ||
                (node.Type == NodeType.SoundBank && File.Exists(node.VirtualPath)))
                fileBytes = await File.ReadAllBytesAsync(node.VirtualPath, cancellationToken);
            else if (node.Type == NodeType.WemFile)
                fileBytes = await _wadContentProvider.GetWemFileBytesAsync(node, cancellationToken);
            else
                fileBytes = await _wadContentProvider.GetVirtualFileBytesAsync(node, cancellationToken);

            if (fileBytes == null) return;

            string fileName = node.Name;
            string filePath = PathUtils.GetUniqueFilePath(destinationPath, fileName);
            await File.WriteAllBytesAsync(filePath, fileBytes, cancellationToken);
            onFileSavedCallback?.Invoke(filePath);
        }

        #endregion

        #region Smart Export (SAVE)

        public async Task<int> CalculateTotalSmartAsync(
            IEnumerable<FileSystemNodeModel> nodes,
            ObservableRangeCollection<FileSystemNodeModel> rootNodes,
            string currentRootPath,
            CancellationToken cancellationToken)
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
                                audioTree = _audioBankService.ParseAudioBank(wpkData, audioBnkData, eventsData, linkedBank.BinData, linkedBank.BaseName);
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

        /// <summary>
        /// Smart export (SAVE): converts per file type using the given formats.
        /// </summary>
        public async Task ExportSmartAsync(
            FileSystemNodeModel node,
            string destinationPath,
            ObservableRangeCollection<FileSystemNodeModel> rootNodes,
            string currentRootPath,
            CancellationToken cancellationToken,
            Action<string> onFileSavedCallback,
            ExportFormats formats)
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
                            await ExportAsync(node, destinationPath, cancellationToken, onFileSavedCallback);
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
                        await ExportAsync(node, destinationPath, cancellationToken, onFileSavedCallback);
                    }
                    break;

                case ".ogg":
                    if (formats.AudioMode == WadExportMode.Smart)
                    {
                        await HandleStandardAudioFileAsync(node, destinationPath, formats.AudioTarget, cancellationToken, onFileSavedCallback);
                    }
                    else
                    {
                        await ExportAsync(node, destinationPath, cancellationToken, onFileSavedCallback);
                    }
                    break;

                default:
                    // Fall back to raw export
                    await ExportAsync(node, destinationPath, cancellationToken, onFileSavedCallback);
                    break;
            }
        }

        private async Task HandleTextureFile(FileSystemNodeModel node, string destinationPath, ImageExportFormat format, CancellationToken cancellationToken, Action<string> onFileSavedCallback)
        {
            if (format == ImageExportFormat.Original)
            {
                await ExportAsync(node, destinationPath, cancellationToken, onFileSavedCallback);
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
                await ExportAsync(node, destinationPath, cancellationToken, onFileSavedCallback);
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
                await ExportAsync(node, destinationPath, cancellationToken, onFileSavedCallback);
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
                await ExportAsync(node, destinationPath, cancellationToken, onFileSavedCallback);
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
            byte[] wemData;
            if (node.Type == NodeType.WemFile)
            {
                wemData = await _wadContentProvider.GetWemFileBytesAsync(node, cancellationToken);
            }
            else if (node.Type == NodeType.RealFile)
            {
                wemData = await File.ReadAllBytesAsync(node.VirtualPath, cancellationToken);
            }
            else
            {
                wemData = await _wadContentProvider.GetVirtualFileBytesAsync(node, cancellationToken);
            }

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
                audioTree = _audioBankService.ParseAudioBank(wpkData, audioBnkFileData, eventsData, linkedBank.BinData, linkedBank.BaseName);
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
