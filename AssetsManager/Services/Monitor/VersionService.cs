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
using AssetsManager.Views.Models.Versions; // Add this
using AssetsManager.Services.Core;
using AssetsManager.Services.Parsers;
using AssetsManager.Utils;

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
        private readonly RmanParser _rmanParser;
        private const string BaseUrl = "https://sieve.services.riotcdn.net/api/v1/products/lol/version-sets";
        private const string ClientReleasesUrl = "https://clientconfig.rpg.riotgames.com/api/v1/config/public?namespace=keystone.products.league_of_legends.patchlines";
        private const string TargetFilename = "LeagueClient.exe";
        private static readonly string[] VersionSets = { "PBE1" };
        private static readonly Dictionary<string, string> RegionMap = new Dictionary<string, string> { { "PBE", "PBE1" } };

        public VersionService(LogService logService, HttpClient httpClient, DirectoriesCreator directoriesCreator, RmanParser rmanParser)
        {
            _logService = logService;
            _httpClient = httpClient;
            _directoriesCreator = directoriesCreator;
            _rmanParser = rmanParser;
        }

        public async Task FetchAllVersionsAsync()
        {
            try
            {
                _logService.Log("Starting version fetch process...");

                // Aseguramos la creacion de la carpeta necesaria
                await _directoriesCreator.CreateDirVersionsAsync();

                // Step 1: Fetch release versions
                foreach (var region in VersionSets)
                {
                    await FetchReleaseVersionsAsync(region);
                }

                // Step 2: Fetch configurations from Riot
                var configurations = await FetchConfigurationsAsync();

                if (configurations.Any())
                {
                    // Step 3: Download executable and get its version
                    var versionInfo = await DownloadAndExtractVersionAsync(configurations);

                    if (versionInfo.Any())
                    {
                        // Step 4: Save the versions and their URLs
                        await SaveClientVersionsAsync(versionInfo);
                    }
                }

                _logService.LogSuccess("Version fetch process completed.");
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "An unexpected error occurred during the version fetch process.");
            }
        }

        private async Task FetchReleaseVersionsAsync(string region, string osPlatform = "windows")
        {
            try
            {
                var url = $"{BaseUrl}/{region}?q[platform]={osPlatform}";
                var response = await _httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync();

                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    JsonElement root = doc.RootElement;
                    if (root.TryGetProperty("releases", out JsonElement releasesElement))
                    {
                        foreach (JsonElement release in releasesElement.EnumerateArray())
                        {
                            var artifactTypeId = release.GetProperty("release").GetProperty("labels").GetProperty("riot:artifact_type_id").GetProperty("values")[0].GetString();
                            var artifactVersion = release.GetProperty("release").GetProperty("labels").GetProperty("riot:artifact_version_id").GetProperty("values")[0].GetString().Split('+')[0];
                            var downloadUrl = release.GetProperty("download").GetProperty("url").GetString();

                            var path = Path.Combine(_directoriesCreator.VersionsPath, region, osPlatform, artifactTypeId);
                            Directory.CreateDirectory(path);
                            var filePath = Path.Combine(path, $"{artifactVersion}.txt");

                            if (!File.Exists(filePath))
                            {
                                await File.WriteAllTextAsync(filePath, downloadUrl);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Error fetching releases for region {region}");
            }
        }

        private async Task<List<(string, string)>> FetchConfigurationsAsync()
        {
            var configs = new List<(string, string)>();
            try
            {
                var response = await _httpClient.GetAsync(ClientReleasesUrl);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync();

                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    JsonElement root = doc.RootElement;
                    var patchlineData = root.GetProperty("keystone.products.league_of_legends.patchlines.pbe").GetProperty("platforms").GetProperty("win").GetProperty("configurations");

                    foreach (JsonElement conf in patchlineData.EnumerateArray())
                    {
                        if (RegionMap.TryGetValue(conf.GetProperty("id").GetString(), out var region))
                        {
                            configs.Add((region, conf.GetProperty("patch_url").GetString()));
                        }
                    }
                }
                return configs;
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Error fetching client configurations");
                return configs;
            }
        }

        private async Task<List<(string region, string os, string version, string url)>> DownloadAndExtractVersionAsync(List<(string, string)> configs)
        {
            var versions = new List<(string, string, string, string)>();
            var urlsSeen = new HashSet<string>();
            string tempDir = Path.Combine(_directoriesCreator.AppDirectory, "TempVersions");

            foreach (var (region, url) in configs)
            {
                if (urlsSeen.Contains(url)) continue;
                urlsSeen.Add(url);

                try
                {
                    if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                    Directory.CreateDirectory(tempDir);

                    var manifest = await _rmanParser.LoadManifestAsync(url);
                    
                    var filesToDownload = await Task.Run(() => _rmanParser.GetFilesToUpdate(manifest, tempDir, filter: $"^{TargetFilename}$"));
                    await _rmanParser.DownloadAssetsAsync(filesToDownload, tempDir, maxThreads: 2, ct: default);

                    string exePath = Path.Combine(tempDir, TargetFilename);
                    string version = GetExeVersion(exePath);

                    if (version != null)
                    {
                        versions.Add((region, "windows", version, url));
                    }
                    else
                    {
                        _logService.LogWarning($"LeagueClient.exe found but version could not be read from {url}");
                    }
                }
                catch (Exception ex)
                {
                    _logService.LogError(ex, $"Error downloading and extracting version from {url}");
                }
                finally
                {
                    if (Directory.Exists(tempDir))
                    {
                        try { Directory.Delete(tempDir, true); } catch { /* Ignore cleanup errors */ }
                    }
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

                if (!File.Exists(filePath))
                {
                    await File.WriteAllTextAsync(filePath, url);
                }
            }
        }

        private string GetExeVersion(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    var versionInfo = FileVersionInfo.GetVersionInfo(filePath);
                    return versionInfo.FileVersion;
                }
                return null;
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Error extracting version from {filePath}");
                return null;
            }
        }

        public async Task DownloadPluginsAsync(string manifestUrl, string lolPbeDirectory, List<string> locales, CancellationToken cancellationToken)
        {
            await ExecuteDownloadTaskAsync("Updating League Client", manifestUrl, lolPbeDirectory, locales, cancellationToken);
        }

        public async Task DownloadGameClientAsync(string manifestUrl, string lolPbeDirectory, List<string> locales, CancellationToken cancellationToken)
        {
            string gameDirectory = Path.Combine(lolPbeDirectory, "Game");
            await ExecuteDownloadTaskAsync("Updating Game Client", manifestUrl, gameDirectory, locales, cancellationToken);
        }

        private async Task ExecuteDownloadTaskAsync(string taskName, string manifestUrl, string targetDirectory, List<string> locales, CancellationToken cancellationToken)
        {
            bool success = false;
            string message = $"{taskName} failed.";

            try
            {
                _logService.Log($"Starting verification/update for {taskName.ToLower()}...");

                if (string.IsNullOrEmpty(manifestUrl) || string.IsNullOrEmpty(targetDirectory) || locales == null || !locales.Any())
                {
                    _logService.LogWarning($"{taskName} called with invalid parameters.");
                    VersionDownloadCompleted?.Invoke(this, (taskName, false, "Invalid parameters."));
                    return;
                }

                VersionDownloadStarted?.Invoke(this, taskName);
                VersionDownloadProgressChanged?.Invoke(this, (taskName, 0, 0, "Verifying Files..."));

                var manifest = await _rmanParser.LoadManifestAsync(manifestUrl);
                
                // DEBUG: Guardar contenido del manifiesto para inspección
                try {
                    var debugLines = manifest.Files.Select(f => $"[{string.Join(",", f.Languages)}] {f.Name} ({f.Size} bytes)");
                    await File.WriteAllLinesAsync(Path.Combine(_directoriesCreator.VersionsPath, "manifest_debug.txt"), debugLines);
                    _logService.LogDebug($"Manifest debug info saved to manifest_debug.txt");
                } catch { }

                cancellationToken.ThrowIfCancellationRequested();

                // 1. Fase de Verificación (Ahora en segundo plano para evitar tirones)
                var filesToUpdate = await Task.Run(() => _rmanParser.GetFilesToUpdate(
                    manifest, 
                    targetDirectory, 
                    locales: locales, 
                    includeNeutral: true,
                    filter: taskName.Contains("League Client") ? $"^{TargetFilename}$" : null
                ));

                if (!filesToUpdate.Any())
                {
                    _logService.LogSuccess("Everything is already up to date.");
                    success = true;
                    message = "Up to date.";
                    return;
                }

                // 2. Fase de Descarga (Determinada - Empieza de 0%)
                bool updatedAny = await _rmanParser.DownloadAssetsAsync(
                    filesToUpdate,
                    targetDirectory,
                    maxThreads: 2,
                    ct: cancellationToken,
                    progressCallback: (file, current, total) => 
                    {
                        VersionDownloadProgressChanged?.Invoke(this, (taskName, current, total, Path.GetFileName(file)));
                    }
                );

                if (updatedAny)
                {
                    _logService.LogSuccess($"{taskName} process finished.");
                    success = true;
                    message = $"{taskName} finished.";
                }
            }
            catch (OperationCanceledException)
            {
                _logService.LogWarning($"{taskName} was cancelled by the user.");
                message = $"{taskName} cancelled.";
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"An error occurred during {taskName.ToLower()}.");
            }
            finally
            {
                VersionDownloadCompleted?.Invoke(this, (taskName, success, message));
            }
        }

        public async Task<List<VersionFileInfo>> GetVersionFilesAsync()
        {
            var versionFiles = new List<VersionFileInfo>();
            string versionsRootPath = _directoriesCreator.VersionsPath;

            if (!Directory.Exists(versionsRootPath))
            {
                _logService.LogWarning($"Versions directory not found: {versionsRootPath}");
                return versionFiles;
            }

            try
            {
                foreach (string directory in Directory.EnumerateDirectories(versionsRootPath, "*", SearchOption.AllDirectories))
                {
                    string category = new DirectoryInfo(directory).Name; // Get the last part of the directory name as category

                    foreach (string filePath in Directory.EnumerateFiles(directory, "*.txt"))
                    {
                        string fileName = Path.GetFileName(filePath);
                        string content = await File.ReadAllTextAsync(filePath);
                        DateTime creationTime = File.GetCreationTime(filePath);
                        string date = creationTime.ToString("dd/MM/yyyy HH:mm:ss");

                        versionFiles.Add(new VersionFileInfo
                        {
                            FileName = fileName,
                            Content = content,
                            Category = category,
                            Date = date
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Error loading version files from {versionsRootPath}");
            }

            return versionFiles;
        }

        public bool DeleteVersionFiles(IEnumerable<VersionFileInfo> versionFiles)
        {
            if (versionFiles == null || !versionFiles.Any())
            {
                _logService.LogWarning("DeleteVersionFiles called with no files.");
                return false;
            }

            var successCount = 0;
            foreach (var versionFile in versionFiles)
            {
                if (DeleteVersionFile(versionFile))
                {
                    successCount++;
                }
            }

            if (successCount > 0)
            {
                _logService.LogSuccess($"Successfully deleted {successCount} version file(s).");
            }

            if (successCount < versionFiles.Count())
            {
                _logService.LogWarning($"Failed to delete {versionFiles.Count() - successCount} version file(s).");
                return false;
            }

            return true;
        }

        private bool DeleteVersionFile(VersionFileInfo versionFile)
        {
            if (versionFile == null)
            {
                _logService.LogWarning("DeleteVersionFile called with null versionFile.");
                return false;
            }

            try
            {
                var files = Directory.GetFiles(_directoriesCreator.VersionsPath, versionFile.FileName, SearchOption.AllDirectories);
                if (files.Length > 0)
                {
                    var filePath = files[0];
                    File.Delete(filePath);
                    return true;
                }
                else
                {
                    _logService.LogWarning($"Version file not found: {versionFile.FileName}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Error deleting version file: {versionFile.FileName}");
                return false;
            }
        }
    }
}