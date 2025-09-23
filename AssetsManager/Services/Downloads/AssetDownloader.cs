using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using Serilog;
using System.Threading.Tasks;
using AssetsManager.Views.Models;
using AssetsManager.Utils;
using AssetsManager.Views.Dialogs;
using AssetsManager.Services.Core;

namespace AssetsManager.Services.Downloads
{
    public class AssetDownloader
    {
        private readonly HttpClient _httpClient;
        private readonly DirectoriesCreator _directoriesCreator;
        private readonly LogService _logService;
        public event Action<int> DownloadStarted;
        public event Action<int, int, string, bool, string> DownloadProgressChanged;
        public event Action DownloadCompleted;

        public AssetDownloader(HttpClient httpClient, DirectoriesCreator directoriesCreator, LogService logService)
        {
            _httpClient = httpClient;
            _directoriesCreator = directoriesCreator;
            _logService = logService;
        }

        public void NotifyDownloadStarted(int totalFiles)
        {
            DownloadStarted?.Invoke(totalFiles);
        }

        public void NotifyDownloadProgressChanged(int completedFiles, int totalFiles, string currentFileName, bool isSuccess, string errorMessage)
        {
            DownloadProgressChanged?.Invoke(completedFiles, totalFiles, currentFileName, isSuccess, errorMessage);
        }

        public void NotifyDownloadCompleted()
        {
            var downloadAssetsPath = _directoriesCreator.SubAssetsDownloadedPath;
            _logService.LogInteractiveSuccess($"Download of new assets completed in {downloadAssetsPath}", downloadAssetsPath);
            DownloadCompleted?.Invoke();
        }

        public async Task<List<string>> DownloadAssets(IEnumerable<(string, string)> assets, string downloadDirectory, List<string> notFoundAssets, int overallTotalFiles, int completedFilesOffset)
        {
            var assetsList = assets.ToList();
            int currentBatchTotalFiles = assetsList.Count;
            int currentBatchCompletedFiles = 0;

            _logService.Log("Starting download of new assets...");
            foreach (var (relativePath, baseUrl) in assetsList)
            {
                string url = baseUrl + relativePath;
                string originalUrl = url;

                url = AssetUrlRules.Adjust(url);

                if (string.IsNullOrEmpty(url))
                {
                    _logService.LogDebug($"Asset skipped due to empty URL after adjustment: {originalUrl}");
                    continue;
                }

                var (success, errorMsg) = await DownloadFileAsync(url, downloadDirectory, originalUrl);
                if (!success)
                {
                    notFoundAssets.Add(originalUrl);
                }

                currentBatchCompletedFiles++;
                NotifyDownloadProgressChanged(completedFilesOffset + currentBatchCompletedFiles, overallTotalFiles, Path.GetFileName(url), success, errorMsg);
            }

            return notFoundAssets;
        }

        public async Task<(bool success, string errorMessage)> DownloadFileAsync(string url, string downloadDirectory, string originalUrl)
        {
            string fileName = Path.GetFileName(url);
            string extensionFolder = _directoriesCreator.CreateAssetDirectoryPath(url, downloadDirectory);
            string filePath = Path.Combine(extensionFolder, fileName);

            try
            {
                var response = await _httpClient.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    await using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                    {
                        await response.Content.CopyToAsync(fileStream);
                    }
                    Log.Information($"Downloaded: {fileName}"); // Log to file only
                    return (true, null);
                }
                else
                {
                    string errorMsg = $"Failed to download '{fileName}'. Reason: {response.StatusCode} - {response.ReasonPhrase}";
                    Log.Error(errorMsg); // Log to file only
                    return (false, errorMsg);
                }
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Error downloading {url}");
                return (false, ex.Message);
            }
        }

