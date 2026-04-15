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

namespace AssetsManager.Services.Monitor
{
    public class AssetWatcherService
    {
        private readonly AppSettings _appSettings;
        private readonly LogService _logService;
        private readonly DirectoriesCreator _directoriesCreator;
        private readonly WadContentProvider _wadContentProvider;

        private static readonly FieldInfo _checksumField = typeof(WadChunk).GetField("_checksum", BindingFlags.NonPublic | BindingFlags.Instance);

        public event Action<MonitoredAsset> AssetUpdated;

        public AssetWatcherService(AppSettings appSettings, LogService logService, DirectoriesCreator directoriesCreator, WadContentProvider wadContentProvider)
        {
            _appSettings = appSettings;
            _logService = logService;
            _directoriesCreator = directoriesCreator;
            _wadContentProvider = wadContentProvider;
        }

        public async Task<bool> CheckAssetsAsync(IEnumerable<MonitoredAsset> assets, bool silent = false)
        {
            if (assets == null || !assets.Any()) return false;

            if (!silent) _logService.Log("Checking monitored assets for updates...");
            bool anyUpdated = false;

            foreach (var asset in assets)
            {
                try
                {
                    // Don't overwrite pending check or updated status if we are already in that state in UI
                    if (asset.Status != AssetStatus.Updated)
                    {
                        asset.Status = AssetStatus.Pending;
                        asset.StatusColor = (SolidColorBrush)Application.Current.FindResource("AccentBrush");
                    }

                    string fullWadPath = GetFullWadPath(asset);
                    if (string.IsNullOrEmpty(fullWadPath) || !File.Exists(fullWadPath))
                    {
                        _logService.LogWarning($"WAD file not found for asset '{asset.Alias}': {fullWadPath}");
                        asset.Status = AssetStatus.Error;
                        asset.StatusColor = (SolidColorBrush)Application.Current.FindResource("AccentRed");
                        continue;
                    }

                    using var wadFile = new WadFile(fullWadPath);
                    ulong pathHash = XxHash64Ext.Hash(asset.InternalPath.ToLower());
                    
                    if (!wadFile.Chunks.TryGetValue(pathHash, out var chunk))
                    {
                        _logService.LogWarning($"Asset '{asset.InternalPath}' not found inside WAD '{asset.WadName}'");
                        asset.Status = AssetStatus.Error;
                        asset.StatusColor = (SolidColorBrush)Application.Current.FindResource("AccentRed");
                        continue;
                    }

                    // En WAD v3+, el checksum es XXHash64 del contenido (private field _checksum)
                    ulong currentChecksum = _checksumField != null ? (ulong)_checksumField.GetValue(chunk) : 0;

                    if (asset.LastKnownHash == 0)
                    {
                        // First time adding, just save current state and extract initial version
                        await ProcessAssetUpdate(asset, wadFile, chunk, currentChecksum, true);
                    }
                    else if (asset.LastKnownHash != currentChecksum)
                    {
                        // Change detected!
                        await ProcessAssetUpdate(asset, wadFile, chunk, currentChecksum, false);
                        anyUpdated = true;
                    }
                    else
                    {
                        // No changes found in this check. 
                        // CRITICAL: Only set to UpToDate if it wasn't already marked as Updated 
                        // (prevents auto-resetting before user sees it)
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
                    }
                }
                catch (Exception ex)
                {
                    _logService.LogError(ex, $"Error checking asset: {asset.AssetPath}");
                    asset.Status = AssetStatus.Error;
                    asset.StatusColor = (SolidColorBrush)Application.Current.FindResource("AccentRed");
                }
            }

            if (anyUpdated)
            {
                _appSettings.Save();
                if (!silent) _logService.LogSuccess("Some monitored assets have been updated!");
            }
            else if (!silent)
            {
                _logService.Log("All monitored assets are up-to-date.");
            }

            return anyUpdated;
        }

        private async Task ProcessAssetUpdate(MonitoredAsset asset, WadFile wadFile, WadChunk chunk, ulong checksum, bool isInitial)
        {
            string cleanAlias = asset.Alias.Replace("/", "_").Replace("\\", "_");
            string oldPath = Path.Combine(_directoriesCreator.WatcherCacheOldPath, cleanAlias);
            string newPath = Path.Combine(_directoriesCreator.WatcherCacheNewPath, cleanAlias);

            _directoriesCreator.CreateDirectory(Path.GetDirectoryName(oldPath));
            _directoriesCreator.CreateDirectory(Path.GetDirectoryName(newPath));

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

            asset.LastKnownHash = checksum;
            asset.LastUpdated = DateTime.Now;
            asset.Status = isInitial ? AssetStatus.UpToDate : AssetStatus.Updated;
            asset.StatusColor = (SolidColorBrush)Application.Current.FindResource(isInitial ? "AccentGreen" : "AccentBlue");
            asset.HasChanges = !isInitial;
            asset.OldFilePath = oldPath;
            asset.NewFilePath = newPath;

            if (!isInitial)
            {
                AssetUpdated?.Invoke(asset);
            }
        }

        private string GetFullWadPath(MonitoredAsset asset)
        {
            string baseDir = _appSettings.LolPbeDirectory;
            if (string.IsNullOrEmpty(baseDir)) return null;

            // 1. Try direct combined path (Relative or Absolute)
            string fullPath = Path.IsPathRooted(asset.WadName) 
                ? asset.WadName 
                : Path.Combine(baseDir, asset.WadName);

            if (File.Exists(fullPath)) return fullPath;

            // 2. Try common Riot locations
            string gameDataPath = Path.Combine(baseDir, "Game", "DATA", "Final", Path.GetFileName(asset.WadName));
            if (File.Exists(gameDataPath)) return gameDataPath;

            string pluginsPath = Path.Combine(baseDir, "Plugins", Path.GetFileName(asset.WadName));
            if (File.Exists(pluginsPath)) return pluginsPath;

            // 3. Last resort: Smart Recursive Search
            try
            {
                string fileName = Path.GetFileName(asset.WadName);
                var foundFiles = Directory.GetFiles(baseDir, fileName, SearchOption.AllDirectories);
                if (foundFiles.Length > 0)
                {
                    // Update the asset WadName for future checks to avoid searching again
                    asset.WadName = foundFiles[0].Substring(baseDir.Length).TrimStart('/', '\\');
                    return foundFiles[0];
                }
            }
            catch { /* Ignore search errors */ }

            return null;
        }
    }
}
