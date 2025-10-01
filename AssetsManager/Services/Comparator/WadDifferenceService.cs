using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AssetsManager.Services.Hashes;
using System.Windows.Media.Imaging;
using BCnEncoder.Shared;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Renderer;
using LeagueToolkit.Core.Wad;
using AssetsManager.Views.Models;
using AssetsManager.Utils;
using AssetsManager.Services.Core;

namespace AssetsManager.Services.Comparator
{
    public class WadDifferenceService
    {
        private readonly HashResolverService _hashResolverService;
        private readonly LogService _logService;

        public WadDifferenceService(HashResolverService hashResolverService, LogService logService)
        {
            _hashResolverService = hashResolverService;
            _logService = logService;
        }

        public async Task<(string DataType, object OldData, object NewData, string OldPath, string NewPath)> PrepareDifferenceDataAsync(SerializableChunkDiff diff, string oldPbePath, string newPbePath)
        {
            if (string.IsNullOrEmpty(oldPbePath) || string.IsNullOrEmpty(newPbePath)){
                return ("error", null, null, null, null);
            }

            string extension = Path.GetExtension(diff.Path).ToLowerInvariant();
            byte[] oldData = null;
            byte[] newData = null;

            bool isChunkBased = oldPbePath.Contains("wad_chunks");

            if (isChunkBased)
            {
                if (diff.OldPathHash != 0)
                {
                    string oldChunkPath = Path.Combine(oldPbePath, $"{diff.OldPathHash:X16}.chunk");
                    if (File.Exists(oldChunkPath))
                    {
                        byte[] compressedOldData = await File.ReadAllBytesAsync(oldChunkPath);
                        oldData = WadChunkUtils.DecompressChunk(compressedOldData, diff.OldCompressionType);
                    }
                }

                if (diff.NewPathHash != 0)
                {
                    string newChunkPath = Path.Combine(newPbePath, $"{diff.NewPathHash:X16}.chunk");
                    if (File.Exists(newChunkPath))
                    {
                        byte[] compressedNewData = await File.ReadAllBytesAsync(newChunkPath);
                        newData = WadChunkUtils.DecompressChunk(compressedNewData, diff.NewCompressionType);
                    }
                }
            }
            else
            {
                if (diff.OldPathHash != 0 && File.Exists(Path.Combine(oldPbePath, diff.SourceWadFile)))
                {
                    using var oldWad = new WadFile(Path.Combine(oldPbePath, diff.SourceWadFile));
                    if (oldWad.Chunks.TryGetValue(diff.OldPathHash, out WadChunk oldChunk))
                    {
                        using var decompressedChunk = oldWad.LoadChunkDecompressed(oldChunk);
                        oldData = decompressedChunk.Span.ToArray();
                    }
                }

                if (diff.NewPathHash != 0 && File.Exists(Path.Combine(newPbePath, diff.SourceWadFile)))
                {
                    using var newWad = new WadFile(Path.Combine(newPbePath, diff.SourceWadFile));
                    if (newWad.Chunks.TryGetValue(diff.NewPathHash, out WadChunk newChunk))
                    {
                        using var decompressedChunk = newWad.LoadChunkDecompressed(newChunk);
                        newData = decompressedChunk.Span.ToArray();
                    }
                }
            }

            if (oldData == null && diff.Type != ChunkDiffType.New) return ("error", null, null, null, null);
            if (newData == null && diff.Type != ChunkDiffType.Removed) return ("error", null, null, null, null);

            var (dataType, oldResult, newResult) = await PrepareDataFromBytesAsync(oldData, newData, extension);
            return (dataType, oldResult, newResult, diff.OldPath, diff.NewPath);
        }

        public async Task<(string DataType, object OldData, object NewData)> PrepareFileDifferenceDataAsync(string oldFilePath, string newFilePath)
        {
            string extension = Path.GetExtension(newFilePath ?? oldFilePath).ToLowerInvariant();
            byte[] oldData = File.Exists(oldFilePath) ? await File.ReadAllBytesAsync(oldFilePath) : null;
            byte[] newData = File.Exists(newFilePath) ? await File.ReadAllBytesAsync(newFilePath) : null;

            return await PrepareDataFromBytesAsync(oldData, newData, extension);
        }

