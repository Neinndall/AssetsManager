using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
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

        private static readonly string[] AllKnownHashFiles = {
            GAME_HASHES_FILENAME, LCU_HASHES_FILENAME, HASHES_BINENTRIES,
            HASHES_BINFIELDS, HASHES_BINHASHES, HASHES_BINTYPES,
            HASHES_RST_XXH3, HASHES_RST_XXH64
        };

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
            var outdatedFiles = await GetOutdatedHashFilesAsync(silent, onUpdateFound);
            if (outdatedFiles.Any())
            {
                if (!silent) _logService.Log("Server updated or local files out of date. Starting hash synchronization...");

                if (syncHashesWithCDTB)
                {
                    await _requests.DownloadSpecificHashesAsync(outdatedFiles);
                }

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
            foreach (var filename in AllKnownHashFiles)
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

        private List<string> GetLocallyOutOfSyncFiles()
        {
            var outdatedFiles = new List<string>();
            var configSizes = _appSettings.HashesSizes;
            var newHashesPath = _directoriesCreator.HashesNewPath;

            if (configSizes == null || configSizes.Count == 0)
            {
                return outdatedFiles; // Nothing in config to check against.
            }

            if (string.IsNullOrEmpty(newHashesPath) || !Directory.Exists(newHashesPath))
            {
                _logService.LogWarning($"Skipping local file sync check: Hashes/new directory path is invalid or not found ('{newHashesPath}').");
                return outdatedFiles; // Path is not valid, cannot check.
            }

            foreach (var entry in configSizes)
            {
                string filename = entry.Key;
                long configSize = entry.Value;
                string filePath = Path.Combine(newHashesPath, filename);

                bool isOutOfSync = false;
                if (!File.Exists(filePath))
                {
                    if (configSize > 0) isOutOfSync = true;
                }
                else
                {
                    long diskSize = new FileInfo(filePath).Length;
                    if (configSize != diskSize) isOutOfSync = true;
                }

                if (isOutOfSync)
                {
                    outdatedFiles.Add(filename);
                }
            }

            return outdatedFiles;
        }

        public async Task<List<string>> GetOutdatedHashFilesAsync(bool silent = false, Action onUpdateFound = null)
        {
            var localOutOfSync = GetLocallyOutOfSyncFiles();
            if (localOutOfSync.Any())
            {
                onUpdateFound?.Invoke();
                // If files are out of sync locally, we might need to sync everything
                // depending on the strategy, but for now, let's just return the list.
                return localOutOfSync;
            }

            try
            {
                if (!silent) _logService.Log("Getting update sizes from server...");
                var serverSizes = await GetRemoteHashesSizesAsync();

                if (serverSizes == null || serverSizes.Count == 0)
                {
                    _logService.LogWarning("Could not retrieve remote hash sizes or received an empty list. Skipping update check.");
                    return new List<string>();
                }

                var localSizes = _appSettings.HashesSizes ?? new Dictionary<string, long>();
                var filesToUpdate = new List<string>();
                bool notificationSent = false;

                foreach (var filename in AllKnownHashFiles)
                {
                    if (UpdateHashSizeIfDifferent(serverSizes, localSizes, filename))
                    {
                        filesToUpdate.Add(filename);
                        if (!notificationSent)
                        {
                            onUpdateFound?.Invoke();
                            notificationSent = true;
                        }
                    }
                }

                if (filesToUpdate.Any())
                {
                    _appSettings.HashesSizes = localSizes;
                    AppSettings.SaveSettings(_appSettings);
                }

                return filesToUpdate;
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Error checking for updates.");
                return new List<string>();
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