using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
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

        public void RegisterComparisonInHistory(string folderName, string comparisonDisplayName, string oldPbePath, string newPbePath)
        {
            try
            {
                // Ensure we don't add duplicates based on the folder name (ReferenceId)
                bool alreadyInHistory = _appSettings.DiffHistory.Any(h => h.ReferenceId == folderName);
                if (!alreadyInHistory)
                {
                    var entry = new HistoryEntry
                    {
                        FileName = comparisonDisplayName,
                        OldFilePath = oldPbePath,
                        NewFilePath = newPbePath,
                        Timestamp = DateTime.Now,
                        Type = HistoryEntryType.WadComparison,
                        ReferenceId = folderName
                    };

                    _appSettings.DiffHistory.Insert(0, entry);
                    _appSettings.Save();
                    _logService.LogSuccess("Comparison history saved successfully.");
                }
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to register comparison in history.");
            }
        }

        public async Task<(WadComparisonData Data, string Path)> LoadComparisonAsync(string referenceId)
        {
            try
            {
                // Centralize: Look into WadComparisonSavePath
                string historyDir = Path.Combine(_directoriesCreator.WadComparisonSavePath, referenceId);
                string indexFilePath = Path.Combine(historyDir, "wadcomparison.json");

                if (!File.Exists(indexFilePath))
                {
                    return (null, null);
                }

                string json = await File.ReadAllTextAsync(indexFilePath);
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    Converters = { new JsonStringEnumConverter() }
                };
                var data = JsonSerializer.Deserialize<WadComparisonData>(json, options);

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
                    // 1. Delete physical directory
                    string historyDir = Path.Combine(_directoriesCreator.WadComparisonSavePath, entry.ReferenceId);
                    if (Directory.Exists(historyDir))
                    {
                        Directory.Delete(historyDir, true);
                    }

                    // 2. Remove from internal list
                    _appSettings.DiffHistory.Remove(entry);
                    _appSettings.Save();

                    _logService.LogSuccess("Comparison results and related files deleted successfully.");
                }
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Failed to delete comparison history {entry.ReferenceId}.");
            }
        }
    }
}