        public async Task<byte[]> GetDataFromChunkAsync(FileSystemNodeModel node)
        {
            if (node.ChunkDiff == null || !File.Exists(node.SourceWadPath))
            {
                return null;
            }

            byte[] compressedData = await File.ReadAllBytesAsync(node.SourceWadPath);
            var compressionType = node.ChunkDiff.Type == ChunkDiffType.Removed ? node.ChunkDiff.OldCompressionType : node.ChunkDiff.NewCompressionType;
            return WadChunkUtils.DecompressChunk(compressedData, compressionType);
        }

        private async Task<(string DataType, object OldData, object NewData)> PrepareDataFromBytesAsync(byte[] oldData, byte[] newData, string extension)
        {
            var jsExtensions = new[] { ".js" };
            var jsonExtensions = new[] { ".json" };
            var textExtensions = new[] { ".txt", ".lua", ".xml", ".yaml", ".yml", ".ini", ".log" };
            var imageExtensions = new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tex", ".dds" };
            var binExtensions = new[] { ".bin" };
            var cssExtensions = new[] { ".css" };

            object GetBinDataObject(byte[] data)
            {
                if (data == null) return null;
                try
                {
                    using var stream = new MemoryStream(data);
                    var bin = new BinTree(stream);
                    return BinUtils.ConvertBinTreeToDictionary(bin, _hashResolverService);
                }
                catch (Exception ex)
                {
                    _logService.LogError(ex, "Failed to parse .bin file content.");
                    throw;
                }
            }

            string GetTextContent(byte[] data)
            {
                if (data == null) return null;
                return Encoding.UTF8.GetString(data);
            }

            if (binExtensions.Contains(extension))
            {
                await _hashResolverService.LoadBinHashesAsync();
                object oldBinObject = GetBinDataObject(oldData);
                object newBinObject = GetBinDataObject(newData);
                return ("bin", oldBinObject, newBinObject);
            }
            if (jsExtensions.Contains(extension))
            {
                string oldText = GetTextContent(oldData);
                string newText = GetTextContent(newData);
                return ("js", oldText, newText);
            }
            if (jsonExtensions.Contains(extension))
            {
                string oldText = GetTextContent(oldData);
                string newText = GetTextContent(newData);
                return ("json", oldText, newText);
            }
            if (cssExtensions.Contains(extension))
            {
                string oldText = GetTextContent(oldData);
                string newText = GetTextContent(newData);
                return ("css", oldText, newText);
            }
            if (textExtensions.Contains(extension))
            {
                string oldText = GetTextContent(oldData);
                string newText = GetTextContent(newData);
                return ("text", oldText, newText);
            }
            else if (imageExtensions.Contains(extension))
            {
                var oldImage = ToBitmapSource(oldData, extension);
                var newImage = ToBitmapSource(newData, extension);
                return ("image", oldImage, newImage);
            }
            else
            {
                return ("unsupported", null, null);
            }
        }

        private BitmapSource ToBitmapSource(byte[] data, string extension)
        {
            if (data == null || data.Length == 0) return null;

            if (extension == ".tex" || extension == ".dds")
            {
                using (var stream = new MemoryStream(data))
                {
                    var texture = LeagueToolkit.Core.Renderer.Texture.Load(stream);
                    if (texture.Mips.Length == 0) return null;

                    var mainMip = texture.Mips[0];
                    var width = mainMip.Width;
                    var height = mainMip.Height;

                    if (mainMip.Span.TryGetSpan(out Span<ColorRgba32> pixelSpan))
                    {
                        var pixelByteSpan = MemoryMarshal.AsBytes(pixelSpan);
                        var pixelBytes = pixelByteSpan.ToArray();
                        for (int i = 0; i < pixelBytes.Length; i += 4)
                        {
                            var r = pixelBytes[i];
                            var b = pixelBytes[i + 2];
                            pixelBytes[i] = b;
                            pixelBytes[i + 2] = r;
                        }
                        return BitmapSource.Create(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixelBytes, width * 4);
                    }

                    return null;
                }
            }
            else
            {
                using (var stream = new MemoryStream(data))
                {
                    var bitmapImage = new BitmapImage();
                    bitmapImage.BeginInit();
                    bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                    bitmapImage.StreamSource = stream;
                    bitmapImage.EndInit();
                    bitmapImage.Freeze();
                    return bitmapImage;
                }
            }
        }
    }
}
