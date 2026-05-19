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


        public async Task CreateLolPbeDirectoryBackupAsync(string sourceLolPath, string destinationBackupPath, CancellationToken cancellationToken, string logMessage = "Starting backup...")
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

                    _logService.Log(logMessage);
                    
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
                    _logService.LogWarning("Backup process was cancelled.");
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

        public async Task CloneBackupAsync(string sourceBackupPath, string destinationBackupPath, CancellationToken cancellationToken)
        {
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

                    _logService.Log($"Cloning backup: {Path.GetFileName(sourceBackupPath)}...");

                    int totalFiles = 0;
                    try
                    {
                        totalFiles = Directory.GetFiles(sourceBackupPath, "*", SearchOption.AllDirectories).Length;
                    }
                    catch (Exception ex)
                    {
                        _logService.LogWarning($"Could not count files for cloning progress: {ex.Message}");
                    }

                    BackupStarted?.Invoke(this, totalFiles);

                    int processedFiles = 0;
                    CopyDirectoryRecursive(sourceBackupPath, destinationBackupPath, ref processedFiles, totalFiles, cancellationToken);

                    _currentSessionBackups.Add(destinationBackupPath);
                    BackupCompleted?.Invoke(this, true);
                }
                catch (OperationCanceledException)
                {
                    _logService.LogWarning("Backup cloning was cancelled.");
                    BackupCompleted?.Invoke(this, false);
                    if (Directory.Exists(destinationBackupPath))
                    {
                        try { Directory.Delete(destinationBackupPath, true); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    _logService.LogError(ex, $"Error cloning backup: {sourceBackupPath} to {destinationBackupPath}");
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
                            var (isPbe, isMain) = GetPathIdentification(dir);

                            // Filter by preference
                            if (_appSettings.PreferredClient == PreferredClient.PBE && !isPbe) continue;
                            if (_appSettings.PreferredClient == PreferredClient.LIVE && isPbe) continue;

                            backups.Add(new BackupModel
                            {
                                Name = Path.GetFileName(dir),
                                DisplayName = GetBackupDisplayName(null, dir),
                                Version = version,
                                IsPbe = isPbe,
                                Path = dir,
                                IsMainClient = isMain,
                                CreationDate = Directory.GetCreationTime(dir),
                                Size = GetDirectorySize(dir),
                                SizeDisplay = FormatUtils.FormatSize(GetDirectorySize(dir)),
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

        public (bool IsPbe, bool IsMain) GetPathIdentification(string path)
        {
            if (string.IsNullOrEmpty(path)) return (false, false);

            string pbeRoot = _appSettings.LolPbeDirectory;
            string liveRoot = _appSettings.LolLiveDirectory;

            // Prioritize based on user preference
            bool isPbe;
            bool isMain;

            if (_appSettings.PreferredClient == PreferredClient.PBE)
            {
                bool isPbeSub = !string.IsNullOrEmpty(pbeRoot) && PathUtils.IsSameOrSubPath(pbeRoot, path);
                bool isLiveSub = !string.IsNullOrEmpty(liveRoot) && PathUtils.IsSameOrSubPath(liveRoot, path);

                isPbe = path.Contains("(PBE)", StringComparison.OrdinalIgnoreCase) || isPbeSub;
                isMain = isPbeSub || isLiveSub;
            }
            else
            {
                bool isLiveSub = !string.IsNullOrEmpty(liveRoot) && PathUtils.IsSameOrSubPath(liveRoot, path);
                bool isPbeSub = !string.IsNullOrEmpty(pbeRoot) && PathUtils.IsSameOrSubPath(pbeRoot, path);

                isPbe = path.Contains("(PBE)", StringComparison.OrdinalIgnoreCase) || isPbeSub;
                isMain = isLiveSub || isPbeSub;
            }

            return (isPbe, isMain);
        }

        public string GetGameRoot(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            string pbeRoot = _appSettings.LolPbeDirectory;
            string liveRoot = _appSettings.LolLiveDirectory;

            // Prioritize check based on preferred client
            if (_appSettings.PreferredClient == PreferredClient.PBE)
            {
                if (PathUtils.IsSameOrSubPath(pbeRoot, path)) return pbeRoot;
                if (PathUtils.IsSameOrSubPath(liveRoot, path)) return liveRoot;
            }
            else
            {
                if (PathUtils.IsSameOrSubPath(liveRoot, path)) return liveRoot;
                if (PathUtils.IsSameOrSubPath(pbeRoot, path)) return pbeRoot;
            }

            // Fast heuristic climbing (only if not a known main client)
            string current = path;
            for (int i = 0; i < 10; i++) // Safety limit
            {
                if (string.IsNullOrEmpty(current)) break;

                if (File.Exists(Path.Combine(current, "content-metadata.json")) || 
                    File.Exists(Path.Combine(current, "Game", "content-metadata.json")))
                {
                    return current;
                }
                var parent = Directory.GetParent(current);
                if (parent == null) break;
                current = parent.FullName;
            }

            return null;
        }

        private bool IsSameOrSubPath(string root, string sub)
        {
            return PathUtils.IsSameOrSubPath(root, sub);
        }

        private string GetBackupDisplayName(string folderName, string virtualPath)
        {
            var (isPbe, _) = GetPathIdentification(virtualPath);
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

        public bool DeleteBackup(string backupPath, bool showLog = true)
        {
            try
            {
                if (Directory.Exists(backupPath))
                {
                    Directory.Delete(backupPath, true);
                    if (showLog)
                    {
                        _logService.LogSuccess("The selected backup was deleted successfully.");
                    }
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
