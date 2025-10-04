using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Monitor;
using AssetsManager.Utils;

namespace AssetsManager.Services.Downloads
{
    public class Status
    {
        // URL Server where obtain the info for the hashes
        private const string STATUS_URL = "https://raw.communitydragon.org/data/hashes/lol/";

        // Game Hashes
        private const string GAME_HASHES_FILENAME = "hashes.game.txt";
        private const string LCU_HASHES_FILENAME = "hashes.lcu.txt";

        // Bin Hashes
        private const string HASHES_BINENTRIES = "hashes.binentries.txt";
        private const string HASHES_BINFIELDS = "hashes.binfields.txt";
        private const string HASHES_BINHASHES = "hashes.binhashes.txt";
        private const string HASHES_BINTYPES = "hashes.bintypes.txt";

        // Rst Hashes
        private const string HASHES_RST_XXH3 = "hashes.rst.xxh3.txt";
        private const string HASHES_RST_XXH64 = "hashes.rst.xxh64.txt";

        private readonly LogService _logService;
        private readonly Requests _requests;
        private readonly AppSettings _appSettings;
        private readonly HttpClient _httpClient;
        private readonly DirectoriesCreator _directoriesCreator;

        public Status(
            LogService logService,
            Requests requests,
            AppSettings appSettings,
            HttpClient httpClient,
            DirectoriesCreator directoriesCreator)
        {
            _logService = logService;
            _requests = requests;
            _appSettings = appSettings;
            _httpClient = httpClient;
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
                HASHES_BINFIELDS, HASHES_BINHASHES, HASHES_BINTYPES,
                HASHES_RST_XXH3, HASHES_RST_XXH64
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
                var serverSizes = await GetRemoteHashesSizesAsync();

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
                CheckAndUpdate(HASHES_RST_XXH3);
                CheckAndUpdate(HASHES_RST_XXH64);

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

        public async Task<Dictionary<string, long>> GetRemoteHashesSizesAsync()
        {
            var result = new Dictionary<string, long>();

            if (_httpClient == null)
            { 
                _logService.LogError("HttpClient is null. Cannot fetch remote sizes.");
                return result;
            }

            if (string.IsNullOrEmpty(STATUS_URL))
            {
                _logService.LogError("statusUrl is null or empty. Cannot fetch remote sizes.");
                return result;
            }

            string html;
            try
            {
                html = await _httpClient.GetStringAsync(STATUS_URL);
            }
            catch (HttpRequestException httpEx)
            {
                _logService.LogError(httpEx, $"HTTP request failed for '{STATUS_URL}'.");
                return result;
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"An unexpected exception occurred fetching URL '{STATUS_URL}'.");
                return result;
            }

            if (string.IsNullOrEmpty(html))
            {
                _logService.LogError("Received empty response from statusUrl.");
                return result;
            }

            var regex = new Regex(@"href=\""(?<filename>hashes\..*?\.txt)\"".*?\s+(?<size>\d+)\s*$", RegexOptions.Multiline);

            foreach (Match match in regex.Matches(html))
            {
                string filename = match.Groups["filename"].Value;
                string sizeStr = match.Groups["size"].Value;

                if (long.TryParse(sizeStr, out long size))
                {
                    result[filename] = size;
                }
                else
                {
                    _logService.LogError($"Invalid size format '{sizeStr}' for file '{filename}'.");
                }
            }
            if (result.Count == 0)
            {
                _logService.LogWarning("No hash files hashes.game or hashes.lcu found in the remote directory listing.");
            }
            return result;
        }
    }
}