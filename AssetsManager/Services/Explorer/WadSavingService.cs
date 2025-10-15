
using AssetsManager.Services.Core;
using AssetsManager.Views.Models;
using System.Threading.Tasks;
using AssetsManager.Services.Formatting;
using AssetsManager.Services.Audio;
using AssetsManager.Services.Models;
using System.IO;
using System;
using System.Windows.Media.Imaging;
using LeagueToolkit.Core.Renderer;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp;
using System.Windows.Media;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using AssetsManager.Services.Parsers;
using System.Linq;
using LeagueToolkit.Toolkit;

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
        private readonly ModelLoadingService _modelLoadingService;

        public WadSavingService(
            LogService logService,
            WadExtractionService wadExtractionService,
            ContentFormatterService contentFormatterService,
            AudioBankService audioBankService,
            AudioBankLinkerService audioBankLinkerService,
            WemConversionService wemConversionService,
            ModelLoadingService modelLoadingService)
        {
            _logService = logService;
            _wadExtractionService = wadExtractionService;
            _contentFormatterService = contentFormatterService;
            _audioBankService = audioBankService;
            _audioBankLinkerService = audioBankLinkerService;
            _wemConversionService = wemConversionService;
            _modelLoadingService = modelLoadingService;
        }

        public async Task ProcessAndSaveDiffAsync(SerializableChunkDiff diff, string destinationPath, string oldLolPath, string newLolPath)
        {
            string basePath = (diff.Type == ChunkDiffType.Removed) ? oldLolPath : newLolPath;
            string sourceWadPath = Path.Combine(basePath, diff.SourceWadFile);

            var node = new FileSystemNodeModel(diff.FileName, false, diff.Path, sourceWadPath)
            {
                SourceChunkPathHash = (diff.Type == ChunkDiffType.Removed) ? diff.OldPathHash : diff.NewPathHash,
                ChunkDiff = diff,
                Status = (DiffStatus)diff.Type
            };

            await ProcessAndSaveAsync(node, destinationPath, null, basePath);
        }

        public async Task ProcessAndSaveAsync(FileSystemNodeModel node, string destinationPath, ObservableCollection<FileSystemNodeModel> rootNodes, string currentRootPath)
        {
            string extension = Path.GetExtension(node.Name).ToLower();

            switch (extension)
            {
                case ".wpk":
                case ".bnk":
                    await HandleAudioBankFile(node, destinationPath, rootNodes, currentRootPath);
                    break;

                case ".tex":
                case ".dds":
                    await HandleTextureFile(node, destinationPath);
                    break;

                case ".bin":
                    await HandleDataFile(node, destinationPath, "bin");
                    break;

                case ".stringtable":
                    await HandleDataFile(node, destinationPath, "stringtable");
                    break;

                case ".css":
                    await HandleDataFile(node, destinationPath, "css");
                    break;

                case ".js":
                    await HandleJsFile(node, destinationPath);
                    break;

                default:
                    // For any other file, just extract it raw
                    await _wadExtractionService.ExtractNodeAsync(node, destinationPath);
                    break;
            }
        }

        private async Task HandleJsFile(FileSystemNodeModel node, string destinationPath)
        {
            var fileBytes = await _wadExtractionService.GetVirtualFileBytesAsync(node);
            if (fileBytes == null) return;

            var formattedContent = await _contentFormatterService.GetFormattedStringAsync("js", fileBytes);

            string filePath = Path.Combine(destinationPath, node.Name);

            await File.WriteAllTextAsync(filePath, formattedContent);
        }

        private async Task HandleAudioBankFile(FileSystemNodeModel node, string destinationPath, ObservableCollection<FileSystemNodeModel> rootNodes, string currentRootPath)
        {
            string audioBankName = Path.GetFileNameWithoutExtension(node.Name);
            string audioBankPath = Path.Combine(destinationPath, audioBankName);
            Directory.CreateDirectory(audioBankPath);

            var linkedBank = await _audioBankLinkerService.LinkAudioBankAsync(node, rootNodes, currentRootPath);
            if (linkedBank == null) return;

            var eventsData = linkedBank.EventsBnkNode != null ? await _wadExtractionService.GetVirtualFileBytesAsync(linkedBank.EventsBnkNode) : null;
            byte[] wpkData = linkedBank.WpkNode != null ? await _wadExtractionService.GetVirtualFileBytesAsync(linkedBank.WpkNode) : null;
            byte[] audioBnkFileData = linkedBank.AudioBnkNode != null ? await _wadExtractionService.GetVirtualFileBytesAsync(linkedBank.AudioBnkNode) : null;

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
                string eventPath = Path.Combine(audioBankPath, eventNode.Name);
                Directory.CreateDirectory(eventPath);

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
                        byte[] oggData = await _wemConversionService.ConvertWemToOggAsync(wemData);
                        if (oggData != null)
                        {
                            string fileName = Path.ChangeExtension(soundNode.Name, ".ogg");
                            string filePath = Path.Combine(eventPath, fileName);
                            await File.WriteAllBytesAsync(filePath, oggData);
                        }
                    }
                }
            }
        }

        private async Task HandleTextureFile(FileSystemNodeModel node, string destinationPath)
        {
            var fileBytes = await _wadExtractionService.GetVirtualFileBytesAsync(node);
            if (fileBytes == null) return;

            using (var memoryStream = new MemoryStream(fileBytes))
            {
                Texture tex = Texture.Load(memoryStream);
                if (tex.Mips.Length > 0)
                {
                    Image<Rgba32> imageSharp = tex.Mips[0].ToImage();

                    var pixelBuffer = new byte[imageSharp.Width * imageSharp.Height * 4];
                    imageSharp.CopyPixelDataTo(pixelBuffer);

                    for (int i = 0; i < pixelBuffer.Length; i += 4)
                    {
                        var r = pixelBuffer[i];
                        var b = pixelBuffer[i + 2];
                        pixelBuffer[i] = b;
                        pixelBuffer[i + 2] = r;
                    }

                    int stride = imageSharp.Width * 4;
                    var bitmapSource = BitmapSource.Create(imageSharp.Width, imageSharp.Height, 96, 96, PixelFormats.Bgra32, null, pixelBuffer, stride);
                    bitmapSource.Freeze();

                    BitmapEncoder encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmapSource));

                    string fileName = Path.ChangeExtension(node.Name, ".png");
                    string filePath = Path.Combine(destinationPath, fileName);

                    using (var fileStream = new FileStream(filePath, FileMode.Create))
                    {
                        encoder.Save(fileStream);
                    }
                }
            }
        }

        private async Task HandleDataFile(FileSystemNodeModel node, string destinationPath, string type)
        {
            var fileBytes = await _wadExtractionService.GetVirtualFileBytesAsync(node);
            if (fileBytes == null) return;

            var formattedContent = await _contentFormatterService.GetFormattedStringAsync(type, fileBytes);
            
            string fileName = Path.ChangeExtension(node.Name, ".json");
            string filePath = Path.Combine(destinationPath, fileName);

            await File.WriteAllTextAsync(filePath, formattedContent);
        }
    }
}
