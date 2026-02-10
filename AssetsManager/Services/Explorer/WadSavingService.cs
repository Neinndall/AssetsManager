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
using AssetsManager.Services.Core;
using AssetsManager.Views.Models.Audio;
using AssetsManager.Services.Parsers;
using AssetsManager.Views.Models.Explorer;
using AssetsManager.Views.Models.Wad;
using AssetsManager.Views.Models.Settings;

using AssetsManager.Services.Formatting;
using AssetsManager.Services.Audio;
namespace AssetsManager.Services.Explorer
{
    public class WadSavingService
    {
        private readonly LogService _logService;
        private readonly WadExtractionService _wadExtractionService;
        private readonly ContentFormatterService _contentFormatterService;
        private readonly AudioBankService _audioBankService;
        private readonly AudioBankLinkerService _audioBankLinkerService;
        private readonly AudioConversionService _audioConversionService;
        private readonly AppSettings _appSettings;
        private readonly WadNodeLoaderService _wadNodeLoaderService;
        private readonly DirectoriesCreator _directoriesCreator;

        public WadSavingService(
            LogService logService,
            WadExtractionService wadExtractionService,
            ContentFormatterService contentFormatterService,
            AudioBankService audioBankService,
            AudioBankLinkerService audioBankLinkerService,
            AudioConversionService audioConversionService,
            AppSettings appSettings,
            WadNodeLoaderService wadNodeLoaderService,
            DirectoriesCreator directoriesCreator)
        {
            _logService = logService;
            _wadExtractionService = wadExtractionService;
            _contentFormatterService = contentFormatterService;
            _audioBankService = audioBankService;
            _audioBankLinkerService = audioBankLinkerService;
            _audioConversionService = audioConversionService;
            _appSettings = appSettings;
            _wadNodeLoaderService = wadNodeLoaderService;
            _directoriesCreator = directoriesCreator;
        }

        public async Task<int> CalculateTotalAsync(IEnumerable<FileSystemNodeModel> nodes, ObservableRangeCollection<FileSystemNodeModel> rootNodes, string currentRootPath, CancellationToken cancellationToken)
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
                    // --- SMART REDUNDANCY CHECK ---
                    // If it's an expandable bank name but has NO children, 
                    // the Loader marked it as redundant. Skip it.
                    if (SupportedFileTypes.IsExpandableAudioBank(node.Name) && node.Children.Count == 0)
                    {
                        continue;
                    }

