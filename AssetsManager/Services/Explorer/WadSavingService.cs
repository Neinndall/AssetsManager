
using AssetsManager.Services.Core;
using AssetsManager.Views.Models.Audio;
using AssetsManager.Views.Models.Explorer;
using AssetsManager.Views.Models.Wad;
using System.Threading.Tasks;
using AssetsManager.Services.Formatting;
using AssetsManager.Services.Audio;
using System.IO;
using System;
using System.Windows.Media.Imaging;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Threading; // Added to resolve CancellationToken
using AssetsManager.Services.Parsers;
using System.Linq;
using LeagueToolkit.Toolkit;
using AssetsManager.Utils;

namespace AssetsManager.Services.Explorer
{
    public class WadSavingService
    {
        private readonly LogService _logService;
        private readonly WadExtractionService _wadExtractionService;
        private readonly ContentFormatterService _contentFormatterService;
        private readonly AudioBankService _audioBankService;
        private readonly AudioBankLinkerService _audioBankLinkerService;
        private readonly WemConversionService _wemConversionService;

        public WadSavingService(
            LogService logService,
            WadExtractionService wadExtractionService,
            ContentFormatterService contentFormatterService,
            AudioBankService audioBankService,
            AudioBankLinkerService audioBankLinkerService,
            WemConversionService wemConversionService)
        {
            _logService = logService;
            _wadExtractionService = wadExtractionService;
            _contentFormatterService = contentFormatterService;
            _audioBankService = audioBankService;
            _audioBankLinkerService = audioBankLinkerService;
            _wemConversionService = wemConversionService;
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

        public async Task ProcessAndSaveAsync(FileSystemNodeModel node, string destinationPath, ObservableCollection<FileSystemNodeModel> rootNodes, string currentRootPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (node.Type == NodeType.WadFile || node.Type == NodeType.VirtualDirectory || node.Type == NodeType.RealDirectory)
            {
                string currentDestinationPath = Path.Combine(destinationPath, _wadExtractionService.SanitizeName(node.Name));
                Directory.CreateDirectory(currentDestinationPath);

                foreach (var child in node.Children)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await ProcessAndSaveAsync(child, currentDestinationPath, rootNodes, currentRootPath, cancellationToken);
                }
                return;
            }

            string extension = Path.GetExtension(node.Name).ToLower();

            switch (extension)
            {
                case ".wpk":
                case ".bnk":
                    await HandleAudioBankFile(node, destinationPath, rootNodes, currentRootPath, cancellationToken);
                    break;

                case ".tex":
                case ".dds":
                    await HandleTextureFile(node, destinationPath, cancellationToken);
                    break;

                case ".bin":
                    await HandleDataFile(node, destinationPath, "bin", cancellationToken);
                    break;

                case ".stringtable":
                    await HandleDataFile(node, destinationPath, "stringtable", cancellationToken);
                    break;

                case ".css":
                    await HandleDataFile(node, destinationPath, "css", cancellationToken);
                    break;

                case ".js":
                    await HandleJsFile(node, destinationPath, cancellationToken);
                    break;

                default:
                    // For any other file, just extract it raw
                    await _wadExtractionService.ExtractNodeAsync(node, destinationPath, cancellationToken);
                    break;
            }
        }

        private async Task HandleJsFile(FileSystemNodeModel node, string destinationPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileBytes = await _wadExtractionService.GetVirtualFileBytesAsync(node, cancellationToken);
            if (fileBytes == null) return;

            var formattedContent = await _contentFormatterService.GetFormattedStringAsync("js", fileBytes);

            string filePath = Path.Combine(destinationPath, _wadExtractionService.SanitizeName(node.Name));

            await File.WriteAllTextAsync(filePath, formattedContent, cancellationToken);
        }

        private async Task HandleAudioBankFile(FileSystemNodeModel node, string destinationPath, ObservableCollection<FileSystemNodeModel> rootNodes, string currentRootPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var linkedBank = await _audioBankLinkerService.LinkAudioBankAsync(node, rootNodes, currentRootPath);
            if (linkedBank == null) return;

            // Prevent duplicate processing for linked audio bank files.
            // Only the primary node (WPK or AudioBnkNode if no WPK) should trigger extraction.
            if (linkedBank.WpkNode != null && node != linkedBank.WpkNode)
            {
                return;
            }
            if (linkedBank.AudioBnkNode != null && node != linkedBank.AudioBnkNode && linkedBank.WpkNode == null)
            {
                return;
            }

            string audioBankName = Path.GetFileNameWithoutExtension(node.Name);
            string audioBankPath = Path.Combine(destinationPath, _wadExtractionService.SanitizeName(audioBankName));
            Directory.CreateDirectory(audioBankPath);

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
                string eventPath = Path.Combine(audioBankPath, _wadExtractionService.SanitizeName(eventNode.Name));
                Directory.CreateDirectory(eventPath);

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
                        byte[] oggData = await _wemConversionService.ConvertWemToOggAsync(wemData, cancellationToken);
                        if (oggData != null)
                        {
                            string fileName = Path.ChangeExtension(soundNode.Name, ".ogg");
                            string filePath = Path.Combine(eventPath, _wadExtractionService.SanitizeName(fileName));
                            await File.WriteAllBytesAsync(filePath, oggData, cancellationToken);
                        }
                    }
                }
            }
        }

        private async Task HandleTextureFile(FileSystemNodeModel node, string destinationPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileBytes = await _wadExtractionService.GetVirtualFileBytesAsync(node, cancellationToken);
            if (fileBytes == null) return;

            using (var memoryStream = new MemoryStream(fileBytes))
            {
                var bitmapSource = TextureUtils.LoadTexture(memoryStream, Path.GetExtension(node.Name));
                if (bitmapSource != null)
                {
                    BitmapEncoder encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmapSource));

                    string fileName = Path.ChangeExtension(node.Name, ".png");
                    string filePath = Path.Combine(destinationPath, _wadExtractionService.SanitizeName(fileName));

                    using (var fileStream = new FileStream(filePath, FileMode.Create))
                    {
                        encoder.Save(fileStream);
                    }
                }
            }
        }

        private async Task HandleDataFile(FileSystemNodeModel node, string destinationPath, string type, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileBytes = await _wadExtractionService.GetVirtualFileBytesAsync(node, cancellationToken);
            if (fileBytes == null) return;

            var formattedContent = await _contentFormatterService.GetFormattedStringAsync(type, fileBytes);

            string fileName = Path.ChangeExtension(node.Name, ".json");
            string filePath = Path.Combine(destinationPath, _wadExtractionService.SanitizeName(fileName));

            await File.WriteAllTextAsync(filePath, formattedContent, cancellationToken);
        }
    }
}
