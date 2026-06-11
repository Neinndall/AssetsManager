using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Comparator;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Monitor;
using AssetsManager.Views.Models.Wad;

namespace AssetsManager.Services.Monitor
{
    public record ArchiveResult(bool AlreadyArchived, string ReferenceId);

    public class ComparisonHistoryService
    {
        private readonly WadPackagingService _wadPackagingService;
        private readonly AppSettings _appSettings;
        private readonly DirectoriesCreator _directoriesCreator;
        private readonly LogService _logService;

        // Serializes check+archive+register so a background auto-archive and a
        // manual Save click can't race during the SaveBackupAsync I/O await
        // and create two physical folders sharing the same ComparisonKey.
        private readonly SemaphoreSlim _archiveLock = new SemaphoreSlim(1, 1);

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

        /// <summary>
        /// Centralized entry point for archiving a comparison. Builds a stable
        /// identity key from (version + oldPath + newPath). If a previous entry
        /// with the same key already exists in history, returns its
        /// <see cref="HistoryEntry.ReferenceId"/> without writing new files.
        /// Otherwise, packages the backup and registers a new history entry.
        /// </summary>
        public async Task<ArchiveResult> EnsureArchivedAsync(
            List<SerializableChunkDiff> diffs,
            string oldPbePath,
            string newPbePath,
            string version,
            string displayName)
        {
            if (diffs == null || diffs.Count == 0)
            {
                return new ArchiveResult(true, null);
            }

            string comparisonKey = PathUtils.BuildComparisonKey(version, oldPbePath, newPbePath);

            await _archiveLock.WaitAsync();
            try
            {
                // Re-check inside the lock: a background auto-archive and a
                // manual Save click can race during the SaveBackupAsync I/O await.
                string existingReferenceId = FindExistingReferenceId(comparisonKey);

                if (existingReferenceId != null)
                {
                    string archivePath = Path.Combine(_directoriesCreator.WadComparisonSavePath, existingReferenceId);

                    if (Directory.Exists(archivePath))
                    {
                        _logService.LogInteractiveInfo("Comparison already archived as", archivePath, $"{existingReferenceId}.");
                        return new ArchiveResult(true, existingReferenceId);
                    }

                    // Orphan reference: the index still points to a previous archive
                    // whose physical folder was removed from disk. Clean up the stale
                    // entry and fall through to package a fresh snapshot below.
                    _logService.LogWarning($"Previous archive '{existingReferenceId}' is missing from disk. Removing the orphan reference and creating a fresh snapshot.");
                    RemoveOrphanEntry(comparisonKey, existingReferenceId);
                }

                var folderInfo = _directoriesCreator.GetNewWadComparisonFolderInfo();
                await _wadPackagingService.SaveBackupAsync(diffs, oldPbePath, newPbePath, folderInfo.PhysicalPath, version);

                RegisterInHistory(folderInfo.FolderName, displayName, oldPbePath, newPbePath, version, comparisonKey);
                _logService.LogSuccess("Comparison history saved successfully.");

                return new ArchiveResult(false, folderInfo.FolderName);
            }
            finally
            {
                _archiveLock.Release();
            }
        }

        // Returns the dedup key for a history entry, recomputing it on the fly
        // for legacy entries that predate the ComparisonKey field.
        private static string ResolveEntryKey(HistoryEntry entry)
        {
            if (!string.IsNullOrEmpty(entry.ComparisonKey))
            {
                return entry.ComparisonKey;
            }

            return PathUtils.BuildComparisonKey(entry.Version, entry.OldFilePath, entry.NewFilePath);
        }

        // Returns the ReferenceId of the first history entry whose dedup key
        // matches comparisonKey, or null if none matches.
        private string FindExistingReferenceId(string comparisonKey)
        {
            return _appSettings.DiffHistory
                .FirstOrDefault(h => (h.Type == HistoryEntryType.WadArchive || h.Type == HistoryEntryType.WadFile)
                                     && !string.IsNullOrEmpty(h.OldFilePath)
                                     && !string.IsNullOrEmpty(h.NewFilePath)
                                     && ResolveEntryKey(h) == comparisonKey)
                ?.ReferenceId;
        }

        // Removes a history entry whose physical archive folder is missing from
        // disk. Called from EnsureArchivedAsync when a dedup hit resolves to a
        // ghost reference. The SemaphoreSlim in the caller guarantees we don't
        // race with concurrent reads/writes on DiffHistory. Persistence is
        // deferred to the RegisterInHistory call that follows, which performs
        // a single Save() with both the removal and the new entry in one write.
        private void RemoveOrphanEntry(string comparisonKey, string referenceId)
        {
            try
            {
                var orphan = _appSettings.DiffHistory
                    .FirstOrDefault(h => (h.Type == HistoryEntryType.WadArchive || h.Type == HistoryEntryType.WadFile)
                                         && h.ReferenceId == referenceId
                                         && ResolveEntryKey(h) == comparisonKey);

                if (orphan != null)
                {
                    _appSettings.DiffHistory.Remove(orphan);
                }
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Failed to remove orphan history reference '{referenceId}'.");
            }
        }

