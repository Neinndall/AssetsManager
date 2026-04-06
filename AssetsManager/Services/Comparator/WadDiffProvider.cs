using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LeagueToolkit.Core.Wad;
using AssetsManager.Views.Models.Wad;
using AssetsManager.Utils;
using AssetsManager.Services.Core;
using AssetsManager.Services.Explorer;

namespace AssetsManager.Services.Comparator
{
    public class WadDiffProvider
    {
        private readonly LogService _logService;
        private readonly WadContentProvider _wadContentProvider;

        public WadDiffProvider(LogService logService, WadContentProvider wadContentProvider)
        {
            _logService = logService;
            _wadContentProvider = wadContentProvider;
        }

        public async Task<(string DataType, object OldData, object NewData, string OldPath, string NewPath)> PrepareDifferenceDataAsync(SerializableChunkDiff diff, string oldPbePath, string newPbePath)
        {
            string extension = Path.GetExtension(diff.Path).ToLowerInvariant();
            
            // Delegamos la extracción de ambos lados al WadContentProvider
            var oldDataTask = _wadContentProvider.GetDiffSideBytesAsync(diff, oldPbePath, true);
            var newDataTask = _wadContentProvider.GetDiffSideBytesAsync(diff, newPbePath, false);

            await Task.WhenAll(oldDataTask, newDataTask);

            byte[] oldData = await oldDataTask;
            byte[] newData = await newDataTask;

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
