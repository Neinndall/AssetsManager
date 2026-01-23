using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Threading.Tasks;
using AssetsManager.Services.Comparator;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Monitor;
using AssetsManager.Views.Models.Wad;

namespace AssetsManager.Services.Monitor
{
    public class ComparisonHistoryService
    {
        private readonly WadPackagingService _wadPackagingService;
        private readonly AppSettings _appSettings;
        private readonly DirectoriesCreator _directoriesCreator;
        private readonly LogService _logService;

        public ComparisonHistoryService(
            WadPackagingService wadPackagingService, 
            AppSettings appSettings, 
            DirectoriesCreator directoriesCreator, 
            LogService logService)
        {
            _wadPackagingService = wadPackagingService;
            _appSettings = appSettings;
            _directoriesCreator = directoriesCreator;
            _logService = logService;
        }

        public async Task SaveComparisonAsync(List<SerializableChunkDiff> diffs, string oldPbePath, string newPbePath, string comparisonDisplayName)
        {
            try
            {
                // 1. Generate ID and Paths
                string id = Guid.NewGuid().ToString();
                string historyDir = Path.Combine(_directoriesCreator.HistoryCachePath, id);
                string wadChunksOldDir = Path.Combine(historyDir, "wad_chunks", "old");
                string wadChunksNewDir = Path.Combine(historyDir, "wad_chunks", "new");
                string indexFilePath = Path.Combine(historyDir, "index.json");

                // Ensure directories exist
                Directory.CreateDirectory(wadChunksOldDir);
                Directory.CreateDirectory(wadChunksNewDir);

                _logService.LogDebug($"[ComparisonHistoryService] Saving comparison {id} to {historyDir}");

                // 2. Package chunks (extract from WADs to history folder)
                // We use the packaging service to get a "lean" list of diffs with dependencies resolved
                // and to save the physical chunk files to our new history directory.
                var leanDiffs = await _wadPackagingService.CreateLeanWadPackageAsync(
                    diffs, 
                    oldPbePath, 
                    newPbePath, 
                    wadChunksOldDir, 
                    wadChunksNewDir
                );

                // 3. Serialize the data
                var comparisonData = new WadComparisonData
                {
                    OldLolPath = oldPbePath,
                    NewLolPath = newPbePath,
                    Diffs = leanDiffs
                };

                string json = JsonSerializer.Serialize(comparisonData, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(indexFilePath, json);

                // 4. Update AppSettings
                var entry = new HistoryEntry
                {
                    FileName = comparisonDisplayName, // e.g. "Patch 14.1 vs 14.2"
                    OldFilePath = oldPbePath, // Informational
                    NewFilePath = newPbePath, // Informational
                    Timestamp = DateTime.Now,
                    Type = HistoryEntryType.WadComparison,
                    ReferenceId = id
                };

                _appSettings.DiffHistory.Insert(0, entry); // Add to top
                AppSettings.SaveSettings(_appSettings);

                _logService.LogSuccess("Comparison history saved successfully.");
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to save comparison history.");
            }
        }

        public async Task<(WadComparisonData Data, string Path)> LoadComparisonAsync(string referenceId)
        {
            try
            {
                string historyDir = Path.Combine(_directoriesCreator.HistoryCachePath, referenceId);
                string indexFilePath = Path.Combine(historyDir, "index.json");

                if (!File.Exists(indexFilePath))
                {
                    _logService.LogError($"History index file not found: {indexFilePath}");
                    return (null, null);
                }

                string json = await File.ReadAllTextAsync(indexFilePath);
                var data = JsonSerializer.Deserialize<WadComparisonData>(json);

                return (data, indexFilePath);
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Failed to load comparison history {referenceId}.");
                return (null, null);
            }
        }

        public void DeleteComparison(HistoryEntry entry)
        {
            try
            {
                if (entry.Type == HistoryEntryType.WadComparison && !string.IsNullOrEmpty(entry.ReferenceId))
                {
                    string historyDir = Path.Combine(_directoriesCreator.HistoryCachePath, entry.ReferenceId);
                    if (Directory.Exists(historyDir))
                    {
                        Directory.Delete(historyDir, true);
                        _logService.LogDebug($"Deleted history directory: {historyDir}");
                    }
                }
                
                // Also handle legacy FileDiff deletions if needed, though HistoryViewControl handles that manually currently.
                // We'll leave the existing logic in HistoryViewControl for FileDiffs for now or refactor it later.
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Failed to delete comparison history {entry.ReferenceId}.");
            }
        }
    }
}