                    if (node.Children.Count > 1 || (node.Children.Count == 1 && node.Children[0].Name != "Loading..."))
                    {
                        count += CountSoundsInAudioTree(node.Children);
                    }
                    else if (SupportedFileTypes.IsExpandableAudioBank(node.Name))
                    {
                        var linkedBank = await _audioBankLinkerService.LinkAudioBankAsync(node, rootNodes, currentRootPath);
                        if (linkedBank != null)
                        {
                            byte[] wpkData = linkedBank.WpkNode != null ? await _wadExtractionService.GetVirtualFileBytesAsync(linkedBank.WpkNode, cancellationToken) : null;
                            byte[] audioBnkData = linkedBank.AudioBnkNode != null ? await _wadExtractionService.GetVirtualFileBytesAsync(linkedBank.AudioBnkNode, cancellationToken) : null;

                            int soundsCount = _audioBankService.GetSoundCount(wpkData, audioBnkData);
                            count += (soundsCount > 0) ? soundsCount : 1;
                        }
                        else count++;
                    }
                    // Else: It's a metadata bank (like _events.bnk). Skip from total count in SAVE mode.
                }
                else if (node.Type == NodeType.AudioEvent || node.Type == NodeType.VirtualDirectory || node.Type == NodeType.RealDirectory || node.Type == NodeType.WadFile)
                {
                    if ((node.Type == NodeType.VirtualDirectory || node.Type == NodeType.WadFile) &&
                        node.Children.Count == 1 && node.Children[0].Name == "Loading...")
                    {
                        var loadedChildren = await _wadNodeLoaderService.LoadChildrenAsync(node, cancellationToken);
                        node.Children.Clear();
                        foreach (var child in loadedChildren) node.Children.Add(child);
                    }
                    count += await CalculateTotalAsync(node.Children, rootNodes, currentRootPath, cancellationToken);
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

        public async Task ProcessAndSaveDiffAsync(SerializableChunkDiff diff, string destinationPath, string oldLolPath, string newLolPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string basePath = (diff.Type == ChunkDiffType.Removed) ? oldLolPath : newLolPath;
            string sourceWadPath = Path.Combine(basePath, diff.SourceWadFile);

            var node = new FileSystemNodeModel(diff.FileName, false, diff.Path, sourceWadPath)
            {
                SourceChunkPathHash = (diff.Type == ChunkDiffType.Removed) ? diff.OldPathHash : diff.NewPathHash,
                ChunkDiff = diff,
                Status = (DiffStatus)diff.Type
            };

            await ProcessAndSaveAsync(node, destinationPath, null, basePath, cancellationToken);
        }

        public async Task ProcessAndSaveAsync(FileSystemNodeModel node, string destinationPath, ObservableRangeCollection<FileSystemNodeModel> rootNodes, string currentRootPath, CancellationToken cancellationToken, Action<string> onFileSavedCallback = null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (node.Type == NodeType.WadFile || node.Type == NodeType.VirtualDirectory || node.Type == NodeType.RealDirectory)
            {
                string currentDestinationPath = Path.Combine(destinationPath, PathUtils.SanitizeName(node.Name));
                await _directoriesCreator.CreateDirectoryAsync(currentDestinationPath);

                foreach (var child in node.Children)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await ProcessAndSaveAsync(child, currentDestinationPath, rootNodes, currentRootPath, cancellationToken, onFileSavedCallback);
                }
                return;
            }

            if (node.Type == NodeType.AudioEvent)
            {
                string eventPath = Path.Combine(destinationPath, PathUtils.SanitizeName(node.Name));
                await _directoriesCreator.CreateDirectoryAsync(eventPath);

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

            string extension = Path.GetExtension(node.Name).ToLower();

            switch (extension)
            {
                case ".wpk":
                case ".bnk":
                    // Only process as expandable bank if the Loader marked it as such (has children)
                    if (SupportedFileTypes.IsExpandableAudioBank(node.Name) && node.Children.Count > 0)
                    {
                        await HandleAudioBankFile(node, destinationPath, rootNodes, currentRootPath, cancellationToken, onFileSavedCallback);
                    }
                    // Else: Redundant sibling or metadata bank (like _events.bnk). Skip in SAVE mode.
                    break;

                case ".tex":
                case ".dds":
                    await HandleTextureFile(node, destinationPath, cancellationToken, onFileSavedCallback);
                    break;

                case ".bin":
                    await HandleDataFile(node, destinationPath, "bin", cancellationToken, onFileSavedCallback);
                    break;

                case ".stringtable":
                    await HandleDataFile(node, destinationPath, "stringtable", cancellationToken, onFileSavedCallback);
                    break;

                case ".css":
                    await HandleDataFile(node, destinationPath, "css", cancellationToken, onFileSavedCallback);
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

        private async Task HandleStandardAudioFileAsync(FileSystemNodeModel node, string destinationPath, CancellationToken cancellationToken, Action<string> onFileSavedCallback)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileBytes = await _wadExtractionService.GetVirtualFileBytesAsync(node, cancellationToken);
            if (fileBytes == null)
            {
                return;
            }

            var targetFormat = _appSettings.AudioExportFormat;
            string currentExtension = Path.GetExtension(node.Name).ToLower();
            bool conversionNeeded = true;

            // Optimize: Skip conversion if source matches target (Ogg -> Ogg)
            if (targetFormat == AudioExportFormat.Ogg && currentExtension == ".ogg") conversionNeeded = false;

            if (conversionNeeded)
            {
                byte[] convertedData = await _audioConversionService.ConvertAudioToFormatAsync(fileBytes, ".wem", targetFormat, cancellationToken);
                if (convertedData != null)
                {
                    string extension = targetFormat switch { AudioExportFormat.Wav => ".wav", AudioExportFormat.Mp3 => ".mp3", _ => ".ogg" };
                    string fileName = Path.ChangeExtension(node.Name, extension);
                    string filePath = PathUtils.GetUniqueFilePath(destinationPath, fileName);
                    await File.WriteAllBytesAsync(filePath, convertedData, cancellationToken);
                    onFileSavedCallback?.Invoke(filePath);
                    return;
                }
            }

            // Fallback: If conversion wasn't needed or failed, save raw
            string fallbackFilePath = PathUtils.GetUniqueFilePath(destinationPath, node.Name);
            await File.WriteAllBytesAsync(fallbackFilePath, fileBytes, cancellationToken);
            onFileSavedCallback?.Invoke(fallbackFilePath);
        }

        private async Task HandleWemFileAsync(FileSystemNodeModel node, string destinationPath, CancellationToken cancellationToken, Action<string> onFileSavedCallback)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var wemData = await _wadExtractionService.GetWemFileBytesAsync(node, cancellationToken);
            if (wemData == null)
            {
                return;
            }

            var format = _appSettings.AudioExportFormat;
            byte[] convertedData = await _audioConversionService.ConvertAudioToFormatAsync(wemData, ".wem", format, cancellationToken);
            if (convertedData != null)
            {
                string extension = format switch { AudioExportFormat.Wav => ".wav", AudioExportFormat.Mp3 => ".mp3", _ => ".ogg" };
                string fileName = Path.ChangeExtension(node.Name, extension);
                string filePath = PathUtils.GetUniqueFilePath(destinationPath, fileName);
                await File.WriteAllBytesAsync(filePath, convertedData, cancellationToken);
                onFileSavedCallback?.Invoke(filePath);
            }
        }

        private async Task HandleJsFile(FileSystemNodeModel node, string destinationPath, CancellationToken cancellationToken, Action<string> onFileSavedCallback)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileBytes = await _wadExtractionService.GetVirtualFileBytesAsync(node, cancellationToken);
            if (fileBytes == null) return;

            var formattedContent = await _contentFormatterService.GetFormattedStringAsync("js", fileBytes);

            string filePath = PathUtils.GetUniqueFilePath(destinationPath, node.Name);

            await File.WriteAllTextAsync(filePath, formattedContent, cancellationToken);
            onFileSavedCallback?.Invoke(filePath);
        }