        public async Task<string> DownloadAssetTextAsync(string assetUrl)
        {
            try
            {
                var response = await _httpClient.GetAsync(assetUrl);

                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadAsStringAsync();
                }

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    Log.Information($"Asset not found: {assetUrl}");
                }
                else
                {
                   _logService.LogWarning($"HTTP error downloading text content from {assetUrl}. Status: {response.StatusCode}");
                }
                return null;

            }
            catch (Exception ex) // This will now catch network errors, timeouts, etc.
            {
                _logService.LogError(ex, $"Error downloading text content from {assetUrl}");
                return null;
            }
        }

        public async Task<(bool IsAvailable, string ErrorMessage)> CheckAssetAvailabilityAsync(string assetUrl)
        {
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Head, assetUrl))
                {
                    var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                    return (response.IsSuccessStatusCode, response.IsSuccessStatusCode ? null : $"Asset not available. Status: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"An error occurred while checking {assetUrl}");
                return (false, ex.Message);
            }
        }

        public async Task<string> DownloadAssetIfNeeded(string assetUrl, string assetName)
        {
            await _directoriesCreator.CreatePreviewAssetsAsync();
            string previewAssetsPath = _directoriesCreator.PreviewAssetsPath;
            string localFilePath = Path.Combine(previewAssetsPath, assetName);

            if (File.Exists(localFilePath))
            {
                _logService.LogDebug($"Asset already exists locally: {localFilePath}");
                return localFilePath;
            }

            try
            {
                var response = await _httpClient.GetAsync(assetUrl);
                response.EnsureSuccessStatusCode();
                await using (var fileStream = new FileStream(localFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await response.Content.CopyToAsync(fileStream);
                }
                return localFilePath;
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Error downloading asset '{assetName}' from {assetUrl}");
                return null;
            }
        }

        public async Task DownloadAssetToCustomPathAsync(string url, string fullDestinationPath)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fullDestinationPath));
                var response = await _httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode(); // This will throw on non-2xx status codes

                await using (var fs = new FileStream(fullDestinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await response.Content.CopyToAsync(fs);
                }
            }
            catch (Exception ex)
            {
                // Now this single block catches network errors, file errors, and HTTP errors (like 404)
                _logService.LogError(ex, $"Failed to download asset from {url}");
                throw; // Re-throw to be caught by the calling method
            }
        }

        public async Task<int> DownloadWadAssetsAsync(IEnumerable<SerializableChunkDiff> diffs)
        {
            await _directoriesCreator.CreateDirSubAssetsDownloadedAsync();
            string gameBaseUrl = "https://raw.communitydragon.org/pbe/game/";
            string pluginBaseUrl = "https://raw.communitydragon.org/pbe/";
            int successCount = 0;

            foreach (var diff in diffs)
            {
                string baseUrl = diff.SourceWadFile.Contains("plugins", StringComparison.OrdinalIgnoreCase) ? pluginBaseUrl : gameBaseUrl;
                string sourceUrl = baseUrl + diff.Path.Replace("\\", "/");

                string finalUrl = AssetUrlRules.Adjust(sourceUrl);

                if (string.IsNullOrEmpty(finalUrl))
                {
                    Log.Information($"Skipping download for {diff.FileName} as it's filtered by asset rules."); // Log only to the .log file
                    continue;
                }

                string baseSavePath;
                switch (diff.Type)
                {
                    case ChunkDiffType.New:
                        baseSavePath = _directoriesCreator.WadNewAssetsPath;
                        break;
                    case ChunkDiffType.Modified:
                        baseSavePath = _directoriesCreator.WadModifiedAssetsPath;
                        break;
                    case ChunkDiffType.Renamed:
                        baseSavePath = _directoriesCreator.WadRenamedAssetsPath;
                        break;
                    default:
                        // Skip downloading for other types like 'Removed'
                        continue;
                }

                // Refactored to use the centralized DirectoriesCreator for path construction.
                // This correctly handles "game/" vs "plugins/" based on the URL and keeps the logic consistent.
                string destinationDirectory = _directoriesCreator.CreateAssetDirectoryPath(finalUrl, baseSavePath);
                string finalFileName = Path.GetFileName(finalUrl);
                string destinationPath = Path.Combine(destinationDirectory, finalFileName);

                Log.Information($"Downloaded: {finalFileName}"); // Log only to the .log file
                try
                {
                    await DownloadAssetToCustomPathAsync(finalUrl, destinationPath);
                    successCount++;
                }
                catch (Exception ex)
                {
                    _logService.LogError(ex, $"Failed to download {diff.FileName}");
                    // Continue to next file
                }
            }
            return successCount;
        }
    }
}