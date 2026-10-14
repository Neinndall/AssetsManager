using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;
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

        public event Action HashSyncStarted;
        public event Action<bool> HashSyncCompleted;
        public bool IsSyncing { get; private set; } = false;

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
            if (!syncHashesWithCDTB) return false;
            await HashResolverService._hashFileAccessLock.WaitAsync();
            bool succeeded = false;
            try
            {
                var outdatedFiles = await GetOutdatedHashFilesAsync(silent, onUpdateFound);
                if (outdatedFiles.Count == 0)
                {
                    if (!silent) _logService.Log("No hash files selected for synchronization.");
                    return false;
                }
                IsSyncing = true;
                HashSyncStarted?.Invoke();
                if (!silent) _logService.Log("Starting hash synchronization...");
                _directoriesCreator.CreateHashesDirectories();
                succeeded = await _requests.DownloadSpecificHashesAsync(outdatedFiles);
                if (succeeded)
                {
                    if (!silent) _logService.LogSuccess("Synchronization completed.");
                }
                else _logService.LogWarning("Hash synchronization was incomplete; failed downloads will be retried on the next check.");
                return succeeded;
            }
            finally
            {
                try { RefreshLocalHashSizes(); }
                finally
                {
                    HashResolverService._hashFileAccessLock.Release();
                    if (IsSyncing)
                    {
                        IsSyncing = false;
                        HashSyncCompleted?.Invoke(succeeded);
                    }
                }
            }
        }

        private Dictionary<string, long> RefreshLocalHashSizes()
        {
            var hashesPath = _directoriesCreator.HashesPath;
            var newSizes = new Dictionary<string, long>();
            foreach (var filename in AllKnownHashFiles)
            {
                var filePath = Path.Combine(hashesPath, filename);
                newSizes[filename] = File.Exists(filePath) ? new FileInfo(filePath).Length : 0;
            }
            var recordedSizes = _appSettings.HashesSizes;
            if (recordedSizes.Count != newSizes.Count || newSizes.Any(pair =>
                    !recordedSizes.TryGetValue(pair.Key, out long size) || size != pair.Value))
            {
                _appSettings.HashesSizes = newSizes;
                AppSettings.SaveSettings(_appSettings);
            }
            return newSizes;
        }

        public async Task<List<string>> GetOutdatedHashFilesAsync(bool silent = false, Action onUpdateFound = null)
        {
            try
            {
                var localSizes = RefreshLocalHashSizes();
                if (!silent) _logService.Log("Getting update sizes from server...");
                var serverSizes = await GetRemoteHashesSizesAsync();

                if (serverSizes == null || serverSizes.Count == 0)
                {
                    _logService.LogWarning("Could not retrieve remote hash sizes or received an empty list. Skipping update check.");
                    return new List<string>();
                }

                var filesToUpdate = AllKnownHashFiles.Where(filename =>
                    serverSizes.GetValueOrDefault(filename, 0) > localSizes[filename]).ToList();
                if (filesToUpdate.Count > 0) onUpdateFound?.Invoke();
                return filesToUpdate;
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Error checking for updates.");
                return new List<string>();
            }
        }

        public async Task<Dictionary<string, long>> GetRemoteHashesSizesAsync()
        {
            var result = new Dictionary<string, long>();

            if (_httpClient == null)
            {
                _logService.LogError("HttpClient is null. Cannot fetch remote sizes.");
                return result;
            }

            string html;
            try
            {
                html = await _httpClient.GetStringAsync(Requests.BaseUrl);
            }
            catch (HttpRequestException)
            {
                _logService.LogWarning($"Unable to connect to CommunityDragon. Server might be down or busy.");
                return result;
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"An unexpected exception occurred fetching URL '{Requests.BaseUrl}'.");
                return result;
            }

            if (string.IsNullOrEmpty(html))
            {
                _logService.LogError("Received empty response from statusUrl.");
                return result;
            }

            // Regex optimized to capture filename and size from HTML directory listing
            var regex = new Regex(@"href=\""(?<filename>hashes\..*?\.txt)\"".*?\s+(?<size>\d+)\s*$", RegexOptions.Multiline);

            foreach (Match match in regex.Matches(html))
            {
                string filename = match.Groups["filename"].Value;
                string sizeStr = match.Groups["size"].Value;

                if (long.TryParse(sizeStr, out long size))
                {
                    result[filename] = size;
                }
            }

            if (result.Count == 0)
            {
                _logService.LogWarning("No hash files metadata could be retrieved from the HTML listing.");
            }
            return result;
        }
    }
}
