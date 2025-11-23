using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using Serilog;
using System.Threading.Tasks;
using System.Collections.Generic;
using AssetsManager.Services.Core; // For LogService

namespace AssetsManager.Services.Downloads
{
    public class AssetDownloader
    {
        private readonly HttpClient _httpClient;
        private readonly LogService _logService;
        public AssetDownloader(HttpClient httpClient, LogService logService)
        {
            _httpClient = httpClient;
            _logService = logService;
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


    }
}