        private void RegisterInHistory(string folderName, string comparisonDisplayName, string oldPbePath, string newPbePath, string version, string comparisonKey)
        {
            var entryType = HistoryEntryType.WadArchive;
            
            // Detect Individual WAD mode by path
            if (newPbePath.EndsWith(".wad.client", StringComparison.OrdinalIgnoreCase) || 
                newPbePath.EndsWith(".wad", StringComparison.OrdinalIgnoreCase))
            {
                entryType = HistoryEntryType.WadFile;
            }

            var entry = new HistoryEntry
            {
                FileName = comparisonDisplayName,
                DisplayName = comparisonDisplayName,
                Version = version,
                OldFilePath = oldPbePath,
                NewFilePath = newPbePath,
                Timestamp = DateTime.Now,
                Type = entryType,
                ReferenceId = folderName,
                ComparisonKey = comparisonKey
            };

            _appSettings.DiffHistory.Insert(0, entry);
            _appSettings.Save();
        }

        public async Task<(WadComparisonData Data, string Path)> LoadComparisonAsync(string referenceId)
        {
            try
            {
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

        public async Task<int> SyncOrphanedArchivesAsync()
        {
            int recoveredCount = 0;
            
            try
            {
                if (!Directory.Exists(_directoriesCreator.WadComparisonSavePath))
                {
                    return 0;
                }

                var existingReferenceIds = new HashSet<string>(
                    _appSettings.DiffHistory
                        .Where(h => (h.Type == HistoryEntryType.WadArchive || h.Type == HistoryEntryType.WadFile) && !string.IsNullOrEmpty(h.ReferenceId))
                        .Select(h => h.ReferenceId),
                    StringComparer.OrdinalIgnoreCase
                );

                var archiveDirs = Directory.GetDirectories(_directoriesCreator.WadComparisonSavePath);

                bool changesMade = false;

                foreach (var dir in archiveDirs)
                {
                    string folderName = Path.GetFileName(dir);

                    if (!existingReferenceIds.Contains(folderName))
                    {
                        var (data, indexFilePath) = await LoadComparisonAsync(folderName);
                        
                        if (data != null)
                        {
                            // Try to parse timestamp from folder name (e.g., comparison_27052026_153000)
                            DateTime timestamp = Directory.GetCreationTime(dir);
                            if (folderName.StartsWith("comparison_") && folderName.Length >= 26)
                            {
                                string dateStr = folderName.Substring(11, 15);
                                if (DateTime.TryParseExact(dateStr, "ddMMyyyy_HHmmss", null, System.Globalization.DateTimeStyles.None, out DateTime parsedDate))
                                {
                                    timestamp = parsedDate;
                                }
                            }

                            string oldPath = data.OldLolPath ?? "Unknown Path";
                            string newPath = data.NewLolPath ?? "Unknown Path";
                            
                            string version = data.Version;
                            if (string.IsNullOrEmpty(version))
                            {
                                version = "Unknown Version";
                            }

                            string comparisonKey = PathUtils.BuildComparisonKey(version, oldPath, newPath);

                            // Determine DisplayName based on the path
                            string displayName = "Recovered Comparison";
                            
                            // Check if it's an individual WAD file comparison
                            var entryType = HistoryEntryType.WadArchive;
                            if (newPath.EndsWith(".wad.client", StringComparison.OrdinalIgnoreCase) || newPath.EndsWith(".wad", StringComparison.OrdinalIgnoreCase))
                            {
                                displayName = Path.GetFileName(newPath);
                                entryType = HistoryEntryType.WadFile;
                            }
                            else if (newPath.Contains("League of Legends (PBE)", StringComparison.OrdinalIgnoreCase))
                            {
                                displayName = "League of Legends (PBE)";
                            }
                            else if (newPath.Contains("League of Legends", StringComparison.OrdinalIgnoreCase))
                            {
                                displayName = "League of Legends";
                            }

                            var entry = new HistoryEntry
                            {
                                FileName = displayName,
                                DisplayName = displayName,
                                Version = version,
                                OldFilePath = oldPath,
                                NewFilePath = newPath,
                                Timestamp = timestamp,
                                Type = entryType,
                                ReferenceId = folderName,
                                ComparisonKey = comparisonKey
                            };

                            _appSettings.DiffHistory.Add(entry);
                            changesMade = true;
                            recoveredCount++;
                        }
                    }
                }

                if (changesMade)
                {
                    // Sort by timestamp descending
                    var sortedHistory = _appSettings.DiffHistory.OrderByDescending(h => h.Timestamp).ToList();
                    _appSettings.DiffHistory.Clear();
                    foreach (var h in sortedHistory)
                    {
                        _appSettings.DiffHistory.Add(h);
                    }
                    _appSettings.Save();
                }
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to synchronize orphaned archives.");
            }

            return recoveredCount;
        }

        public void DeleteComparison(HistoryEntry entry)
        {
            try
            {
                if ((entry.Type == HistoryEntryType.WadArchive || entry.Type == HistoryEntryType.WadFile) && !string.IsNullOrEmpty(entry.ReferenceId))
                {
                    string historyDir = Path.Combine(_directoriesCreator.WadComparisonSavePath, entry.ReferenceId);
                    if (Directory.Exists(historyDir))
                    {
                        Directory.Delete(historyDir, true);
                    }

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
