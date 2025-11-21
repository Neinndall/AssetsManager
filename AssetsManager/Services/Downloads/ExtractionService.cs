using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Explorer;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Explorer;
using AssetsManager.Views.Models.Wad;

namespace AssetsManager.Services.Downloads
{
    public class ExtractionService
    {
        private readonly AppSettings _appSettings;
        private readonly LogService _logService;
        private readonly DirectoriesCreator _directoriesCreator;
        private readonly WadSavingService _wadSavingService;
        private readonly WadExtractionService _wadExtractionService;

        public event EventHandler<(string message, int totalFiles)> ExtractionStarted;
        public event EventHandler<(int extractedCount, int totalFiles, string message)> ExtractionProgressChanged;
        public event EventHandler ExtractionCompleted;

        public ExtractionService(
            AppSettings appSettings,
            LogService logService,
            DirectoriesCreator directoriesCreator,
            WadSavingService wadSavingService,
            WadExtractionService wadExtractionService)
        {
            _appSettings = appSettings;
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
                _logService.Log("No new assets to extract from the comparison.");
                ExtractionCompleted?.Invoke(this, EventArgs.Empty);
                return;
            }

            int totalFiles = newDiffs.Count;

            ExtractionStarted?.Invoke(this, ("Extraction of new assets started...", totalFiles));

            // Create a unique destination directory for this extraction session
            _directoriesCreator.GenerateNewSubAssetsDownloadedPath();
            string destinationRootPath = _directoriesCreator.SubAssetsDownloadedPath;

            int extractedCount = 0;

            _logService.Log($"Starting extraction of {totalFiles} new assets.");

            try
            {
                foreach (var diff in newDiffs)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    extractedCount++;
                    string progressMessage = $"{diff.FileName}";
                    ExtractionProgressChanged?.Invoke(this, (extractedCount, totalFiles, progressMessage));

                    string sourceWadFullPath = Path.Combine(newLolPath, diff.SourceWadFile);
                    var node = new FileSystemNodeModel(diff.FileName, false, diff.NewPath, sourceWadFullPath)
                    {
                        SourceChunkPathHash = diff.NewPathHash,
                        Status = DiffStatus.New
                    };

                    string fileDestinationDirectory;
                    if (_appSettings.OrganizeExtractedAssets)
                    {
                        fileDestinationDirectory = Path.Combine(destinationRootPath, Path.GetDirectoryName(diff.NewPath));
                    }
                    else
                    {
                        fileDestinationDirectory = destinationRootPath;
                    }

                    Directory.CreateDirectory(fileDestinationDirectory);

                    string extension = Path.GetExtension(node.Name).ToLower();

                    if (extension == ".wpk" || extension == ".bnk")
                    {
                        // Raw copy for audio banks, ensuring it goes to the correct subdirectory
                        await _wadExtractionService.ExtractNodeAsync(node, fileDestinationDirectory, cancellationToken);
                    }
                    else
                    {
                        // Smart saving for all other files, ensuring it goes to the correct subdirectory
                        await _wadSavingService.ProcessAndSaveAsync(node, fileDestinationDirectory, null, newLolPath, cancellationToken);
                    }
                }
                _logService.LogInteractiveSuccess($"Extraction completed of {extractedCount} assets in {_directoriesCreator.SubAssetsDownloadedPath}", _directoriesCreator.SubAssetsDownloadedPath, _directoriesCreator.SubAssetsDownloadedPath);
            }
            catch (OperationCanceledException)
            {
                _logService.LogWarning("Extraction was cancelled by the user.");
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "An unexpected error occurred during extraction.");
            }
            finally
            {
                ExtractionCompleted?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}

