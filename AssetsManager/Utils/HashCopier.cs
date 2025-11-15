using System;
using System.IO;
using System.Threading.Tasks;
using AssetsManager.Services;
using AssetsManager.Services.Core;

namespace AssetsManager.Utils
{
    public class HashCopier
    {
        private readonly LogService _logService;
        private readonly DirectoriesCreator _directoriesCreator;

        public HashCopier(LogService logService, DirectoriesCreator directoriesCreator)
        {
            _logService = logService;
            _directoriesCreator = directoriesCreator;
        }

        public async Task HandleCopyAsync(bool autoCopyHashes)
        {
            if (autoCopyHashes)
            {
                await CopyNewHashesToOlds();
            }
        }

        private async Task CopyNewHashesToOlds()
        {
            string sourcePath = _directoriesCreator.HashesNewPath;
            string destinationPath = _directoriesCreator.HashesOldPath;
            var filesToCopy = new[] { "hashes.game.txt", "hashes.lcu.txt" };

            try
            {
                Directory.CreateDirectory(destinationPath);

                await Task.Run(() =>
                {
                    foreach (var fileName in filesToCopy)
                    {
                        string sourceFile = Path.Combine(sourcePath, fileName);
                        string destFile = Path.Combine(destinationPath, fileName);

                        if (File.Exists(sourceFile))
                        {
                            File.Copy(sourceFile, destFile, true);
                        }
                        else
                        {
                            _logService.LogWarning($"Source hash file not found, skipping copy: {sourceFile}");
                        }
                    }
                });

                _logService.LogSuccess("Hashes copied successfully.");
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "An error occurred while copying specific hashes.");
            }
        }
    }
}
