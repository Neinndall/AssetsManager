using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using Serilog;
using System.Threading.Tasks;
using System.Collections.Generic;
using AssetsManager.Services.Core; // For LogService
using AssetsManager.Utils;

namespace AssetsManager.Services.Downloads
{
    public class AssetDownloader
    {
        private readonly HttpClient _httpClient;
        private readonly LogService _logService;
        private readonly DirectoriesCreator _directoriesCreator;

        public AssetDownloader(HttpClient httpClient, LogService logService, DirectoriesCreator directoriesCreator)
        {
            _httpClient = httpClient;
            _logService = logService;
            _directoriesCreator = directoriesCreator;
        }

        public async Task DownloadAssetToCustomPathAsync(string url, string fullDestinationPath)
        {
            bool completed = false;
            try
            {
                string dir = Path.GetDirectoryName(fullDestinationPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    _directoriesCreator.CreateDirectory(dir);
                }

                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode(); // This will throw on non-2xx status codes

                long? expectedLength = response.Content.Headers.ContentLength;
                await using var fs = new FileStream(fullDestinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await response.Content.CopyToAsync(fs);

                if (expectedLength.HasValue && fs.Length != expectedLength.Value)
                {
                    throw new IOException($"Incomplete download from {url}: received {fs.Length}/{expectedLength.Value} bytes.");
                }

                completed = true;
            }
            catch (Exception ex)
            {
                // Now this single block catches network errors, file errors, and HTTP errors (like 404)
                _logService.LogError(ex, $"Failed to download asset from {url}");
                throw; // Re-throw to be caught by the calling method
            }
            finally
            {
                if (!completed && File.Exists(fullDestinationPath))
                {
                    try { File.Delete(fullDestinationPath); } catch { /* best-effort cleanup of partial file */ }
                }
            }
        }


    }
}
