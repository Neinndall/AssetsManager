using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        public const string BaseUrl = "https://raw.communitydragon.org/data/hashes/lol/";

        public Requests(HttpClient httpClient, DirectoriesCreator directoriesCreator, LogService logService)
        {
            _httpClient = httpClient;
            _directoriesCreator = directoriesCreator;
            _logService = logService;
        }

        public async Task DownloadHashesAsync(string fileName, string downloadDirectory)
        {
            var url = $"{BaseUrl.TrimEnd('/')}/{fileName}";

            try
            {
                var filePath = Path.Combine(downloadDirectory, fileName);
                var tempPath = filePath + ".tmp";
                var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);

                if (response.IsSuccessStatusCode)
                {
                    await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                    {
                        await response.Content.CopyToAsync(fileStream);
                    }

                    if (File.Exists(filePath)) File.Delete(filePath);
                    File.Move(tempPath, filePath);
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

        public async Task DownloadGameHashesFilesAsync()
        {
            await DownloadSpecificHashesAsync(new List<string> { "hashes.game.txt", "hashes.lcu.txt" });
        }

        public async Task DownloadBinHashesFilesAsync()
        {
            await DownloadSpecificHashesAsync(new List<string>
            {
                "hashes.binentries.txt", "hashes.binfields.txt",
                "hashes.binhashes.txt", "hashes.bintypes.txt"
            });
        }

        public async Task DownloadRstHashesFilesAsync()
        {
            await DownloadSpecificHashesAsync(new List<string> { "hashes.rst.xxh3.txt", "hashes.rst.xxh64.txt" });
        }

        public async Task DownloadSpecificHashesAsync(List<string> filesToDownload)
        {
            var tasks = new List<Task>();
            foreach (var fileName in filesToDownload)
            {
                tasks.Add(DownloadHashesAsync(fileName, _directoriesCreator.HashesPath));
            }
            await Task.WhenAll(tasks);
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
