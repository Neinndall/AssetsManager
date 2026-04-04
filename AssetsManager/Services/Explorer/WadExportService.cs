using System;
using System.IO;
using System.Windows.Media.Imaging;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Threading;
using System.Linq;
using LeagueToolkit.Toolkit;
using AssetsManager.Utils;
using AssetsManager.Utils.Framework;
using AssetsManager.Services.Core;
using AssetsManager.Views.Models.Audio;
using AssetsManager.Services.Parsers;
using AssetsManager.Services.Formatting;
using AssetsManager.Services.Audio;
using AssetsManager.Views.Models.Explorer;
using AssetsManager.Views.Models.Wad;
using AssetsManager.Views.Models.Settings;

namespace AssetsManager.Services.Explorer
{
    /// <summary>
    /// Unified service for exporting assets to the disk.
    /// Handles both raw extraction (Original) and smart conversions (Smart).
    /// </summary>
    public class WadExportService
    {
        private readonly LogService _logService;
        private readonly WadContentProvider _wadContentProvider;
        private readonly ContentFormatterService _contentFormatterService;
        private readonly AudioBankService _audioBankService;
        private readonly AudioBankLinkerService _audioBankLinkerService;
        private readonly AudioConversionService _audioConversionService;
        private readonly AppSettings _appSettings;
        private readonly WadNodeLoaderService _wadNodeLoaderService;
        private readonly DirectoriesCreator _directoriesCreator;

        public WadExportService(
            LogService logService,
            WadContentProvider wadContentProvider,
            ContentFormatterService contentFormatterService,
            AudioBankService audioBankService,
            AudioBankLinkerService audioBankLinkerService,
            AudioConversionService audioConversionService,
            AppSettings appSettings,
            WadNodeLoaderService wadNodeLoaderService,
            DirectoriesCreator directoriesCreator)
        {
            _logService = logService;
            _wadContentProvider = wadContentProvider;
            _contentFormatterService = contentFormatterService;
            _audioBankService = audioBankService;
            _audioBankLinkerService = audioBankLinkerService;
            _audioConversionService = audioConversionService;
            _appSettings = appSettings;
            _wadNodeLoaderService = wadNodeLoaderService;
            _directoriesCreator = directoriesCreator;
        }

        #region Traversal & Total Calculation

        public async Task<int> CalculateTotalAsync(IEnumerable<FileSystemNodeModel> nodes, ObservableRangeCollection<FileSystemNodeModel> rootNodes, string currentRootPath, WadExportMode mode, CancellationToken cancellationToken)
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
                    if (mode == WadExportMode.Smart)
                    {
                        // Smart redundancy check for audio banks
                        if (SupportedFileTypes.IsExpandableAudioBank(node.Name) && node.Children.Count == 0) continue;

                        if (node.Children.Count > 1 || (node.Children.Count == 1 && node.Children[0].Name != "Loading..."))
                        {
                            count += CountSoundsInAudioTree(node.Children);
                        }
                        else if (SupportedFileTypes.IsExpandableAudioBank(node.Name))
                        {
                            var linkedBank = await _audioBankLinkerService.LinkAudioBankAsync(node, rootNodes, currentRootPath);
                            if (linkedBank != null)
                            {
                                byte[] wpkData = linkedBank.WpkNode != null ? await _wadContentProvider.GetVirtualFileBytesAsync(linkedBank.WpkNode, cancellationToken) : null;
                                byte[] audioBnkData = linkedBank.AudioBnkNode != null ? await _wadContentProvider.GetVirtualFileBytesAsync(linkedBank.AudioBnkNode, cancellationToken) : null;
                                int soundsCount = _audioBankService.GetSoundCount(wpkData, audioBnkData);
                                count += (soundsCount > 0) ? soundsCount : 1;
                            }
                            else count++;
                        }
                    }
                    else // Original Mode
                    {
                        count++;
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
                    count += await CalculateTotalAsync(node.Children, rootNodes, currentRootPath, mode, cancellationToken);
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
                else count += CountSoundsInAudioTree(node.Children);
            }
            return count;
        }

        #endregion

        #region Export Orchestration

        public async Task ExportAsync(FileSystemNodeModel node, string destinationPath, WadExportMode mode, ObservableRangeCollection<FileSystemNodeModel> rootNodes, string currentRootPath, CancellationToken cancellationToken, Action<string> onFileSavedCallback = null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 1. Directory Traversal
            if (node.Type == NodeType.WadFile || node.Type == NodeType.VirtualDirectory || node.Type == NodeType.RealDirectory)
            {
                string currentDestinationPath = Path.Combine(destinationPath, PathUtils.SanitizeName(node.Name));
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
                    await ExportAsync(child, currentDestinationPath, mode, rootNodes, currentRootPath, cancellationToken, onFileSavedCallback);
                }
                return;
            }

