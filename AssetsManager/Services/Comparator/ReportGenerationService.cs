using AssetsManager.Services.Core;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Wad;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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

        public async Task GenerateReportAsync(List<SerializableChunkDiff> diffs)
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

            foreach (var diff in filteredDiffs)
            {
                string line = $"[{diff.Type}] ";
                switch (diff.Type)
                {
                    case ChunkDiffType.New:
                        line += diff.NewPath;
                        break;
                    case ChunkDiffType.Removed:
                        line += diff.OldPath;
                        break;
                    case ChunkDiffType.Renamed:
                        line += $"{diff.OldPath} -> {diff.NewPath}";
                        break;
                    case ChunkDiffType.Modified:
                        line += diff.NewPath;
                        break;
                }
                reportContent.AppendLine(line);
            }

            await File.WriteAllTextAsync(logFilePath, reportContent.ToString());

            _logService.LogInteractiveSuccess($"Successfully generated asset report with {filteredDiffs.Count} entries at ", logFilePath, logFilePath);
        }
    }
}
