using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AssetsManager.Views.Models; // Add this
using AssetsManager.Services.Core;
using AssetsManager.Utils;

namespace AssetsManager.Services.Versions
{
    public class VersionService
    {
        private readonly LogService _logService;
        private readonly HttpClient _httpClient;
        private readonly DirectoriesCreator _directoriesCreator;
        private const string BaseUrl = "https://sieve.services.riotcdn.net/api/v1/products/lol/version-sets";
        private const string ClientReleasesUrl = "https://clientconfig.rpg.riotgames.com/api/v1/config/public?namespace=keystone.products.league_of_legends.patchlines";
        private const string TargetFilename = "LeagueClient.exe";
        private static readonly string[] VersionSets = { "PBE1" };
        private static readonly Dictionary<string, string> RegionMap = new Dictionary<string, string> { { "PBE", "PBE1" } };

        public VersionService(LogService logService, DirectoriesCreator directoriesCreator)
        {
            _logService = logService;
            _httpClient = new HttpClient();
            _directoriesCreator = directoriesCreator;
        }

        public async Task FetchAllVersionsAsync()
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

            // Step 3: Download executable and get its version
            var versionInfo = await DownloadAndExtractVersionAsync(configurations);

            // Step 4: Save the versions and their URLs
            await SaveClientVersionsAsync(versionInfo);

            _logService.LogSuccess("Version fetch process completed.");
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
                    Directory.CreateDirectory(tempDir);
                    await ExtractAndRunManifestDownloader(url, tempDir);

                    string exePath = Path.Combine(tempDir, TargetFilename);
                    string version = GetExeVersion(exePath);

                    if (version != null)
                    {
                        versions.Add((region, "windows", version, url));
                    }
                }
                catch (Exception ex)
                {
                    _logService.LogError(ex, $"Error downloading from {url}");
                }
                finally
                {
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, true);
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

        private async Task ExtractAndRunManifestDownloader(string manifestUrl, string outputDir)
        {
            string arguments = $"\"{manifestUrl}\" -f {TargetFilename} -o \"{outputDir}\" -t 4";
            await RunManifestDownloaderAsync(arguments);
        }

        private async Task RunManifestDownloaderAsync(string arguments)
        {
            string resourceName = "AssetsManager.Resources.ManifestDownloader.ManifestDownloader.exe";
            string manifestDownloader = Path.Combine(_directoriesCreator.VersionsPath, "ManifestDownloader.exe");

            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                using (Stream stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                    {
                        _logService.LogError("Embedded resource 'ManifestDownloader.exe' not found.");
                        return;
                    }
                    using (FileStream fs = new FileStream(manifestDownloader, FileMode.Create, FileAccess.Write))
                    {
                        await stream.CopyToAsync(fs);
                    }
                }

                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = manifestDownloader,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (Process process = Process.Start(startInfo))
                {
                    // Asynchronous reading of the output streams to prevent deadlocks
                    var outputTask = process.StandardOutput.ReadToEndAsync();
                    var errorTask = process.StandardError.ReadToEndAsync();

                    await process.WaitForExitAsync();

                    string error = await errorTask;

                    if (process.ExitCode != 0)
                    {
                        if (!string.IsNullOrWhiteSpace(error))
                            _logService.LogError($"ManifestDownloader.exe failed with exit code {process.ExitCode}: {error}");
                        else
                            _logService.LogError($"ManifestDownloader.exe failed with exit code {process.ExitCode}");
                    }
                }
            }
            finally
            {
                if (File.Exists(manifestDownloader))
                {
                    File.Delete(manifestDownloader);
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

        public async Task DownloadPluginsAsync(string manifestUrl, string lolDirectory, List<string> locales)
        {
            if (string.IsNullOrEmpty(manifestUrl) || string.IsNullOrEmpty(lolDirectory) || locales == null || !locales.Any())
            {
                _logService.LogWarning("DownloadPluginsAsync called with invalid parameters.");
                return;
            }

            string localesArgument = string.Join(" ", locales);
            string arguments = $"\"{manifestUrl}\" -o \"{lolDirectory}\" -l {localesArgument} -n -t 4 skip-existing";

            _logService.Log("Starting updating the plugins...");
            await RunManifestDownloaderAsync(arguments);
            _logService.LogSuccess("Plugin download process finished.");
        }

        public async Task DownloadGameClientAsync(string manifestUrl, string lolDirectory, List<string> locales)
        {
            if (string.IsNullOrEmpty(manifestUrl) || string.IsNullOrEmpty(lolDirectory) || locales == null || !locales.Any())
            {
                _logService.LogWarning("DownloadGameClientAsync called with invalid parameters.");
                return;
            }

            string gameDirectory = Path.Combine(lolDirectory, "Game");
            string localesArgument = string.Join(" ", locales);
            string arguments = $"\"{manifestUrl}\" -o \"{gameDirectory}\" -l {localesArgument} -n -t 4 skip-existing";

            _logService.Log("Starting updating the game client...");
            await RunManifestDownloaderAsync(arguments);
            _logService.LogSuccess("Game client download process finished.");
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
