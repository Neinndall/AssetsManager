using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;
using AssetsManager.Utils;
using AssetsManager.Services.Core;
using AssetsManager.Services.Explorer;
using AssetsManager.Services.Hashes;
using AssetsManager.Views.Models.Monitor;
using AssetsManager.Views.Models.Settings;

namespace AssetsManager.Services.Monitor
{
    public class AssetWatcherService
    {
        private readonly AppSettings _appSettings;
        private readonly LogService _logService;
        private readonly DirectoriesCreator _directoriesCreator;
        private readonly WadContentProvider _wadContentProvider;
        private readonly VersionService _versionService;

        private static readonly FieldInfo _checksumField = typeof(WadChunk).GetField("_checksum", BindingFlags.NonPublic | BindingFlags.Instance);

        public event Action<MonitoredAsset> AssetUpdated;

        public AssetWatcherService(AppSettings appSettings, LogService logService, DirectoriesCreator directoriesCreator, WadContentProvider wadContentProvider, VersionService versionService)
        {
            _appSettings = appSettings;
            _logService = logService;
            _directoriesCreator = directoriesCreator;
            _wadContentProvider = wadContentProvider;
            _versionService = versionService;
        }

        public async Task<(bool anyUpdated, List<string> updatedAssetNames)> CheckAssetsAsync(IEnumerable<MonitoredAsset> assets, bool silent = false)
        {
            if (assets == null || !assets.Any()) return (false, new List<string>());

            if (!silent) _logService.Log("Checking monitored assets for updates...");

            bool anyUpdated = false;
            bool checkPerformed = false;
            bool baseDirWarningLogged = false;
            var updatedAssetNames = new List<string>();

            string baseDir = _appSettings.LolPbeDirectory;
            bool hasBaseDir = !string.IsNullOrEmpty(baseDir) && Directory.Exists(baseDir);

            foreach (var asset in assets)
            {
                try
                {
                    // Don't overwrite pending check or updated status if we are already in that state in UI
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (asset.Status != AssetStatus.Updated)
                        {
                            asset.Status = AssetStatus.Pending;
                            asset.StatusColor = (SolidColorBrush)Application.Current.FindResource("AccentBrush");
                        }
                    });

                    string fullWadPath = GetFullWadPath(asset);
                    
                    if (string.IsNullOrEmpty(fullWadPath) || !File.Exists(fullWadPath))
                    {
                        if (Path.IsPathRooted(asset.WadName))
                        {
                            // It's an absolute path that really doesn't exist
                            _logService.LogWarning($"WAD file not found at path: {asset.WadName}");
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                asset.Status = AssetStatus.Error;
                                asset.StatusColor = (SolidColorBrush)Application.Current.FindResource("AccentRed");
                            });
                        }
                        else if (!hasBaseDir)
                        {
                            // It's a relative path but we don't have a base directory to resolve it
                            if (!silent && !baseDirWarningLogged)
                            {
                                _logService.LogWarning("Some monitored assets have relative paths but PBE Client Directory is not configured in Settings.");
                                baseDirWarningLogged = true;
                            }
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                asset.Status = AssetStatus.Pending;
                                asset.StatusColor = (SolidColorBrush)Application.Current.FindResource("TextMuted");
                            });
                        }
                        else
                        {
                            // It's a relative path, we have a base dir, but still couldn't find the file
                            _logService.LogWarning($"Asset WAD not found in game directory: {asset.WadName}");
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                asset.Status = AssetStatus.Error;
                                asset.StatusColor = (SolidColorBrush)Application.Current.FindResource("AccentRed");
                            });
                        }
                        continue;
                    }

                    checkPerformed = true;
                    using var wadFile = new WadFile(fullWadPath);
                    ulong pathHash = XxHash64Ext.Hash(asset.InternalPath.ToLower());
                    
                    if (!wadFile.Chunks.TryGetValue(pathHash, out var chunk))
                    {
                        _logService.LogWarning($"Asset '{asset.InternalPath}' not found inside WAD '{asset.WadName}'");
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            asset.Status = AssetStatus.Error;
                            asset.StatusColor = (SolidColorBrush)Application.Current.FindResource("AccentRed");
                        });
                        continue;
                    }

                    // En WAD v3+, el checksum es XXHash64 del contenido (private field _checksum)
                    ulong currentChecksum = _checksumField != null ? (ulong)_checksumField.GetValue(chunk) : 0;

                    if (asset.LastKnownHash == 0)
                    {
                        // First time adding, just save current state and extract initial version
                        await ProcessAssetUpdate(asset, wadFile, chunk, currentChecksum, true, fullWadPath);
                    }
                    else if (asset.LastKnownHash != currentChecksum)
                    {
                        // Change detected!
                        await ProcessAssetUpdate(asset, wadFile, chunk, currentChecksum, false, fullWadPath);
                        anyUpdated = true;
                        updatedAssetNames.Add(asset.Alias);
                    }
                    else
                    {
                        // No changes found in this check. 
                        // CRITICAL: Only set to UpToDate if it wasn't already marked as Updated 
                        // (prevents auto-resetting before user sees it)
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            if (!asset.HasChanges)
                            {
                                asset.Status = AssetStatus.UpToDate;
                                asset.StatusColor = (SolidColorBrush)Application.Current.FindResource("AccentGreen");
                            }
                            else
                            {
                                // Keep Updated status until user acknowledges
                                asset.Status = AssetStatus.Updated;
                                asset.StatusColor = (SolidColorBrush)Application.Current.FindResource("AccentBlue");
                            }
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logService.LogError(ex, $"Error checking asset: {asset.AssetPath}");
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        asset.Status = AssetStatus.Error;
                        asset.StatusColor = (SolidColorBrush)Application.Current.FindResource("AccentRed");
                    });
                }
            }

            if (anyUpdated)
            {
				// Necesita la otra llamada para evitar reload tree?
				_appSettings.Save();
				
                if (!silent)
                {
                    _logService.LogSuccess("Assets Watcher files are updated.");
                }
            }
            else if (!silent && checkPerformed)
            {
                _logService.LogSuccess("All monitored assets are up-to-date.");
            }

            return (anyUpdated, updatedAssetNames);
        }

        private async Task ProcessAssetUpdate(MonitoredAsset asset, WadFile wadFile, WadChunk chunk, ulong checksum, bool isInitial, string fullWadPath)
        {
            string cleanAlias = asset.Alias.Replace("/", "_").Replace("\\", "_");
            string oldPath = Path.Combine(_directoriesCreator.WatcherCacheOldPath, cleanAlias);
            string newPath = Path.Combine(_directoriesCreator.WatcherCacheNewPath, cleanAlias);

            _directoriesCreator.CreateDirectory(Path.GetDirectoryName(oldPath));
            _directoriesCreator.CreateDirectory(Path.GetDirectoryName(newPath));

            // Update version if we can (ALWAYS from PBE for the Watcher)
            string currentVersion = asset.Version;
            try
            {
                if (!string.IsNullOrEmpty(_appSettings.LolPbeDirectory))
                {
                    currentVersion = await _versionService.GetGameVersionAsync(_appSettings.LolPbeDirectory) ?? currentVersion;
                }
            }
            catch { }

            if (!isInitial)
            {
                // DETECTED REAL UPDATE
                // Move current "New" to "Old" to allow diffing
                if (File.Exists(newPath))
                {
                    File.Copy(newPath, oldPath, true);
                }
                else
                {
                    File.WriteAllText(oldPath, string.Empty);
                }
            }
            else
            {
                // INITIAL ADDITION
                // Just create empty old and full new
                File.WriteAllText(oldPath, string.Empty);
            }

            // Extract new version from WAD
            using var decompressedDataOwner = wadFile.LoadChunkDecompressed(chunk);
            byte[] newData = decompressedDataOwner.Span.ToArray();
            await File.WriteAllBytesAsync(newPath, newData);

            // Archive to history if enabled
            if (!isInitial && _appSettings.SaveJsonHistory)
            {
                try
                {
                    string historyFolder = _directoriesCreator.GetNewJsonHistoryPath(asset.Alias);
                    string archiveOld = Path.Combine(historyFolder, "old_" + cleanAlias);
                    string archiveNew = Path.Combine(historyFolder, "new_" + cleanAlias);

                    File.Copy(oldPath, archiveOld, true);
                    File.Copy(newPath, archiveNew, true);

                    var entry = new HistoryEntry
                    {
                        FileName = asset.Alias,
                        DisplayName = asset.Alias,
                        Version = currentVersion,
                        OldFilePath = archiveOld,
                        NewFilePath = archiveNew,
                        Timestamp = DateTime.Now,
                        Type = HistoryEntryType.WatcherUpdate,
                        ReferenceId = "Watcher Update"
                    };

                    _appSettings.DiffHistory.Insert(0, entry);
                    // No need to save settings here as it's saved in the caller loop
                }
                catch (Exception ex)
                {
                    _logService.LogError(ex, $"Failed to archive watcher update for {asset.Alias}");
                }
            }

            Application.Current.Dispatcher.Invoke(() =>
            {
                asset.Version = currentVersion;
                asset.LastKnownHash = checksum;
                asset.LastUpdated = DateTime.Now;
                asset.Status = isInitial ? AssetStatus.UpToDate : AssetStatus.Updated;
                asset.StatusColor = (SolidColorBrush)Application.Current.FindResource(isInitial ? "AccentGreen" : "AccentBlue");
                asset.HasChanges = !isInitial;
                asset.OldFilePath = oldPath;
                asset.NewFilePath = newPath;
            });

            if (!isInitial)
            {
                AssetUpdated?.Invoke(asset);
            }
        }

        private string GetFullWadPath(MonitoredAsset asset)
        {
            if (string.IsNullOrEmpty(asset.WadName)) return null;

            // 1. Absolute Path: Trust it.
            if (Path.IsPathRooted(asset.WadName))
            {
                return File.Exists(asset.WadName) ? asset.WadName : null;
            }

            // 2. Relative Path: Resolve it using base directory.
            string baseDir = _appSettings.LolPbeDirectory;
            if (string.IsNullOrEmpty(baseDir)) return null;

            // Combined
            string combined = Path.Combine(baseDir, asset.WadName);
            if (File.Exists(combined)) return combined;

            // Common locations
            string fileName = Path.GetFileName(asset.WadName);
            string gameDataPath = Path.Combine(baseDir, "Game", "DATA", "Final", fileName);
            if (File.Exists(gameDataPath)) return gameDataPath;

            string pluginsPath = Path.Combine(baseDir, "Plugins", fileName);
            if (File.Exists(pluginsPath)) return pluginsPath;

            // Deep search if not in common locations
            try
            {
                string searchPattern = fileName;
                var foundFiles = Directory.GetFiles(baseDir, searchPattern, SearchOption.AllDirectories);
                if (foundFiles.Length > 0)
                {
                    // Update the asset WadName for future checks to avoid searching again
                    asset.WadName = foundFiles[0].Substring(baseDir.Length).TrimStart('/', '\\');
                    return foundFiles[0];
                }
            }
            catch { }

            return null;
        }
    }
}
