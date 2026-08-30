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
        public event Func<bool, Task> BackupCompleted;
        public event Action BackupsChanged;

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
                    
                    DirectoryMetrics metrics = MeasureDirectory(sourceLolPath, cancellationToken);
                    BackupStarted?.Invoke(metrics.FileCount);

                    int processedFiles = 0;
                    CopyDirectoryRecursive(sourceLolPath, destinationBackupPath, ref processedFiles, metrics.FileCount, cancellationToken);
                    
                    MarkCurrentSessionBackup(destinationBackupPath);
                    await NotifyBackupCompletedAsync(true);
                }
                catch (OperationCanceledException)
                {
                    // Clean up partially created backup if cancelled
                    if (Directory.Exists(destinationBackupPath))
                    {
                        try { Directory.Delete(destinationBackupPath, true); } 
                        catch (Exception ex) { _logService.LogError(ex, "Could not clean up directory after cancelled operation."); }
                    }
                    await NotifyBackupCompletedAsync(false);
                    throw;
                }
                catch (Exception ex)
                {
                    _logService.LogError(ex, $"Backup failed for source: {sourceLolPath}");
                    await NotifyBackupCompletedAsync(false);
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

                    DirectoryMetrics metrics = MeasureDirectory(sourceBackupPath, cancellationToken);
                    BackupStarted?.Invoke(metrics.FileCount);

                    int processedFiles = 0;
                    CopyDirectoryRecursive(sourceBackupPath, destinationBackupPath, ref processedFiles, metrics.FileCount, cancellationToken);

                    MarkCurrentSessionBackup(destinationBackupPath);
                    await NotifyBackupCompletedAsync(true);
                }
                catch (OperationCanceledException)
                {
                    if (Directory.Exists(destinationBackupPath))
                    {
                        try { Directory.Delete(destinationBackupPath, true); } 
                        catch (Exception ex) { _logService.LogError(ex, "Could not clean up directory after failed/cancelled operation."); }
                    }
                    await NotifyBackupCompletedAsync(false);
                    throw;
                }
                catch (Exception ex)
                {
                    _logService.LogError(ex, $"Error cloning backup: {sourceBackupPath} to {destinationBackupPath}");
                    await NotifyBackupCompletedAsync(false);
                    throw;
                }
            }, cancellationToken);
        }

        private async Task NotifyBackupCompletedAsync(bool success)
        {
            if (success)
            {
                BackupsChanged?.Invoke();
            }

            Delegate[] handlers = BackupCompleted?.GetInvocationList();
            if (handlers is null) return;

            foreach (Func<bool, Task> handler in handlers.Cast<Func<bool, Task>>())
            {
                try
                {
                    await handler(success);
                }
                catch (Exception ex)
                {
                    _logService.LogError(ex, "A backup completion observer failed.");
                }
            }
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

        public async Task<List<BackupModel>> GetBackupsAsync(
            CancellationToken cancellationToken = default,
            bool includeStorageMetrics = true,
            PreferredClient? client = null)
        {
            return await Task.Run(() =>
            {
                var backups = new List<BackupModel>();
                var scannedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string[] configuredPaths = client switch
                {
                    PreferredClient.PBE => new[] { _appSettings.LolPbeDirectory },
                    PreferredClient.LIVE => new[] { _appSettings.LolLiveDirectory },
                    _ => new[] { _appSettings.LolPbeDirectory, _appSettings.LolLiveDirectory }
                };
                var basePaths = configuredPaths
                    .Where(path => !string.IsNullOrWhiteSpace(path));

                var candidateEntries = new List<(string Dir, string Version, bool IsPbe, bool IsMain)>();

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
                            candidateEntries.Add((dir, version, isPbe, isMain));
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logService.LogError(ex, $"Error scanning directory for backups: {parentDir}");
                    }
                }

                var metricsMap = new Dictionary<string, DirectoryMetrics>(StringComparer.OrdinalIgnoreCase);

                if (includeStorageMetrics)
                {
                    foreach (var (dir, _, _, _) in candidateEntries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        metricsMap[dir] = MeasureDirectory(dir, cancellationToken);
                    }
                }

                foreach (var (dir, version, isPbe, isMain) in candidateEntries)
                {
                    DirectoryMetrics metrics = metricsMap.TryGetValue(dir, out var m) ? m : default;
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
                        SizeDisplay = includeStorageMetrics
                            ? FormatUtils.FormatSize(metrics.TotalBytes)
                            : null,
                        IsSelected = false,
                        IsCurrentSessionBackup = IsCurrentSessionBackup(dir)
                    });
                }

                return FilterByClient(backups, client)
                    .OrderByDescending(backup => backup.CreationDate)
                    .ToList();
            }, cancellationToken);
        }

        public static IEnumerable<BackupModel> FilterByClient(
            IEnumerable<BackupModel> backups,
            PreferredClient? client)
        {
            if (backups == null) throw new ArgumentNullException(nameof(backups));

            return client switch
            {
                PreferredClient.PBE => backups.Where(backup => backup.IsPbe),
                PreferredClient.LIVE => backups.Where(backup => !backup.IsPbe),
                _ => backups
            };
        }

        public (bool IsPbe, bool IsMain) GetPathIdentification(string path)
        {
            if (string.IsNullOrEmpty(path)) return (false, false);

            string pbeRoot = _appSettings.LolPbeDirectory;
            string liveRoot = _appSettings.LolLiveDirectory;

            bool isPbeSub = !string.IsNullOrEmpty(pbeRoot) && PathUtils.IsSameOrSubPath(pbeRoot, path);
            bool isLiveSub = !string.IsNullOrEmpty(liveRoot) && PathUtils.IsSameOrSubPath(liveRoot, path);

            bool isPbe = path.Contains("(PBE)", StringComparison.OrdinalIgnoreCase) || isPbeSub;

            bool isMain = isPbeSub || isLiveSub;

            // Fallback for installations outside any configured root (e.g. the LIVE
            // client when only PBE is configured): any valid game installation that
            // is not a "_old_" snapshot is the main client of its environment, so it
            // must be identified as MAIN regardless of the preferred client.
            if (!isMain)
            {
                string folderName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                isMain = !folderName.Contains("_old_", StringComparison.OrdinalIgnoreCase);
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
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                return default;
            }

            int fileCount = 0;
            long totalBytes = 0;

            var enumerationOptions = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };

            var dirInfo = new DirectoryInfo(path);
            foreach (var file in dirInfo.EnumerateFiles("*", enumerationOptions))
            {
                cancellationToken.ThrowIfCancellationRequested();
                fileCount++;
                totalBytes += file.Length;
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
                    BackupsChanged?.Invoke();
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
