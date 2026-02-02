using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Views.Models.Versions;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using AssetsManager.Services.Manifests;
using AssetsManager.Services.Downloads;
using AssetsManager.Views.Models.Monitor;

namespace AssetsManager.Services.Monitor
{
    public class VersionService
    {
        public event EventHandler<string> VersionDownloadStarted;
        public event EventHandler<(string TaskName, int CurrentValue, int TotalValue, string CurrentFile)> VersionDownloadProgressChanged;
        public event EventHandler<(string TaskName, bool Success, string Message)> VersionDownloadCompleted;

        private readonly LogService _logService;
        private readonly HttpClient _httpClient;
        private readonly DirectoriesCreator _directoriesCreator;
        
        private readonly RmanService _rmanService;
        private readonly ManifestDownloader _manifestDownloader;
        private readonly RmanApiService _riotApiService;

        public VersionService(
            LogService logService, 
            HttpClient httpClient, 
            DirectoriesCreator directoriesCreator,
            RmanService rmanService,
            ManifestDownloader manifestDownloader,
            RmanApiService riotApiService)
        {
            _logService = logService;
            _httpClient = httpClient;
            _directoriesCreator = directoriesCreator;
            
            _rmanService = rmanService;
            _manifestDownloader = manifestDownloader;
            _riotApiService = riotApiService;

            _manifestDownloader.ProgressChanged += (taskName, fileName, current, total) => {
                VersionDownloadProgressChanged?.Invoke(this, (taskName, current, total, Path.GetFileName(fileName)));
            };
        }

        public async Task FetchAllVersionsAsync()
        {
            _logService.Log("Starting get versions from League Client and Game Client...");

            await _directoriesCreator.CreateDirVersionsAsync();

            var riotVersions = await _riotApiService.FetchVersionsAsync();

            if (!riotVersions.Any())
            {
                _logService.LogWarning("No versions found from Riot servers.");
                return;
            }

            foreach (var v in riotVersions.Where(x => x.Product == "Game Client"))
            {
                var path = Path.Combine(_directoriesCreator.VersionsPath, "PBE1", "windows", v.Category);
                Directory.CreateDirectory(path);
                var filePath = Path.Combine(path, $"{v.Version}.txt");
                if (!File.Exists(filePath)) await File.WriteAllTextAsync(filePath, v.ManifestUrl);
            }

            var pluginConfigs = riotVersions.Where(x => x.Product == "League Client").Select(x => x.ManifestUrl).ToList();
            var versionInfo = await DownloadAndExtractVersionAsync(pluginConfigs);

            await SaveClientVersionsAsync(versionInfo);

            _logService.LogSuccess("Version fetch process completed successfully.");
            
            VersionDownloadCompleted?.Invoke(this, ("Fetching Versions", true, "Success"));
        }

        private async Task<List<(string region, string os, string version, string url)>> DownloadAndExtractVersionAsync(List<string> manifestUrls)
        {
            var versions = new List<(string region, string os, string version, string url)>();
            string tempDir = Path.Combine(_directoriesCreator.AppDirectory, "TempVersions");

            foreach (var url in manifestUrls)
            {
                try
                {
                    if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                    Directory.CreateDirectory(tempDir);

                    var manifestBytes = await _httpClient.GetByteArrayAsync(url);
                    var manifest = _rmanService.Parse(manifestBytes);

                    await _manifestDownloader.DownloadManifestAsync(manifest, tempDir, 8, "LeagueClient.exe");

                    string exePath = Path.Combine(tempDir, "LeagueClient.exe");
                    if (File.Exists(exePath))
                    {
                        var version = FileVersionInfo.GetVersionInfo(exePath).FileVersion;
                        if (version != null) versions.Add(("PBE1", "windows", version, url));
                    }
                }
                catch (Exception ex)
                {
                    _logService.LogError(ex, $"Error extracting version from {url}");
                }
                finally
                {
                    if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                }
            }

            return versions;
        }

        private async Task SaveClientVersionsAsync(List<(string region, string os, string version, string url)> versions)
        {
            await _directoriesCreator.CreateDirVersionsAsync();
            foreach (var (region, os, version, url) in versions)
            {
                var path = Path.Combine(_directoriesCreator.VersionsPath, region, os, "league-client");
                Directory.CreateDirectory(path);
                var filePath = Path.Combine(path, $"{version}.txt");
                if (!File.Exists(filePath)) await File.WriteAllTextAsync(filePath, url);
            }
        }

        public async Task DownloadPluginsAsync(string manifestUrl, string lolPbeDirectory, List<string> locales, CancellationToken cancellationToken)
        {
            await ExecuteNativeDownloadTaskAsync("League Client", manifestUrl, lolPbeDirectory, locales);
        }

        public async Task DownloadGameClientAsync(string manifestUrl, string lolPbeDirectory, List<string> locales, CancellationToken cancellationToken)
        {
            string gameDirectory = Path.Combine(lolPbeDirectory, "Game");
            await ExecuteNativeDownloadTaskAsync("Game Client", manifestUrl, gameDirectory, locales);
        }

        private async Task ExecuteNativeDownloadTaskAsync(string taskName, string manifestUrl, string targetDirectory, List<string> locales)
        {
            try
            {
                _logService.Log($"Verifying/Updating {taskName}...");
                VersionDownloadStarted?.Invoke(this, taskName);

                var manifestBytes = await _httpClient.GetByteArrayAsync(manifestUrl);
                var manifest = _rmanService.Parse(manifestBytes);

                int updatedCount = await _manifestDownloader.DownloadManifestAsync(manifest, targetDirectory, 8, null, locales);

                if (updatedCount > 0)
                {
                    _logService.LogSuccess($"{taskName} update finished.");
                }
                else
                {
                    _logService.Log("No updates required for this manifest.");
                }
                VersionDownloadCompleted?.Invoke(this, (taskName, true, "Finished"));
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Error during native {taskName} update");
                VersionDownloadCompleted?.Invoke(this, (taskName, false, ex.Message));
            }
        }

        public async Task<List<VersionFileInfo>> GetVersionFilesAsync()
        {
            var versionFiles = new List<VersionFileInfo>();
            string versionsRootPath = _directoriesCreator.VersionsPath;
            if (!Directory.Exists(versionsRootPath)) return versionFiles;

            try
            {
                foreach (string directory in Directory.EnumerateDirectories(versionsRootPath, "*", SearchOption.AllDirectories))
                {
                    string category = new DirectoryInfo(directory).Name;
                    foreach (string filePath in Directory.EnumerateFiles(directory, "*.txt"))
                    {
                        versionFiles.Add(new VersionFileInfo
                        {
                            FileName = Path.GetFileName(filePath),
                            Content = await File.ReadAllTextAsync(filePath),
                            Category = category,
                            Date = File.GetCreationTime(filePath).ToString("dd/MM/yyyy HH:mm:ss")
                        });
                    }
                }
            }
            catch (Exception ex) { _logService.LogError(ex, "Error loading version files"); }
            return versionFiles;
        }

        public bool DeleteVersionFiles(IEnumerable<VersionFileInfo> versionFiles)
        {
            if (versionFiles == null || !versionFiles.Any()) return false;
            var successCount = 0;
            foreach (var file in versionFiles)
            {
                try
                {
                    var paths = Directory.GetFiles(_directoriesCreator.VersionsPath, file.FileName, SearchOption.AllDirectories);
                    if (paths.Length > 0) { File.Delete(paths[0]); successCount++; }
                }
                catch { }
            }
            return successCount > 0;
        }
    }
}
