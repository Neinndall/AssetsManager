using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using LeagueToolkit.Core.Wad;
using AssetsManager.Views.Models.Wad;

namespace AssetsManager.Services.Comparator
{
    public class ReportGenerationService
    {
        private readonly AppSettings _appSettings;
        private readonly LogService _logService;
        private readonly DirectoriesCreator _directoriesCreator;

        public ReportGenerationService(AppSettings appSettings, LogService logService, DirectoriesCreator directoriesCreator)
        {
            _appSettings = appSettings;
            _logService = logService;
            _directoriesCreator = directoriesCreator;
        }

        public async Task GenerateReportAsync(List<SerializableChunkDiff> diffs, string oldDirectory, string newDirectory)
        {
            if (diffs == null || !diffs.Any())
            {
                return;
            }

            var filteredDiffs = new List<SerializableChunkDiff>();
            if (_appSettings.ReportGeneration.FilterNew)
                filteredDiffs.AddRange(diffs.Where(d => d.Type == ChunkDiffType.New));
            if (_appSettings.ReportGeneration.FilterModified)
                filteredDiffs.AddRange(diffs.Where(d => d.Type == ChunkDiffType.Modified));
            if (_appSettings.ReportGeneration.FilterRenamed)
                filteredDiffs.AddRange(diffs.Where(d => d.Type == ChunkDiffType.Renamed));
            if (_appSettings.ReportGeneration.FilterRemoved)
                filteredDiffs.AddRange(diffs.Where(d => d.Type == ChunkDiffType.Removed));

            if (!filteredDiffs.Any())
            {
                _logService.Log("No assets matched the selected filters for the report.");
                return;
            }

            _directoriesCreator.GenerateNewSubAssetsDownloadedPath();
            string destinationPath = _directoriesCreator.SubAssetsDownloadedPath;
            string logFilePath = Path.Combine(destinationPath, "assets.log");

            var reportContent = new StringBuilder();
            reportContent.AppendLine($"Asset Report generated on {DateTime.Now}");
            reportContent.AppendLine("========================================");
            reportContent.AppendLine();

            string GetPathWithExtension(string path, ulong hash, string sourceWadFile, string baseDirectory)
            {
                if (Path.HasExtension(path) || string.IsNullOrEmpty(baseDirectory) || hash == 0)
                {
                    return path;
                }

                string wadPath = Path.Combine(baseDirectory, sourceWadFile);
                if (!File.Exists(wadPath))
                {
                    return path;
                }

                try
                {
                    using var wadFile = new WadFile(wadPath);
                    if (wadFile.Chunks.TryGetValue(hash, out WadChunk chunk))
                    {
                        using var decompressedChunk = wadFile.LoadChunkDecompressed(chunk);
                        var extension = FileTypeDetector.GuessExtension(decompressedChunk.Span);
                        if (!string.IsNullOrEmpty(extension))
                        {
                            return $"{path}.{extension}";
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logService.LogWarning($"Could not process chunk {hash:x16} from {wadPath}: {ex.Message}");
                }

                return path;
            }

            foreach (var diff in filteredDiffs)
            {
                string line = $"[{diff.Type}] ";
                switch (diff.Type)
                {
                    case ChunkDiffType.New:
                        line += GetPathWithExtension(diff.NewPath, diff.NewPathHash, diff.SourceWadFile, newDirectory);
                        break;
                    case ChunkDiffType.Removed:
                        line += GetPathWithExtension(diff.OldPath, diff.OldPathHash, diff.SourceWadFile, oldDirectory);
                        break;
                    case ChunkDiffType.Renamed:
                        var oldPathWithExt = GetPathWithExtension(diff.OldPath, diff.OldPathHash, diff.SourceWadFile, oldDirectory);
                        var newPathWithExt = GetPathWithExtension(diff.NewPath, diff.NewPathHash, diff.SourceWadFile, newDirectory);
                        line += $"{oldPathWithExt} -> {newPathWithExt}";
                        break;
                    case ChunkDiffType.Modified:
                        line += GetPathWithExtension(diff.NewPath, diff.NewPathHash, diff.SourceWadFile, newDirectory);
                        break;
                }
                reportContent.AppendLine(line);
            }

            await File.WriteAllTextAsync(logFilePath, reportContent.ToString());

            _logService.LogInteractiveSuccess($"Successfully generated asset report with {filteredDiffs.Count} entries at", logFilePath, logFilePath);
        }
    }
}
