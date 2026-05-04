using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Monitor;
using AssetsManager.Views.Models.Settings;

namespace AssetsManager.Services.Monitor
{
    public class BackupManager
    {
        public event EventHandler<int> BackupStarted;
        public event EventHandler<(int Processed, int Total, string CurrentFile)> BackupProgressChanged;
        public event EventHandler<bool> BackupCompleted;

        private readonly DirectoriesCreator _directoriesCreator;
        private readonly LogService _logService;
        private readonly AppSettings _appSettings;
        private readonly VersionService _versionService;
        private readonly HashSet<string> _currentSessionBackups;

        public BackupManager(DirectoriesCreator directoriesCreator, LogService logService, AppSettings appSettings, VersionService versionService)
        {
            _directoriesCreator = directoriesCreator;
            _logService = logService;
            _appSettings = appSettings;
            _versionService = versionService;
            _currentSessionBackups = new HashSet<string>();
        }


        public async Task CreateLolPbeDirectoryBackupAsync(string sourceLolPath, string destinationBackupPath, CancellationToken cancellationToken)
        {
            // Notify UI immediately to show activity (Indeterminate spinner)
            BackupStarted?.Invoke(this, 0);

            await Task.Run(async () =>
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (Directory.Exists(destinationBackupPath))
                    {
                        Directory.Delete(destinationBackupPath, true);
                    }

                    _logService.Log("Starting directory backup...");
                    
                    // Count total files for progress
                    int totalFiles = 0;
                    try 
                    {
                        totalFiles = Directory.GetFiles(sourceLolPath, "*", SearchOption.AllDirectories).Length;
                    }
                    catch (Exception ex)
                    {
                        _logService.LogWarning($"Could not count files for progress: {ex.Message}");
                    }

                    // Update UI with the real total discovered
                    BackupStarted?.Invoke(this, totalFiles);

                    int processedFiles = 0;
                    CopyDirectoryRecursive(sourceLolPath, destinationBackupPath, ref processedFiles, totalFiles, cancellationToken);
                    
                    _currentSessionBackups.Add(destinationBackupPath);
                    BackupCompleted?.Invoke(this, true);
                }
                catch (OperationCanceledException)
                {
                    _logService.LogWarning("Backup operation was cancelled by the user.");
                    BackupCompleted?.Invoke(this, false);
                    // Clean up partially created backup if cancelled
                    if (Directory.Exists(destinationBackupPath))
                    {
                        try { Directory.Delete(destinationBackupPath, true); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    _logService.LogError(ex, $"AssetsManager.Services.Monitor.BackupManager.CreateLolPbeDirectoryBackupAsync Exception for source: {sourceLolPath}, destination: {destinationBackupPath}");
                    BackupCompleted?.Invoke(this, false);
                    throw; 
                }
            }, cancellationToken);
        }

        private void CopyDirectoryRecursive(string sourceDir, string destinationDir, ref int processedFiles, int totalFiles, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dir = new DirectoryInfo(sourceDir);
            if (!dir.Exists)
                throw new DirectoryNotFoundException($"Source directory not found: {dir.FullName}");

            _directoriesCreator.CreateDirectory(destinationDir);

            foreach (FileInfo file in dir.GetFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();
                file.CopyTo(Path.Combine(destinationDir, file.Name), true);
                processedFiles++;
                BackupProgressChanged?.Invoke(this, (processedFiles, totalFiles, file.Name));
            }

            foreach (DirectoryInfo subDir in dir.GetDirectories())
            {
                CopyDirectoryRecursive(subDir.FullName, Path.Combine(destinationDir, subDir.Name), ref processedFiles, totalFiles, cancellationToken);
            }
        }

        public async Task<List<BackupModel>> GetBackupsAsync()
        {
            var backups = new List<BackupModel>();
            var scannedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var basePaths = new List<string>();
            if (!string.IsNullOrEmpty(_appSettings.LolPbeDirectory)) basePaths.Add(_appSettings.LolPbeDirectory);
            if (!string.IsNullOrEmpty(_appSettings.LolLiveDirectory)) basePaths.Add(_appSettings.LolLiveDirectory);

            foreach (var basePath in basePaths)
            {
                var parentDir = Directory.GetParent(basePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))?.FullName;
                if (string.IsNullOrEmpty(parentDir) || !Directory.Exists(parentDir)) continue;

                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(parentDir))
                    {
                        if (scannedPaths.Contains(dir)) continue;
                        
                        string version = await _versionService.GetGameVersionAsync(dir);
                        if (version != null)
                        {
                            var (isPbe, isActive) = GetPathIdentification(dir);

                            // Filter by preference
                            if (_appSettings.PreferredBackupClient == PreferredClient.PBE && !isPbe) continue;
                            if (_appSettings.PreferredBackupClient == PreferredClient.LIVE && isPbe) continue;

                            backups.Add(new BackupModel
                            {
                                Name = Path.GetFileName(dir),
                                DisplayName = GetBackupDisplayName(null, dir),
                                Version = version,
                                Path = dir,
                                IsActiveClient = isActive,
                                CreationDate = Directory.GetCreationTime(dir),
                                Size = GetDirectorySize(dir),
                                SizeDisplay = FormatBytes(GetDirectorySize(dir)),
                                IsSelected = false,
                                IsCurrentSessionBackup = _currentSessionBackups.Contains(dir)
                            });
                            scannedPaths.Add(dir);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logService.LogWarning($"Error scanning directory {parentDir}: {ex.Message}");
                }
            }

            return backups.OrderByDescending(b => b.CreationDate).ToList();
        }

        public (bool IsPbe, bool IsActive) GetPathIdentification(string path)
        {
            if (string.IsNullOrEmpty(path)) return (false, false);

            bool isPbe = path.Contains("(PBE)", StringComparison.OrdinalIgnoreCase) || 
                         (!string.IsNullOrEmpty(_appSettings.LolPbeDirectory) && path.StartsWith(_appSettings.LolPbeDirectory, StringComparison.OrdinalIgnoreCase));

            bool isActive = (!string.IsNullOrEmpty(_appSettings.LolPbeDirectory) && path.Equals(_appSettings.LolPbeDirectory, StringComparison.OrdinalIgnoreCase)) || 
                            (!string.IsNullOrEmpty(_appSettings.LolLiveDirectory) && path.Equals(_appSettings.LolLiveDirectory, StringComparison.OrdinalIgnoreCase));

            return (isPbe, isActive);
        }

        private string GetBackupDisplayName(string folderName, string fullPath)
        {
            var (isPbe, _) = GetPathIdentification(fullPath);
            return isPbe ? "League of Legends PBE" : "League of Legends LIVE";
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
