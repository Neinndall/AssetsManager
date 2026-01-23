using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AssetsManager.Services;
using AssetsManager.Services.Core;
using AssetsManager.Views.Models.Monitor;

namespace AssetsManager.Utils
{
    public class BackupManager
    {
        private readonly DirectoriesCreator _directoriesCreator;
        private readonly LogService _logService;
        private readonly AppSettings _appSettings;
        private readonly HashSet<string> _currentSessionBackups;

        public BackupManager(DirectoriesCreator directoriesCreator, LogService logService, AppSettings appSettings)
        {
            _directoriesCreator = directoriesCreator;
            _logService = logService;
            _appSettings = appSettings;
            _currentSessionBackups = new HashSet<string>();
        }


        public async Task CreateLolPbeDirectoryBackupAsync(string sourceLolPath, string destinationBackupPath)
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
                    _currentSessionBackups.Add(destinationBackupPath);
                }
                catch (Exception ex)
                {
                    _logService.LogError(ex, $"AssetsManager.Utils.BackupManager.CreateLolPbeDirectoryBackupAsync Exception for source: {sourceLolPath}, destination: {destinationBackupPath}");
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
        
        public async Task<List<BackupModel>> GetBackupsAsync()
        {
            return await Task.Run(() =>
            {
                var backups = new List<BackupModel>();
                var lolPbeDirectory = _appSettings.LolPbeDirectory;
                
                if (!string.IsNullOrEmpty(lolPbeDirectory))
                {
                    var specificBackupPath = lolPbeDirectory + "_old";
                    if (Directory.Exists(specificBackupPath))
                    {
                        var directoryInfo = new DirectoryInfo(specificBackupPath);
                        backups.Add(new BackupModel
                        {
                            Name = directoryInfo.Name,
                            DisplayName = GetBackupDisplayName(directoryInfo.Name),
                            Path = directoryInfo.FullName,
                            CreationDate = directoryInfo.CreationTime,
                            Size = FormatBytes(GetDirectorySize(directoryInfo.FullName)),
                            IsSelected = false,
                            IsCurrentSessionBackup = _currentSessionBackups.Contains(directoryInfo.FullName)
                        });
                    }
                }

                if (string.IsNullOrEmpty(lolPbeDirectory))
                {
                    return backups;
                }
                
                var parentDirectory = Directory.GetParent(lolPbeDirectory)?.FullName;

                if (string.IsNullOrEmpty(parentDirectory) || !Directory.Exists(parentDirectory))
                {
                    return backups;
                }

                foreach (var dir in Directory.EnumerateDirectories(parentDirectory))
                {
                    if (dir.EndsWith("_old", StringComparison.OrdinalIgnoreCase))
                    {
                        if (backups.Any(b => b.Path.Equals(dir, StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }
                        
                        var directoryInfo = new DirectoryInfo(dir);
                        backups.Add(new BackupModel
                        {
                            Name = directoryInfo.Name,
                            DisplayName = GetBackupDisplayName(directoryInfo.Name),
                            Path = directoryInfo.FullName,
                            CreationDate = directoryInfo.CreationTime,
                            Size = FormatBytes(GetDirectorySize(directoryInfo.FullName)),
                            IsSelected = false,
                            IsCurrentSessionBackup = _currentSessionBackups.Contains(directoryInfo.FullName)
                        });
                    }
                }
                return backups.OrderByDescending(b => b.CreationDate).ToList();
            });
        }

        private string GetBackupDisplayName(string folderName)
        {
            string cleanName = folderName.Replace("_old", "", StringComparison.OrdinalIgnoreCase);
            if (cleanName.Contains("PBE", StringComparison.OrdinalIgnoreCase))
            {
                return "League of Legends PBE";
            }
            return "League of Legends LIVE";
        }
        
        private long GetDirectorySize(string path)
        {
            long size = 0;
            var dirInfo = new DirectoryInfo(path);

            try
            {
                foreach (FileInfo fi in dirInfo.GetFiles("*", SearchOption.AllDirectories))
                {
                    size += fi.Length;
                }
            }
            catch (Exception ex)
            {
                _logService.LogWarning($"Unable to calculate size for {path}: {ex.Message}");
            }
            
            return size;
        }

        private string FormatBytes(long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int counter = 0;
            decimal number = (decimal)bytes;
            while (Math.Round(number / 1024) >= 1)
            {
                number = number / 1024;
                counter++;
            }
            return string.Format("{0:n1} {1}", number, suffixes[counter]);
        }
        
        public bool DeleteBackup(string backupPath)
        {
            try
            {
                if (Directory.Exists(backupPath))
                {
                    Directory.Delete(backupPath, true);
                    _logService.LogSuccess("The selected backup was deleted successfully.");
                    _currentSessionBackups.Remove(backupPath);
                    return true;
                }
                _logService.LogWarning($"Attempted to delete non-existent backup: {backupPath}");
                return false;
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Error deleting backup: {backupPath}");
                return false;
            }
        }
    }
}
