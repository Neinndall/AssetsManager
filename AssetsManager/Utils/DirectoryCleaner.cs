using System;
using System.IO;
using System.Linq;
using AssetsManager.Services;
using AssetsManager.Services.Core;

namespace AssetsManager.Utils
{
    public class DirectoryCleaner
    {
        private readonly DirectoriesCreator _directoriesCreator;
        private readonly LogService _logService;

        public DirectoryCleaner(DirectoriesCreator directoriesCreator, LogService logService)
        {
            _directoriesCreator = directoriesCreator ?? throw new ArgumentNullException(nameof(directoriesCreator));
            _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        }

        public void CleanEmptyDirectories()
        {
            var subAssetsDownloadedPath = _directoriesCreator.SubAssetsDownloadedPath;

            if (!Directory.Exists(subAssetsDownloadedPath))
            {
                _logService.LogWarning($"The folder doesn't exist: {subAssetsDownloadedPath}");
                return;
            }

            string[] rootFoldersToClean =
            {
                Path.Combine(subAssetsDownloadedPath, "plugins"),
                Path.Combine(subAssetsDownloadedPath, "game")
            };

            foreach (var folder in rootFoldersToClean)
            {
                if (Directory.Exists(folder))
                {
                    DeleteEmptyDirectoriesRecursively(folder);
                }
            }
        }

        private void DeleteEmptyDirectoriesRecursively(string directory)
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    return;
                }

                foreach (var subDir in Directory.GetDirectories(directory))
                {
                    DeleteEmptyDirectoriesRecursively(subDir);
                }

                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"The folder could not be processed: {directory}.");
            }
        }
    }
}
