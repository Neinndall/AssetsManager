using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using AssetsManager.Services.Downloads;
using AssetsManager.Services;
using AssetsManager.Services.Core;

namespace AssetsManager.Utils
{
    public class Resources
    {
        private readonly HttpClient _httpClient;
        private readonly AssetDownloader _assetDownloader;
        private readonly DirectoriesCreator _directoriesCreator;
        private readonly LogService _logService;

        public Resources(HttpClient httpClient, DirectoriesCreator directoriesCreator, LogService logService, AssetDownloader assetDownloader)
        {
            _httpClient = httpClient;
            _directoriesCreator = directoriesCreator;
            _logService = logService;
            _assetDownloader = assetDownloader;
        }

        public async Task GetResourcesFiles()
        {
            var resourcesPath = _directoriesCreator.ResourcesPath;
            var differencesGameFilePath = Path.Combine(resourcesPath, "differences_game.txt");
            var differencesLcuFilePath = Path.Combine(resourcesPath, "differences_lcu.txt");

            if (!File.Exists(differencesGameFilePath) || !File.Exists(differencesLcuFilePath))
            {
                _logService.LogError("One or more diff files do not exist in the resources path.");
                return;
            }

            try
            {
                var gameDifferences = (await File.ReadAllLinesAsync(differencesGameFilePath))
                    .Select(line => line.Split(' ').Length >= 2 ? line.Split(' ')[1] : line)
                    .Select(asset => (asset, "https://raw.communitydragon.org/pbe/game/"));
                var lcuDifferences = (await File.ReadAllLinesAsync(differencesLcuFilePath))
                    .Select(line => line.Split(' ').Length >= 2 ? line.Split(' ')[1] : line)
                    .Select(asset => (asset, "https://raw.communitydragon.org/pbe/"));
                var allDifferences = gameDifferences.Concat(lcuDifferences).ToList();

                // Aseguramos la creacion de la carpeta de AssetsDownloaded
                _directoriesCreator.GenerateNewSubAssetsDownloadedPath();
                var downloadDirectory = _directoriesCreator.SubAssetsDownloadedPath;

                var notFoundAssets = new List<string>();

                int overallTotalFiles = allDifferences.Count;

                _assetDownloader.NotifyDownloadStarted(overallTotalFiles);

                await _assetDownloader.DownloadAssets(
                    allDifferences,
                    downloadDirectory,
                    notFoundAssets,
                    overallTotalFiles,
                    0);

                await SaveNotFoundAssets(notFoundAssets, resourcesPath);

                _assetDownloader.NotifyDownloadCompleted();
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Error processing difference files in Resources.");
                _assetDownloader.NotifyDownloadCompleted(); // Ensure completion is notified even on error
                throw;
            }
        }

        private async Task SaveNotFoundAssets(List<string> notFoundAssets, string resourcesPath)
        {
            if (string.IsNullOrWhiteSpace(resourcesPath))
                throw new ArgumentException("The resource path is not defined.", nameof(resourcesPath));

            var modifiedNotFoundGameAssets = notFoundAssets
            .Select(url =>
            {
                if (url.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                    return url.Replace(".dds", ".png", StringComparison.OrdinalIgnoreCase);
                if (url.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
                    return url.Replace(".tex", ".png", StringComparison.OrdinalIgnoreCase);
                return url;
            })
            .ToList();

            var allNotFoundAssets = modifiedNotFoundGameAssets.ToList();
            var notFoundFilePath = Path.Combine(resourcesPath, "NotFounds.txt");

            try
            {
                await File.WriteAllLinesAsync(notFoundFilePath, allNotFoundAssets);
                _logService.LogInteractiveInfo($"Saved not found assets to {resourcesPath}", resourcesPath);
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Error saving the NotFound file");
                throw;
            }
        }
    }
}