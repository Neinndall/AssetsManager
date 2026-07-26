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
        public event Action<int> BackupStarted;
        public event Action<int, int, string> BackupProgressChanged;
        public event Action<bool> BackupCompleted;

        private readonly DirectoriesCreator _directoriesCreator;
        private readonly LogService _logService;
        private readonly AppSettings _appSettings;
        private readonly VersionService _versionService;
        private readonly HashSet<string> _currentSessionBackups;
        private readonly object _sessionBackupsSync = new();

        public BackupManager(DirectoriesCreator directoriesCreator, LogService logService, AppSettings appSettings, VersionService versionService)
        {
            _directoriesCreator = directoriesCreator;
            _logService = logService;
            _appSettings = appSettings;
            _versionService = versionService;
            _currentSessionBackups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        public readonly record struct BackupStorageEstimate(int FileCount, long TotalBytes, long AvailableBytes);

        public async Task CreateLolPbeDirectoryBackupAsync(
            string sourceLolPath,
            string destinationBackupPath,
            CancellationToken cancellationToken,
            string logMessage = "Starting backup...")
        {
            BackupStarted?.Invoke(0);
            await Task.Run(() =>
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (Directory.Exists(destinationBackupPath))
                    {
                        Directory.Delete(destinationBackupPath, true);
                    }

                    _logService.Log(logMessage);
                    
                    DirectoryMetrics metrics = MeasureDirectory(sourceLolPath, cancellationToken);
                    BackupStarted?.Invoke(metrics.FileCount);

                    int processedFiles = 0;
                    CopyDirectoryRecursive(sourceLolPath, destinationBackupPath, ref processedFiles, metrics.FileCount, cancellationToken);
                    
                    MarkCurrentSessionBackup(destinationBackupPath);
                    BackupCompleted?.Invoke(true);
                }
                catch (OperationCanceledException)
                {
                    BackupCompleted?.Invoke(false);
                    // Clean up partially created backup if cancelled
                    if (Directory.Exists(destinationBackupPath))
                    {
                        try { Directory.Delete(destinationBackupPath, true); } 
                        catch (Exception ex) { _logService.LogError(ex, "Could not clean up directory after cancelled operation."); }
                    }
                    throw;
                }
                catch (Exception ex)
                {
                    _logService.LogError(ex, $"Backup failed for source: {sourceLolPath}");
                    BackupCompleted?.Invoke(false);
                    throw; 
                }
            }, cancellationToken);
        }

        public async Task CloneBackupAsync(
            string sourceBackupPath,
            string destinationBackupPath,
            CancellationToken cancellationToken)
        {
            BackupStarted?.Invoke(0);
            await Task.Run(() =>
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (Directory.Exists(destinationBackupPath))
                    {
                        Directory.Delete(destinationBackupPath, true);
                    }

                    _logService.Log($"Cloning backup: {Path.GetFileName(sourceBackupPath)}...");

                    DirectoryMetrics metrics = MeasureDirectory(sourceBackupPath, cancellationToken);
                    BackupStarted?.Invoke(metrics.FileCount);

                    int processedFiles = 0;
                    CopyDirectoryRecursive(sourceBackupPath, destinationBackupPath, ref processedFiles, metrics.FileCount, cancellationToken);

                    MarkCurrentSessionBackup(destinationBackupPath);
                    BackupCompleted?.Invoke(true);
                }
                catch (OperationCanceledException)
                {
                    BackupCompleted?.Invoke(false);
                    if (Directory.Exists(destinationBackupPath))
                    {
                        try { Directory.Delete(destinationBackupPath, true); } 
                        catch (Exception ex) { _logService.LogError(ex, "Could not clean up directory after failed/cancelled operation."); }
                    }
                    throw;
                }
                catch (Exception ex)
                {
                    _logService.LogError(ex, $"Error cloning backup: {sourceBackupPath} to {destinationBackupPath}");
                    BackupCompleted?.Invoke(false);
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
                BackupProgressChanged?.Invoke(processedFiles, totalFiles, file.Name);
            }

            foreach (DirectoryInfo subDir in dir.GetDirectories())
            {
                CopyDirectoryRecursive(subDir.FullName, Path.Combine(destinationDir, subDir.Name), ref processedFiles, totalFiles, cancellationToken);
            }
        }

        public async Task<List<BackupModel>> GetBackupsAsync(CancellationToken cancellationToken = default)
        {
            return await Task.Run(() =>
            {
                var backups = new List<BackupModel>();
                var scannedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var basePaths = new[] { _appSettings.LolPbeDirectory, _appSettings.LolLiveDirectory }
                    .Where(path => !string.IsNullOrWhiteSpace(path));

                foreach (string basePath in basePaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var parentDir = Directory.GetParent(basePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))?.FullName;
                    if (string.IsNullOrEmpty(parentDir) || !Directory.Exists(parentDir)) continue;
                    try
                    {
                        foreach (string dir in Directory.EnumerateDirectories(parentDir))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (!scannedPaths.Add(dir)) continue;
                            string version = _versionService.GetGameVersionAsync(dir).GetAwaiter().GetResult();
                            if (version == null) continue;
                            var (isPbe, isMain) = GetPathIdentification(dir);
                            DirectoryMetrics metrics = MeasureDirectory(dir, cancellationToken);
                            backups.Add(new BackupModel
                            {
                                Name = Path.GetFileName(dir),
                                DisplayName = GetBackupDisplayName(null, dir),
                                Version = version,
                                IsPbe = isPbe,
                                Path = dir,
                                IsMainClient = isMain,
                                CreationDate = Directory.GetCreationTime(dir),
                                Size = metrics.TotalBytes,
                                SizeDisplay = FormatUtils.FormatSize(metrics.TotalBytes),
                                IsSelected = false,
                                IsCurrentSessionBackup = IsCurrentSessionBackup(dir)
                            });
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logService.LogError(ex, $"Error scanning directory for backups: {parentDir}");
                    }
                }
                return backups.OrderByDescending(backup => backup.CreationDate).ToList();
            }, cancellationToken);
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

                if (File.Exists(Path.Combine(current, "Game", "content-metadata.json")))
                {
                    return current;
                }

                if (File.Exists(Path.Combine(current, "content-metadata.json")))
                {
                    if (Path.GetFileName(current).Equals("Game", StringComparison.OrdinalIgnoreCase))
                    {
                        var parentDir = Directory.GetParent(current);
                        if (parentDir != null) return parentDir.FullName;
                    }
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

        public Task<BackupStorageEstimate> GetStorageEstimateAsync(
            string sourcePath,
            string destinationPath,
            CancellationToken cancellationToken = default) =>
            Task.Run(() =>
            {
                DirectoryMetrics metrics = MeasureDirectory(sourcePath, cancellationToken);
                string root = Path.GetPathRoot(Path.GetFullPath(destinationPath));
                long available = string.IsNullOrEmpty(root) ? 0 : new DriveInfo(root).AvailableFreeSpace;
                return new BackupStorageEstimate(metrics.FileCount, metrics.TotalBytes, available);
            }, cancellationToken);

        public bool CanDeleteBackup(string backupPath) =>
            !string.IsNullOrWhiteSpace(backupPath) &&
            !GetPathIdentification(backupPath).IsMain;

        private static DirectoryMetrics MeasureDirectory(string path, CancellationToken cancellationToken)
        {
            int fileCount = 0;
            long totalBytes = 0;
            foreach (string filePath in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = new FileInfo(filePath);
                fileCount++;
                totalBytes += info.Length;
            }
            return new DirectoryMetrics(fileCount, totalBytes);
        }

        private void MarkCurrentSessionBackup(string path)
        {
            lock (_sessionBackupsSync) _currentSessionBackups.Add(path);
        }

        private bool IsCurrentSessionBackup(string path)
        {
            lock (_sessionBackupsSync) return _currentSessionBackups.Contains(path);
        }

        public bool DeleteBackup(string backupPath, bool showLog = true)
        {
            try
            {
                if (!CanDeleteBackup(backupPath))
                {
                    _logService.LogWarning($"Refused to delete configured MAIN installation: {backupPath}");
                    return false;
                }
                if (Directory.Exists(backupPath))
                {
                    Directory.Delete(backupPath, true);
                    if (showLog)
                    {
                        _logService.LogSuccess("The selected backup was deleted successfully.");
                    }
                    lock (_sessionBackupsSync) _currentSessionBackups.Remove(backupPath);
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

        private readonly record struct DirectoryMetrics(int FileCount, long TotalBytes);
    }
}
