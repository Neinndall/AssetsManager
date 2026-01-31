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
        private const string BaseUrl = "https://sieve.services.riotcdn.net/api/v1/products/lol/version-sets";
        private const string ClientReleasesUrl = "https://clientconfig.rpg.riotgames.com/api/v1/config/public?namespace=keystone.products.league_of_legends.patchlines";
        private const string TargetFilename = "LeagueClient.exe";
        private static readonly string[] VersionSets = { "PBE1" };
        private static readonly Dictionary<string, string> RegionMap = new Dictionary<string, string> { { "PBE", "PBE1" } };

        public VersionService(LogService logService, HttpClient httpClient, DirectoriesCreator directoriesCreator)
        {
            _logService = logService;
            _httpClient = httpClient;
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

            if (!versionInfo.Any() && configurations.Any())
            {
                // Rely on the ManifestDownloader.exe specific error for now.
                return;
            }

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

            try
            {
                ExtractManifestDownloader();

                foreach (var (region, url) in configs)
                {
                    if (urlsSeen.Contains(url)) continue;
                    urlsSeen.Add(url);

                    try
                    {
                        Directory.CreateDirectory(tempDir);
                        var process = await ExtractAndRunManifestDownloader(url, tempDir);
                        await process.WaitForExitAsync();

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
            }
            finally
            {
                await CleanupManifestDownloaderAsync();
                VersionDownloadCompleted?.Invoke(this, ("Downloading League Client Executable", true, "Finished"));
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

        private async Task<Process> ExtractAndRunManifestDownloader(string manifestUrl, string outputDir, CancellationToken cancellationToken = default)
        {
            string arguments = $"\"{manifestUrl}\" -f {TargetFilename} -o \"{outputDir}\" -t 4";
            return await RunManifestDownloaderAsync(arguments, "Downloading League Client Executable", cancellationToken: cancellationToken);
        }

        private void ExtractManifestDownloader()
        {
            string resourceName = "AssetsManager.Resources.ManifestDownloader.ManifestDownloader.exe";
            string manifestDownloaderPath = Path.Combine(_directoriesCreator.VersionsPath, "ManifestDownloader.exe");

            if (File.Exists(manifestDownloaderPath)) return;

            var assembly = Assembly.GetExecutingAssembly();
            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                {
                    _logService.LogError("Embedded resource 'ManifestDownloader.exe' not found.");
                    throw new FileNotFoundException("Embedded resource 'ManifestDownloader.exe' not found.");
                }
                using (FileStream fs = new FileStream(manifestDownloaderPath, FileMode.Create, FileAccess.Write))
                {
                    stream.CopyTo(fs);
                }
            }
        }

        private async Task CleanupManifestDownloaderAsync()
        {
            string manifestDownloaderPath = Path.Combine(_directoriesCreator.VersionsPath, "ManifestDownloader.exe");
            if (!File.Exists(manifestDownloaderPath)) return;

            // Wait a bit to ensure the process has fully released the file handle
            await Task.Delay(200);

            try
            {
                File.Delete(manifestDownloaderPath);
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to cleanup ManifestDownloader.exe");
            }
        }

        private async Task<(int FileCount, bool Success)> GetManifestFileCountAsync(string manifestUrl, string outputDirectory, string localesArgument, CancellationToken cancellationToken)
        {
            string arguments = $"\"{manifestUrl}\" -o \"{outputDirectory}\" -l {localesArgument} -n -t 4 --verify-only skip-existing";
            string manifestDownloader = Path.Combine(_directoriesCreator.VersionsPath, "ManifestDownloader.exe");

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = manifestDownloader,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            int filesToUpdate = 0;
            bool success = true;
            using (Process process = Process.Start(startInfo))
            {
                var outputReader = process.StandardOutput;
                var errorReader = process.StandardError;

                var outputTask = Task.Run(async () =>
                {
                    string line;
                    // FASE DE VERIFICACIÓN: Aquí es donde calculamos el TOTAL real.
                    Regex incorrectRegex = new Regex(@"^\s*File\s+(.+?)\s+is\s+incorrect\s*\.?\s*$", RegexOptions.IgnoreCase);
                    Regex missingRegex = new Regex(@"^\s*File\s+(.+?)\s+is\s+missing\s*\.?\s*$", RegexOptions.IgnoreCase);

                    while ((line = await outputReader.ReadLineAsync()) != null)
                    {
                        _logService.LogDebug($"[ManifestDownloader PRE-VERIFY] {line}");
                        if (incorrectRegex.IsMatch(line) || missingRegex.IsMatch(line))
                        {
                            filesToUpdate++;
                        }
                    }
                });

                var errorTask = errorReader.ReadToEndAsync(); 
                
                await Task.WhenAll(process.WaitForExitAsync(cancellationToken), outputTask);

                if (process.ExitCode != 0)
                {
                    success = false;
                    _logService.LogError($"[ManifestDownloader PRE-VERIFY] Exited with code {process.ExitCode}. Error output: {errorTask.Result}");
                }
            }
            return (filesToUpdate, success);
        }

        private async Task<Process> RunManifestDownloaderAsync(string arguments, string taskName, int totalFiles = 0, bool silent = false, CancellationToken cancellationToken = default)
        {
            string manifestDownloader = Path.Combine(_directoriesCreator.VersionsPath, "ManifestDownloader.exe");

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = manifestDownloader,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            Process process = Process.Start(startInfo);
            
            var outputTask = Task.Run(async () =>
            {
                string line;
                int fileCounter = 0;
                // FASE DE DESCARGA: Solo detectamos acciones para avanzar el marcador.
                Regex fixingUpRegex = new Regex(@"^\s*Fixing up file\s+(.+?)\.\.\.", RegexOptions.IgnoreCase);
                Regex downloadingRegex = new Regex(@"^\s*Downloading file\s+(.+?)\.\.\.", RegexOptions.IgnoreCase);

                while ((line = await process.StandardOutput.ReadLineAsync()) != null)
                {
                    if (silent) continue;

                    // Avanzamos nuestro contador con cada archivo procesado (Fixing o Downloading)
                    Match fixingMatch = fixingUpRegex.Match(line);
                    Match downloadingMatch = downloadingRegex.Match(line);
                    string fileName = null;

                    if (fixingMatch.Success)
                    {
                        fileName = fixingMatch.Groups[1].Value;
                    }
                    else if (downloadingMatch.Success)
                    {
                        fileName = downloadingMatch.Groups[1].Value;
                    }

                    if (fileName != null)
                    {
                        fileCounter++;
                        string displayName = Path.GetFileName(fileName);
                        // totalFiles viene de la fase de verificación previa
                        VersionDownloadProgressChanged?.Invoke(this, (taskName, fileCounter, totalFiles, displayName));
                    }
                }
            }, cancellationToken);

            var errorTask = Task.Run(async () =>
            {
                string line;
                while ((line = await process.StandardError.ReadLineAsync()) != null)
                {
                    _logService.LogError($"[ManifestDownloader] {line}");
                }
            }, cancellationToken);

            if (silent)
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            
            return process;
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
            Process process = null;

            try
            {
                _logService.Log($"Starting verifying/updating the {taskName.ToLower()}...");
                ExtractManifestDownloader();

                if (string.IsNullOrEmpty(manifestUrl) || string.IsNullOrEmpty(targetDirectory) || locales == null || !locales.Any())
                {
                    _logService.LogWarning($"{taskName} called with invalid parameters.");
                    VersionDownloadCompleted?.Invoke(this, (taskName, false, "Invalid parameters."));
                    return;
                }

                VersionDownloadStarted?.Invoke(this, taskName);
                VersionDownloadProgressChanged?.Invoke(this, (taskName, 0, 0, "Verify Files..."));

                string localesArgument = string.Join(" ", locales);
                (int totalFiles, bool preCheckSuccess) = await GetManifestFileCountAsync(manifestUrl, targetDirectory, localesArgument, cancellationToken);

                if (!preCheckSuccess)
                {
                    message = $"{taskName} verification failed.";
                    return; 
                }

                cancellationToken.ThrowIfCancellationRequested();

                string arguments = $"\"{manifestUrl}\" -o \"{targetDirectory}\" -l {localesArgument} -n -t 4 skip-existing";

                process = await RunManifestDownloaderAsync(arguments, taskName, totalFiles, cancellationToken: cancellationToken);
                await process.WaitForExitAsync(cancellationToken);

                if (process.ExitCode == 0)
                {
                    _logService.LogSuccess($"{taskName} process finished.");
                    success = true;
                    message = $"{taskName} finished.";
                }
                else
                {
                    // Error is already logged in real-time by RunManifestDownloaderAsync
                    message = $"{taskName} failed with exit code {process.ExitCode}.";
                }
            }
            catch (OperationCanceledException)
            {
                _logService.LogWarning($"{taskName} was cancelled by the user.");
                if (process != null && !process.HasExited)
                {
                    process.Kill(true);
                    process.WaitForExit();
                }
                message = $"{taskName} cancelled.";
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"An error occurred during {taskName.ToLower()}.");
            }
            finally
            {
                process?.Dispose();
                await CleanupManifestDownloaderAsync();
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