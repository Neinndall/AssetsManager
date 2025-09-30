using System;
using System.IO;
using System.Threading.Tasks;
using AssetsManager.Services;
using AssetsManager.Services.Core;

namespace AssetsManager.Utils
{
    public class BackupManager
    {
        private readonly DirectoriesCreator _directoriesCreator;
        private readonly LogService _logService;

        public BackupManager(DirectoriesCreator directoriesCreator, LogService logService)
        {
            _directoriesCreator = directoriesCreator;
            _logService = logService;
        }

        public async Task<string> HandleBackUpAsync(bool createBackUp)
        {
            if (createBackUp)
            {
                return await CopyFilesToBackUp();
            }
            else
            {
                return string.Empty;
            }
        }

        public async Task<string> CopyFilesToBackUp()
        {
            try
            {
                // Llamamos para la creacion de la carpeta de BackUp si es necesario
                _directoriesCreator.GenerateNewBackUpOldHashesPath();
                
                string backupDirectory = _directoriesCreator.BackUpOldHashesPath;                
                var filesToCopy = new[] { "hashes.game.txt", "hashes.lcu.txt" };

                foreach (var fileName in filesToCopy)
                {
                    string sourceFilePath = Path.Combine("hashes", "olds", fileName);
                    string destinationFilePath = Path.Combine(backupDirectory, fileName);

                    if (File.Exists(sourceFilePath))
                    {
                        await Task.Run(() => File.Copy(sourceFilePath, destinationFilePath, true));
                    }
                }
                return backupDirectory;
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Error occurred while creating hash backup");
                return string.Empty;
            }
        }

        public async Task CreateLolDirectoryBackupAsync(string sourceLolPath, string destinationBackupPath)
        {
            await Task.Run(() =>
            {
                try
                {
                    if (Directory.Exists(destinationBackupPath))
                    {
                        Directory.Delete(destinationBackupPath, true);
                    }

                    _logService.Log("Starting directory backup...");
                    CopyDirectoryRecursive(sourceLolPath, destinationBackupPath);
                }
                catch (Exception ex)
                {
                    _logService.LogError(ex, $"AssetsManager.Utils.BackupManager.CreateLolDirectoryBackupAsync Exception for source: {sourceLolPath}, destination: {destinationBackupPath}");
                    throw; // Re-throw the exception after logging
                }
            });
        }

        private void CopyDirectoryRecursive(string sourceDir, string destinationDir)
        {
            var dir = new DirectoryInfo(sourceDir);
            if (!dir.Exists)
                throw new DirectoryNotFoundException($"Source directory not found: {dir.FullName}");

            Directory.CreateDirectory(destinationDir);

            foreach (FileInfo file in dir.GetFiles())
            {
                file.CopyTo(Path.Combine(destinationDir, file.Name), true);
            }

            foreach (DirectoryInfo subDir in dir.GetDirectories())
            {
                CopyDirectoryRecursive(subDir.FullName, Path.Combine(destinationDir, subDir.Name));
            }
        }
    }
}
