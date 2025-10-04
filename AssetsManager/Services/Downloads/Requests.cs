using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using AssetsManager.Utils;
using AssetsManager.Services.Core;

namespace AssetsManager.Services.Downloads
{
    public class Requests
    {
        private readonly HttpClient _httpClient;
        private readonly DirectoriesCreator _directoriesCreator;
        private readonly LogService _logService;

        private const string BaseUrl = "https://raw.communitydragon.org/data/hashes/lol/";

        public Requests(HttpClient httpClient, DirectoriesCreator directoriesCreator, LogService logService)
        {
            _httpClient = httpClient;
            _directoriesCreator = directoriesCreator;
            _logService = logService;
        }

        public async Task DownloadHashesAsync(string fileName, string downloadDirectory)
        {
            var url = $"{BaseUrl}/{fileName}";

            try
            {
                var filePath = Path.Combine(downloadDirectory, fileName);
                var response = await _httpClient.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    await using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
                    await response.Content.CopyToAsync(fileStream);
                }
                else
                {
                    _logService.LogError($"Error downloading {fileName}. Status code: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Exception downloading {fileName}.");
            }
        }

        public async Task DownloadGameHashesFilesAsync(string downloadDirectory)
        {
            await DownloadHashesAsync("hashes.game.txt", downloadDirectory);
            await DownloadHashesAsync("hashes.lcu.txt", downloadDirectory);
        }

        public async Task DownloadBinHashesFilesAsync(string downloadDirectory)
        {
            await DownloadHashesAsync("hashes.binentries.txt", downloadDirectory);
            await DownloadHashesAsync("hashes.binfields.txt", downloadDirectory);
            await DownloadHashesAsync("hashes.binhashes.txt", downloadDirectory);
            await DownloadHashesAsync("hashes.bintypes.txt", downloadDirectory);
        }

        public async Task DownloadRstHashesFilesAsync(string downloadDirectory)
        {
            await DownloadHashesAsync("hashes.rst.xxh3.txt", downloadDirectory);
            await DownloadHashesAsync("hashes.rst.xxh64.txt", downloadDirectory);
        }

        public async Task SyncHashesIfEnabledAsync(bool syncHashesWithCDTB)
        {
            if (syncHashesWithCDTB)
            {
                await DownloadGameHashesFilesAsync(_directoriesCreator.HashesNewPath);
                await DownloadBinHashesFilesAsync(_directoriesCreator.HashesNewPath);
                await DownloadRstHashesFilesAsync(_directoriesCreator.HashesNewPath);
            }
        }

        public async Task<string> DownloadJsonContentAsync(string url)
        {
            try
            {
                var response = await _httpClient.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    string content = await response.Content.ReadAsStringAsync();
                    _logService.LogDebug($"Successfully downloaded JSON content from {url}.");
                    return content;
                }

                _logService.LogError(
                    $"Failed to download JSON from {url}. Status: {response.StatusCode} - {response.ReasonPhrase}");
                return null;
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Error downloading JSON from {url}.");
                return null;
            }
        }

        public async Task<byte[]> DownloadFileAsBytesAsync(string url)
        {
            try
            {
                var response = await _httpClient.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    byte[] content = await response.Content.ReadAsByteArrayAsync();
                    _logService.LogDebug($"Successfully downloaded file as bytes from {url}.");
                    return content;
                }

                _logService.LogError(
                    $"Failed to download file from {url}. Status: {response.StatusCode} - {response.ReasonPhrase}");
                return null;
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Error downloading file from {url}.");
                return null;
            }
        }
    }
}
