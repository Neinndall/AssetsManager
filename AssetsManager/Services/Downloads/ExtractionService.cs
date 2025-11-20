using AssetsManager.Services.Core;
using AssetsManager.Services.Explorer;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Explorer;
using AssetsManager.Views.Models.Wad;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AssetsManager.Services.Downloads
{
    public class ExtractionService
    {
        private readonly LogService _logService;
        private readonly DirectoriesCreator _directoriesCreator;
        private readonly WadSavingService _wadSavingService;
        private readonly WadExtractionService _wadExtractionService;

        public event EventHandler<(string message, int totalFiles)> ExtractionStarted;
        public event EventHandler<(int extractedCount, int totalFiles, string message)> ExtractionProgressChanged;
        public event EventHandler ExtractionCompleted;

        public ExtractionService(
            LogService logService,
            DirectoriesCreator directoriesCreator,
            WadSavingService wadSavingService,
            WadExtractionService wadExtractionService)
        {
            _logService = logService;
            _directoriesCreator = directoriesCreator;
            _wadSavingService = wadSavingService;
            _wadExtractionService = wadExtractionService;
        }

        public async Task ExtractNewFilesFromComparisonAsync(
            List<SerializableChunkDiff> allDiffs,
            string newLolPath,
            CancellationToken cancellationToken)
        {
            var newDiffs = allDiffs.Where(d => d.Type == ChunkDiffType.New).ToList();

            if (!newDiffs.Any())
            {
                _logService.Log("No new files to extract from the comparison.");
                ExtractionCompleted?.Invoke(this, EventArgs.Empty);
                return;
            }

            int totalFiles = newDiffs.Count;

            ExtractionStarted?.Invoke(this, ("Extraction of new assets started...", totalFiles));

            // Create a unique destination directory for this extraction session
            _directoriesCreator.GenerateNewSubAssetsDownloadedPath();
            string destinationRootPath = _directoriesCreator.SubAssetsDownloadedPath;

            int extractedCount = 0;

            _logService.Log($"Starting extraction of {totalFiles} new files.");

            foreach (var diff in newDiffs)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logService.LogWarning("Extraction was cancelled by the user.");
                    break; 
                }

                extractedCount++;
                string progressMessage = $"{diff.FileName}";
                ExtractionProgressChanged?.Invoke(this, (extractedCount, totalFiles, progressMessage));
                
                try
                {
                    string sourceWadFullPath = Path.Combine(newLolPath, diff.SourceWadFile);
                    var node = new FileSystemNodeModel(diff.FileName, false, diff.NewPath, sourceWadFullPath)
                    {
                        SourceChunkPathHash = diff.NewPathHash,
                        Status = DiffStatus.New
                    };

                    // This calculation is now done for ALL files beforehand.
                    string fileDestinationDirectory = Path.Combine(destinationRootPath, Path.GetDirectoryName(diff.NewPath));
                    Directory.CreateDirectory(fileDestinationDirectory);

                    string extension = Path.GetExtension(node.Name).ToLower();

                    if (extension == ".wpk" || extension == ".bnk")
                    {
                        // Raw copy for audio banks, ensuring it goes to the correct subdirectory
                        await _wadExtractionService.ExtractNodeAsync(node, fileDestinationDirectory);
                    }
                    else
                    {
                        // Smart saving for all other files, ensuring it goes to the correct subdirectory
                        await _wadSavingService.ProcessAndSaveAsync(node, fileDestinationDirectory, null, newLolPath);
                    }
                }
                catch (Exception ex)
                {
                    _logService.LogError(ex, $"Failed to extract new file: {diff.FileName}");
                    // Continue with other files even if one fails
                }
            }
            
            _logService.LogSuccess($"Extraction completed. {extractedCount} files processed.");
            ExtractionCompleted?.Invoke(this, EventArgs.Empty);
        }
    }
}

