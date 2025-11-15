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
            string extension = Path.GetExtension(diff.Path).ToLowerInvariant();
            byte[] oldData = null;
            byte[] newData = null;

            // Live Mode: Paths are provided to read from original WADs
            if (oldPbePath != null && newPbePath != null)
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
            // Backup Mode: Paths are null, rely on the diff's chunk path
            else
            {
                if (string.IsNullOrEmpty(diff.BackupChunkPath))
                {
                    _logService.LogError("Critical error: In backup mode but BackupChunkPath is null.");
                    return ("error", null, null, null, null);
                }

                string backupRoot = Path.GetDirectoryName(Path.GetDirectoryName(diff.BackupChunkPath));

                if (diff.OldPathHash != 0)
                {
                    string oldChunkPath = Path.Combine(backupRoot, "old", $"{diff.OldPathHash:X16}.chunk");
                    if (File.Exists(oldChunkPath))
                    {
                        byte[] compressedOldData = await File.ReadAllBytesAsync(oldChunkPath);
                        oldData = WadChunkUtils.DecompressChunk(compressedOldData, diff.OldCompressionType);
                    }
                }

                if (diff.NewPathHash != 0)
                {
                    string newChunkPath = Path.Combine(backupRoot, "new", $"{diff.NewPathHash:X16}.chunk");
                    if (File.Exists(newChunkPath))
                    {
                        byte[] compressedNewData = await File.ReadAllBytesAsync(newChunkPath);
                        newData = WadChunkUtils.DecompressChunk(compressedNewData, diff.NewCompressionType);
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


        private (string DataType, object OldData, object NewData) PrepareDataFromBytes(byte[] oldData, byte[] newData, string extension)
        {
            var dataType = extension.TrimStart('.');
            return (dataType, oldData, newData);
        }
    }
}