        private async Task HandleAudioBankFile(FileSystemNodeModel node, string destinationPath, ObservableRangeCollection<FileSystemNodeModel> rootNodes, string currentRootPath, CancellationToken cancellationToken, Action<string> onFileSavedCallback)
        {
            if (!SupportedFileTypes.IsExpandableAudioBank(node.Name))
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var linkedBank = await _audioBankLinkerService.LinkAudioBankAsync(node, rootNodes, currentRootPath);
            if (linkedBank == null)
            {
                return;
            }

            string audioBankName = Path.GetFileNameWithoutExtension(node.Name);
            string audioBankPath = Path.Combine(destinationPath, PathUtils.SanitizeName(audioBankName));
            await _directoriesCreator.CreateDirectoryAsync(audioBankPath);

            cancellationToken.ThrowIfCancellationRequested();
            var eventsData = linkedBank.EventsBnkNode != null ? await _wadExtractionService.GetVirtualFileBytesAsync(linkedBank.EventsBnkNode, cancellationToken) : null;
            byte[] wpkData = linkedBank.WpkNode != null ? await _wadExtractionService.GetVirtualFileBytesAsync(linkedBank.WpkNode, cancellationToken) : null;
            byte[] audioBnkFileData = linkedBank.WpkNode == null && linkedBank.AudioBnkNode != null ? await _wadExtractionService.GetVirtualFileBytesAsync(linkedBank.AudioBnkNode, cancellationToken) : null;
            
            List<AudioEventNode> audioTree;
            if (linkedBank.BinData != null)
            {
                if (wpkData != null)
                {
                    audioTree = _audioBankService.ParseAudioBank(wpkData, audioBnkFileData, eventsData, linkedBank.BinData, linkedBank.BaseName, linkedBank.BinType);
                }
                else
                {
                    audioTree = _audioBankService.ParseSfxAudioBank(audioBnkFileData, eventsData, linkedBank.BinData, linkedBank.BaseName, linkedBank.BinType);
                }
            }
            else
            {
                audioTree = _audioBankService.ParseGenericAudioBank(wpkData, audioBnkFileData, eventsData);
            }

            foreach (var eventNode in audioTree)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string eventPath = Path.Combine(audioBankPath, PathUtils.SanitizeName(eventNode.Name));
                await _directoriesCreator.CreateDirectoryAsync(eventPath);

                foreach (var soundNode in eventNode.Sounds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    byte[] wemData = null;
                    if (linkedBank.WpkNode != null)
                    {
                        using var wpkStream = new MemoryStream(wpkData);
                        var wpk = WpkParser.Parse(wpkStream, _logService);
                        var wem = wpk.Wems.FirstOrDefault(w => w.Id == soundNode.Id);
                        if (wem != null)
                        {
                            using var reader = new BinaryReader(wpkStream);
                            wpkStream.Seek(wem.Offset, SeekOrigin.Begin);
                            wemData = reader.ReadBytes((int)wem.Size);
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
                            string fileName = Path.ChangeExtension(soundNode.Name, extension);
                            string filePath = PathUtils.GetUniqueFilePath(eventPath, fileName);
                            await File.WriteAllBytesAsync(filePath, convertedData, cancellationToken);
                            onFileSavedCallback?.Invoke(filePath);
                        }
                    }
                }
            }
        }

        private async Task HandleTextureFile(FileSystemNodeModel node, string destinationPath, CancellationToken cancellationToken, Action<string> onFileSavedCallback)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileBytes = await _wadExtractionService.GetVirtualFileBytesAsync(node, cancellationToken);
            if (fileBytes == null)
            {
                return;
            }

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
            cancellationToken.ThrowIfCancellationRequested();

            var fileBytes = await _wadExtractionService.GetVirtualFileBytesAsync(node, cancellationToken);
            if (fileBytes == null)
            {
                return;
            }

            var formattedContent = await _contentFormatterService.GetFormattedStringAsync(type, fileBytes);

            string fileName = Path.ChangeExtension(node.Name, ".json");
            string filePath = PathUtils.GetUniqueFilePath(destinationPath, fileName);

            await File.WriteAllTextAsync(filePath, formattedContent, cancellationToken);
            onFileSavedCallback?.Invoke(filePath);
        }

        private async Task HandleRawFileExtractionAsync(FileSystemNodeModel node, string destinationPath, CancellationToken cancellationToken, Action<string> onFileSavedCallback)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileBytes = await _wadExtractionService.GetVirtualFileBytesAsync(node, cancellationToken);
            if (fileBytes == null)
            {
                return;
            }

            string fileName = node.Name;
            string existingExtension = Path.GetExtension(fileName);

            if (string.IsNullOrEmpty(existingExtension))
            {
                // If no extension, try to guess it
                string guessedExtension = FileTypeDetector.GuessExtension(fileBytes);
                if (!string.IsNullOrEmpty(guessedExtension))
                {
                    // Fix: If we guessed it's a texture, we should convert it to PNG just like in the main switch
                    if (guessedExtension.Equals("tex", StringComparison.OrdinalIgnoreCase) || 
                        guessedExtension.Equals("dds", StringComparison.OrdinalIgnoreCase))
                    {
                        using (var memoryStream = new MemoryStream(fileBytes))
                        {
                            // Try to load it as a texture
                            var bitmapSource = TextureUtils.LoadTexture(memoryStream, "." + guessedExtension);
                            if (bitmapSource != null)
                            {
                                TextureUtils.SaveBitmapSourceAsImage(bitmapSource, node.Name, destinationPath, _appSettings.ImageExportFormat, onFileSavedCallback);
                                return; // Done, we converted and saved it
                            }
                        }
                    }

                    // For other types, just append the extension
                    fileName = $"{fileName}.{guessedExtension}";
                }
            }

            string filePath = PathUtils.GetUniqueFilePath(destinationPath, fileName);
            await File.WriteAllBytesAsync(filePath, fileBytes, cancellationToken);
            onFileSavedCallback?.Invoke(filePath);
        }
    }
}
