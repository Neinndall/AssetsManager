using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LeagueToolkit.Core.Wad;
using AssetsManager.Views.Models;
using AssetsManager.Utils;
using AssetsManager.Services.Core;

namespace AssetsManager.Services.Comparator
{
    public class WadDifferenceService
    {
        private readonly LogService _logService;

        public WadDifferenceService(LogService logService)
        {
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

            var (dataType, oldResult, newResult) = PrepareDataFromBytes(oldData, newData, extension);
            return (dataType, oldResult, newResult, diff.OldPath, diff.NewPath);
        }

        public async Task<(string DataType, object OldData, object NewData)> PrepareFileDifferenceDataAsync(string oldFilePath, string newFilePath)
        {
            string extension = Path.GetExtension(newFilePath ?? oldFilePath).ToLowerInvariant();
            byte[] oldData = File.Exists(oldFilePath) ? await File.ReadAllBytesAsync(oldFilePath) : null;
            byte[] newData = File.Exists(newFilePath) ? await File.ReadAllBytesAsync(newFilePath) : null;

            return PrepareDataFromBytes(oldData, newData, extension);
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

        private (string DataType, object OldData, object NewData) PrepareDataFromBytes(byte[] oldData, byte[] newData, string extension)
        {
            var dataType = extension.TrimStart('.');
            return (dataType, oldData, newData);
        }
    }
}
