using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Monitor;
using AssetsManager.Utils;

namespace AssetsManager.Services.Downloads
{
    public class Status
    {
        // Game Hashes
        private const string GAME_HASHES_FILENAME = "hashes.game.txt";
        private const string LCU_HASHES_FILENAME = "hashes.lcu.txt";

        // Bin Hashes
        private const string HASHES_BINENTRIES = "hashes.binentries.txt";
        private const string HASHES_BINFIELDS = "hashes.binfields.txt";
        private const string HASHES_BINHASHES = "hashes.binhashes.txt";
        private const string HASHES_BINTYPES = "hashes.bintypes.txt";

        private readonly LogService _logService;
        private readonly Requests _requests;
        private readonly AppSettings _appSettings;
        private readonly JsonDataService _jsonDataService;
        private readonly DirectoriesCreator _directoriesCreator;

        public Status(
            LogService logService,
            Requests requests,
            AppSettings appSettings,
            JsonDataService jsonDataService,
            DirectoriesCreator directoriesCreator)
        {
            _logService = logService;
            _requests = requests;
            _appSettings = appSettings;
            _jsonDataService = jsonDataService;
            _directoriesCreator = directoriesCreator;
        }

        public async Task<bool> SyncHashesIfNeeds(bool syncHashesWithCDTB, bool silent = false, Action onUpdateFound = null)
        {
            bool isUpdated = await IsUpdatedAsync(silent, onUpdateFound);
            if (isUpdated)
            {
                if (!silent) _logService.Log("Server updated or local files out of date. Starting hash synchronization...");
                await _requests.SyncHashesIfEnabledAsync(syncHashesWithCDTB);

                UpdateConfigWithLocalFileSizes();

                if (!silent) _logService.LogSuccess("Synchronization completed.");
                return true;
            }

            if (!silent) _logService.Log("No server updates found. Local hashes are up-to-date.");
            return false;
        }

        private void UpdateConfigWithLocalFileSizes()
        {
            var newHashesPath = _directoriesCreator.HashesNewPath;
            if (!Directory.Exists(newHashesPath))
            {
                _logService.LogWarning($"Cannot update hash config: 'hashes/new' directory not found at '{newHashesPath}'.");
                return;
            }

            var newSizes = new Dictionary<string, long>();
            var allKnownFiles = new[] {
                GAME_HASHES_FILENAME, LCU_HASHES_FILENAME, HASHES_BINENTRIES,
                HASHES_BINFIELDS, HASHES_BINHASHES, HASHES_BINTYPES
            };

            foreach (var filename in allKnownFiles)
            {
                var filePath = Path.Combine(newHashesPath, filename);
                if (File.Exists(filePath))
                {
                    newSizes[filename] = new FileInfo(filePath).Length;
                }
                else
                {
                    newSizes[filename] = 0;
                }
            }

            _appSettings.HashesSizes = newSizes;
            AppSettings.SaveSettings(_appSettings);
        }

        private bool AreLocalFilesOutOfSync()
        {
            var configSizes = _appSettings.HashesSizes;
            var newHashesPath = _directoriesCreator.HashesNewPath;

            if (configSizes == null || configSizes.Count == 0)
            {
                return false; // Nothing in config to check against.
            }

            if (string.IsNullOrEmpty(newHashesPath) || !Directory.Exists(newHashesPath))
            {
                _logService.LogWarning($"Skipping local file sync check: Hashes/new directory path is invalid or not found ('{newHashesPath}').");
                return false; // Path is not valid, cannot check.
            }

            foreach (var entry in configSizes)
            {
                string filename = entry.Key;
                long configSize = entry.Value;
                string filePath = Path.Combine(newHashesPath, filename);

                if (!File.Exists(filePath))
                {
                    if (configSize > 0) return true;
                }
                else
                {
                    long diskSize = new FileInfo(filePath).Length;
                    if (configSize != diskSize) return true;
                }
            }

            return false; 
        }

        public async Task<bool> IsUpdatedAsync(bool silent = false, Action onUpdateFound = null)
        {
            if (AreLocalFilesOutOfSync())
            {
                onUpdateFound?.Invoke();
                return true;
            }

            try
            {
                if (!silent) _logService.Log("Getting update sizes from server...");
                var serverSizes = await _jsonDataService.GetRemoteHashesSizesAsync();

                if (serverSizes == null || serverSizes.Count == 0)
                {
                    _logService.LogWarning("Could not retrieve remote hash sizes or received an empty list. Skipping update check.");
                    return false;
                }

                var localSizes = _appSettings.HashesSizes ?? new Dictionary<string, long>();
                bool updated = false;
                bool notificationSent = false;

                void CheckAndUpdate(string filename)
                {
                    if (UpdateHashSizeIfDifferent(serverSizes, localSizes, filename))
                    {
                        updated = true;
                        if (!notificationSent)
                        {
                            onUpdateFound?.Invoke();
                            notificationSent = true;
                        }
                    }
                }

                CheckAndUpdate(GAME_HASHES_FILENAME);
                CheckAndUpdate(LCU_HASHES_FILENAME);
                CheckAndUpdate(HASHES_BINENTRIES);
                CheckAndUpdate(HASHES_BINFIELDS);
                CheckAndUpdate(HASHES_BINHASHES);
                CheckAndUpdate(HASHES_BINTYPES);

                if (updated)
                {
                    _appSettings.HashesSizes = localSizes;
                    AppSettings.SaveSettings(_appSettings);
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Error checking for updates.");
                return false;
            }
        }

        private bool UpdateHashSizeIfDifferent(
            Dictionary<string, long> serverSizes,
            Dictionary<string, long> localSizes,
            string filename)
        {
            long serverSize = serverSizes.GetValueOrDefault(filename, 0);
            long localSize = localSizes.GetValueOrDefault(filename, 0);

            if (serverSize != localSize)
            {
                localSizes[filename] = serverSize;
                return true;
            }

            return false;
        }
    }
}