            // 2. Audio Event Handling (Smart Only)
            if (node.Type == NodeType.AudioEvent && mode == WadExportMode.Smart)
            {
                string eventPath = Path.Combine(destinationPath, PathUtils.SanitizeName(node.Name));
                _directoriesCreator.CreateDirectory(eventPath);

                foreach (var soundNode in node.Children)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (soundNode.Type == NodeType.WemFile)
                    {
                        await HandleWemFileAsync(soundNode, eventPath, cancellationToken, onFileSavedCallback);
                    }
                }
                return;
            }

            // 3. Single File Handling
            await ExportSingleAsync(node, destinationPath, mode, rootNodes, currentRootPath, cancellationToken, onFileSavedCallback);
        }

        private async Task ExportSingleAsync(FileSystemNodeModel node, string destinationPath, WadExportMode mode, ObservableRangeCollection<FileSystemNodeModel> rootNodes, string currentRootPath, CancellationToken cancellationToken, Action<string> onFileSavedCallback)
        {
            if (mode == WadExportMode.Original)
            {
                await HandleRawFileExtractionAsync(node, destinationPath, cancellationToken, onFileSavedCallback);
                return;
            }

            // Smart Export Mode
            string extension = Path.GetExtension(node.Name).ToLower();
            switch (extension)
            {
                case ".wpk":
                case ".bnk":
                    if (SupportedFileTypes.IsExpandableAudioBank(node.Name) && node.Children.Count > 0)
                    {
                        await HandleAudioBankFile(node, destinationPath, rootNodes, currentRootPath, cancellationToken, onFileSavedCallback);
                    }
                    break;

                case ".tex":
                case ".dds":
                    await HandleTextureFile(node, destinationPath, cancellationToken, onFileSavedCallback);
                    break;

                case ".bin":
                case ".stringtable":
                case ".css":
                    await HandleDataFile(node, destinationPath, extension.TrimStart('.'), cancellationToken, onFileSavedCallback);
                    break;

                case ".js":
                    await HandleJsFile(node, destinationPath, cancellationToken, onFileSavedCallback);
                    break;

                case ".wem":
                    await HandleWemFileAsync(node, destinationPath, cancellationToken, onFileSavedCallback);
                    break;

                case ".ogg":
                    await HandleStandardAudioFileAsync(node, destinationPath, cancellationToken, onFileSavedCallback);
                    break;

                default:
                    await HandleRawFileExtractionAsync(node, destinationPath, cancellationToken, onFileSavedCallback);
                    break;
            }
        }

        #endregion

        #region Handlers (Smart & Raw)

        private async Task HandleRawFileExtractionAsync(FileSystemNodeModel node, string destinationPath, CancellationToken cancellationToken, Action<string> onFileSavedCallback)
        {
            cancellationToken.ThrowIfCancellationRequested();

            byte[] fileBytes;
            if (node.Type == NodeType.WemFile)
                fileBytes = await _wadContentProvider.GetWemFileBytesAsync(node, cancellationToken);
            else
                fileBytes = await _wadContentProvider.GetVirtualFileBytesAsync(node, cancellationToken);

            if (fileBytes == null) return;

            string fileName = node.Name;
            if (string.IsNullOrEmpty(Path.GetExtension(fileName)))
            {
                string guessedExtension = FileTypeDetector.GuessExtension(fileBytes);
                if (!string.IsNullOrEmpty(guessedExtension)) fileName = $"{fileName}.{guessedExtension}";
            }

            string filePath = PathUtils.GetUniqueFilePath(destinationPath, fileName);
            await File.WriteAllBytesAsync(filePath, fileBytes, cancellationToken);
            onFileSavedCallback?.Invoke(filePath);
        }

        private async Task HandleTextureFile(FileSystemNodeModel node, string destinationPath, CancellationToken cancellationToken, Action<string> onFileSavedCallback)
        {
            if (_appSettings.ImageExportFormat == ImageExportFormat.Original)
            {
                await HandleRawFileExtractionAsync(node, destinationPath, cancellationToken, onFileSavedCallback);
                return;
            }

            var fileBytes = await _wadContentProvider.GetVirtualFileBytesAsync(node, cancellationToken);
            if (fileBytes == null) return;

            using (var memoryStream = new MemoryStream(fileBytes))
            {
                var bitmapSource = TextureUtils.LoadTexture(memoryStream, Path.GetExtension(node.Name));
                if (bitmapSource != null)
                {
                    TextureUtils.SaveBitmapSourceAsImage(bitmapSource, node.Name, destinationPath, _appSettings.ImageExportFormat, onFileSavedCallback);
                }
            }
        }

        private async Task HandleDataFile(FileSystemNodeModel node, string destinationPath, string type, CancellationToken cancellationToken, Action<string> onFileSavedCallback)
        {
            var fileBytes = await _wadContentProvider.GetVirtualFileBytesAsync(node, cancellationToken);
            if (fileBytes == null) return;

            var formattedContent = await _contentFormatterService.GetFormattedStringAsync(type, fileBytes);
            string fileName = Path.ChangeExtension(node.Name, ".json");
            string filePath = PathUtils.GetUniqueFilePath(destinationPath, fileName);

            await File.WriteAllTextAsync(filePath, formattedContent, cancellationToken);
            onFileSavedCallback?.Invoke(filePath);
        }

        private async Task HandleJsFile(FileSystemNodeModel node, string destinationPath, CancellationToken cancellationToken, Action<string> onFileSavedCallback)
        {
            var fileBytes = await _wadContentProvider.GetVirtualFileBytesAsync(node, cancellationToken);
            if (fileBytes == null) return;

            var formattedContent = await _contentFormatterService.GetFormattedStringAsync("js", fileBytes);
            string filePath = PathUtils.GetUniqueFilePath(destinationPath, node.Name);

            await File.WriteAllTextAsync(filePath, formattedContent, cancellationToken);
            onFileSavedCallback?.Invoke(filePath);
        }

        private async Task HandleStandardAudioFileAsync(FileSystemNodeModel node, string destinationPath, CancellationToken cancellationToken, Action<string> onFileSavedCallback)
        {
            var fileBytes = await _wadContentProvider.GetVirtualFileBytesAsync(node, cancellationToken);
            if (fileBytes == null) return;

            var targetFormat = _appSettings.AudioExportFormat;
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

        private async Task HandleWemFileAsync(FileSystemNodeModel node, string destinationPath, CancellationToken cancellationToken, Action<string> onFileSavedCallback)
        {
            var wemData = await _wadContentProvider.GetWemFileBytesAsync(node, cancellationToken);
            if (wemData == null) return;

            var format = _appSettings.AudioExportFormat;
            byte[] convertedData = await _audioConversionService.ConvertAudioToFormatAsync(wemData, ".wem", format, cancellationToken);
            if (convertedData != null)
            {
                string extension = format switch { AudioExportFormat.Wav => ".wav", AudioExportFormat.Mp3 => ".mp3", _ => ".ogg" };
                string filePath = PathUtils.GetUniqueFilePath(destinationPath, Path.ChangeExtension(node.Name, extension));
                await File.WriteAllBytesAsync(filePath, convertedData, cancellationToken);
                onFileSavedCallback?.Invoke(filePath);
            }
        }

        private async Task HandleAudioBankFile(FileSystemNodeModel node, string destinationPath, ObservableRangeCollection<FileSystemNodeModel> rootNodes, string currentRootPath, CancellationToken cancellationToken, Action<string> onFileSavedCallback)
        {
            var linkedBank = await _audioBankLinkerService.LinkAudioBankAsync(node, rootNodes, currentRootPath);
            if (linkedBank == null) return;

            string audioBankPath = Path.Combine(destinationPath, PathUtils.SanitizeName(Path.GetFileNameWithoutExtension(node.Name)));
            _directoriesCreator.CreateDirectory(audioBankPath);

            var eventsData = linkedBank.EventsBnkNode != null ? await _wadContentProvider.GetVirtualFileBytesAsync(linkedBank.EventsBnkNode, cancellationToken) : null;
            byte[] wpkData = linkedBank.WpkNode != null ? await _wadContentProvider.GetVirtualFileBytesAsync(linkedBank.WpkNode, cancellationToken) : null;
            byte[] audioBnkFileData = linkedBank.WpkNode == null && linkedBank.AudioBnkNode != null ? await _wadContentProvider.GetVirtualFileBytesAsync(linkedBank.AudioBnkNode, cancellationToken) : null;
            
            List<AudioEventNode> audioTree;
            if (linkedBank.BinData != null)
                audioTree = _audioBankService.ParseAudioBank(wpkData, audioBnkFileData, eventsData, linkedBank.BinData, linkedBank.BaseName, linkedBank.BinType);
            else
                audioTree = _audioBankService.ParseGenericAudioBank(wpkData, audioBnkFileData, eventsData);

            foreach (var eventNode in audioTree)
            {
                string eventPath = Path.Combine(audioBankPath, PathUtils.SanitizeName(eventNode.Name));
                _directoriesCreator.CreateDirectory(eventPath);

                foreach (var soundNode in eventNode.Sounds)
                {
                    byte[] wemData = null;
                    if (linkedBank.WpkNode != null)
                    {
                        using var wpkStream = new MemoryStream(wpkData);
                        var wpk = WpkParser.Parse(wpkStream, _logService);
                        var wem = wpk.Wems.FirstOrDefault(w => w.Id == soundNode.Id);
                        if (wem != null)
                        {
                            wpkStream.Seek(wem.Offset, SeekOrigin.Begin);
                            wemData = new byte[wem.Size];
                            wpkStream.Read(wemData, 0, (int)wem.Size);
                        }
                    }
                    else if (audioBnkFileData != null)
                    {
                        wemData = new byte[soundNode.Size];
                        Array.Copy(audioBnkFileData, soundNode.Offset, wemData, 0, soundNode.Size);
                    }

                    if (wemData != null)
                    {
                        var format = _appSettings.AudioExportFormat;
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
            }
        }

        #endregion
    }
}
