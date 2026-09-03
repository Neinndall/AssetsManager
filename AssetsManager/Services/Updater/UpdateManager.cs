using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Reflection;
using System.Threading;
using System.Windows;
using Newtonsoft.Json;
using Microsoft.Extensions.DependencyInjection;
using AssetsManager.Services;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using AssetsManager.Views.Dialogs;

namespace AssetsManager.Services.Updater
{
    public class UpdateManager
    {
        private readonly LogService _logService;
        private readonly DirectoriesCreator _directoriesCreator;
        private readonly UpdateExtractor _updateExtractor;
        private readonly HttpClient _httpClient;
        private readonly IServiceProvider _serviceProvider;
        private readonly CustomMessageBoxService _customMessageBoxService;

        public UpdateManager(LogService logService, DirectoriesCreator directoriesCreator, HttpClient httpClient, UpdateExtractor updateExtractor, IServiceProvider serviceProvider, CustomMessageBoxService customMessageBoxService)
        {
            _logService = logService;
            _directoriesCreator = directoriesCreator;
            _httpClient = httpClient;
            _updateExtractor = updateExtractor;
            _serviceProvider = serviceProvider;
            _customMessageBoxService = customMessageBoxService;
        }

        public async Task CheckForUpdatesAsync(Window owner = null, bool showNoUpdatesMessage = true, CancellationToken cancellationToken = default)
        {
            string currentVersionRaw = Assembly.GetExecutingAssembly().GetName().Version.ToString();
            string apiUrl = "https://api.github.com/repos/Neinndall/AssetsManager/releases/latest";
            string downloadUrl = "";
            long totalBytes = 0;

            try
            {
                // Llamamos a _directoriesCreator para crear la carpeta de update cache
                _directoriesCreator.CreateDirectory(_directoriesCreator.UpdateCachePath);

                var response = await _httpClient.GetStringAsync(apiUrl, cancellationToken);
                var releaseData = JsonConvert.DeserializeObject<dynamic>(response);

                string latestVersionRaw = releaseData?.tag_name != null ? (string)releaseData.tag_name : null;
                var assets = releaseData?.assets;
                if (string.IsNullOrEmpty(latestVersionRaw) || assets == null || assets.Count == 0)
                {
                    _logService.LogWarning("Update check returned no usable release (missing tag or assets).");
                    if (showNoUpdatesMessage)
                    {
                        _customMessageBoxService.ShowInfo("Updates", "No updates available.", owner);
                    }
                    return;
                }

                downloadUrl = (string)assets[0].browser_download_url;
                totalBytes = (long?)assets[0].size ?? 0;
                if (string.IsNullOrEmpty(downloadUrl))
                {
                    _logService.LogWarning("Update check returned a release without download URL.");
                    if (showNoUpdatesMessage)
                    {
                        _customMessageBoxService.ShowInfo("Updates", "No updates available.", owner);
                    }
                    return;
                }

                string parsedCurrentVersion = Regex.Match(currentVersionRaw, @"\d+(\.\d+){1,3}").Value;
                string parsedLatestVersion = Regex.Match(latestVersionRaw.ToString(), @"\d+(\.\d+){1,3}").Value;

                if (string.IsNullOrEmpty(parsedCurrentVersion) || string.IsNullOrEmpty(parsedLatestVersion))
                {
                    _customMessageBoxService.ShowError("Error", "Could not parse version numbers.", owner);
                    return;
                }

                Version currentVer = new Version(parsedCurrentVersion);
                Version latestVer = new Version(parsedLatestVersion);

                bool isNewer = latestVer.CompareTo(currentVer) > 0;
                bool isExperimentalToStable = VersionInfo.IsQA;

                if (isNewer || isExperimentalToStable)
                {
                    string message = isExperimentalToStable && !isNewer 
                        ? $"A stable version {latestVersionRaw} is available. Do you want to return to the stable version?" 
                        : $"New version available {latestVersionRaw}. Do you want to download it?";

                    bool? result = _customMessageBoxService.ShowYesNo(
                        "Update available",
                        message,
                        owner
                    );

                    if (result == true)
                    {
                        string fileName = $"AssetsManager_{latestVersionRaw}.zip";
                        string downloadPath = Path.Combine(_directoriesCreator.UpdateCachePath, fileName);

                        // Check if the file already exists and has the correct size
                        if (File.Exists(downloadPath) && totalBytes > 0 && new FileInfo(downloadPath).Length == totalBytes)
                        {
                            _logService.Log("Update package already exists and has the correct size. Skipping download.");
                        }
                        else
                        {
                            await DownloadPackageWithProgressAsync(downloadUrl, downloadPath, totalBytes, owner, cancellationToken);
                        }

                        var dialog = _serviceProvider.GetRequiredService<UpdateModeDialog>();
                        dialog.Owner = owner;
                        bool? dialogResult = dialog.ShowDialog();

                        if (dialogResult == true)
                        {
                            bool saveSettings = dialog.SelectedMode == UpdateMode.CleanWithSaving;
                            await _updateExtractor.ExtractAndRestart(downloadPath, saveSettings, owner);
                        }
                        else
                        {
                            _customMessageBoxService.ShowInfo("Update Ready", $"Update downloaded to:\n{downloadPath}\n\nYou can install it manually later.", owner);
                        }
                    }
                }
                else if (showNoUpdatesMessage)
                {
                    _customMessageBoxService.ShowInfo("Updates", "No updates available.", owner);
                }
            }
            catch (OperationCanceledException)
            {
                _logService.Log("Update check was canceled.");
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Error checking for updates in UpdateManager.");
                _customMessageBoxService.ShowError("Error", "Error checking for updates:\n" + ex.Message, owner);
            }
        }

        public async Task DownloadAndInstallDevelopmentBuildAsync(string downloadUrl, long totalBytes, string shortSha, Window owner, CancellationToken cancellationToken = default)
        {
            try
            {
                string fileName = $"AssetsManager_dev_{shortSha}.zip";
                _directoriesCreator.CreateDirectory(_directoriesCreator.UpdateCachePath);
                string downloadPath = Path.Combine(_directoriesCreator.UpdateCachePath, fileName);

                await DownloadPackageWithProgressAsync(downloadUrl, downloadPath, totalBytes, owner, cancellationToken);

                var dialog = _serviceProvider.GetRequiredService<UpdateModeDialog>();
                dialog.Owner = owner;
                bool? dialogResult = dialog.ShowDialog();

                if (dialogResult == true)
                {
                    bool saveSettings = dialog.SelectedMode == UpdateMode.CleanWithSaving;
                    await _updateExtractor.ExtractAndRestart(downloadPath, saveSettings, owner);
                }
            }
            catch (OperationCanceledException)
            {
                _logService.Log("Development build download was canceled.");
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to download and install development build.");
                _customMessageBoxService.ShowError("Installation Error", "Failed to download or install the development build:\n" + ex.Message, owner);
            }
        }

        private async Task DownloadPackageWithProgressAsync(string downloadUrl, string downloadPath, long totalBytes, Window owner, CancellationToken cancellationToken)
        {
            UpdateProgressWindow progressWindow = null;
            try
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    progressWindow = _serviceProvider.GetRequiredService<UpdateProgressWindow>();
                    if (owner != null) progressWindow.Owner = owner;
                    progressWindow.Show();
                    progressWindow.UpdateLayout();
                });

                string downloadSize = totalBytes > 0 ? $"{(totalBytes / 1024.0 / 1024.0):0.00} MB" : "Unknown size";
                progressWindow.SetProgress(0, $"Downloading {downloadSize}...");
                await Task.Delay(300, cancellationToken);

                using (var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                {
                    response.EnsureSuccessStatusCode();
                    long effectiveTotalBytes = totalBytes > 0 ? totalBytes : (response.Content.Headers.ContentLength ?? 0);
                    long bytesDownloaded = 0;
                    int lastReportedPercentage = -1;
                    long lastReportTicks = 0;

                    using (var fs = new FileStream(downloadPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    {
                        byte[] buffer = new byte[81920];
                        int bytesRead;

                        using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
                        {
                            while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                            {
                                await fs.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                                bytesDownloaded += bytesRead;

                                if (effectiveTotalBytes > 0)
                                {
                                    int progressPercentage = (int)((bytesDownloaded * 100.0) / effectiveTotalBytes);
                                    long nowTicks = DateTime.UtcNow.Ticks;
                                    if (progressPercentage != lastReportedPercentage && nowTicks - lastReportTicks >= TimeSpan.FromMilliseconds(100).Ticks)
                                    {
                                        lastReportedPercentage = progressPercentage;
                                        lastReportTicks = nowTicks;
                                        long snapshot = bytesDownloaded;
                                        progressWindow.SetProgress(progressPercentage, $"Downloading... {(snapshot / 1024.0 / 1024.0):0.00} MB / {downloadSize}");
                                    }
                                }
                            }
                        }
                    }

                    // Guarantee 100% is displayed on completion
                    progressWindow.SetProgress(100, $"Downloading... {downloadSize} / {downloadSize}");
                    await Task.Delay(500, cancellationToken);
                }
            }
            catch
            {
                if (File.Exists(downloadPath))
                {
                    try { File.Delete(downloadPath); } catch { /* best-effort cleanup */ }
                }
                throw;
            }
            finally
            {
                if (progressWindow != null)
                {
                    await progressWindow.Dispatcher.InvokeAsync(() => progressWindow.Close());
                }
            }
        }

        private string _lastReleaseEtag;
        private string _lastLatestVersionRaw;

        public async Task<(bool, string)> IsNewVersionAvailableAsync(CancellationToken cancellationToken = default)
        {
            string currentVersionRaw = Assembly.GetExecutingAssembly().GetName().Version.ToString();
            string apiUrl = "https://api.github.com/repos/Neinndall/AssetsManager/releases/latest";

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                if (!string.IsNullOrEmpty(_lastReleaseEtag))
                {
                    request.Headers.TryAddWithoutValidation("If-None-Match", _lastReleaseEtag);
                }

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                string latestVersionRaw;

                if (response.StatusCode == System.Net.HttpStatusCode.NotModified && !string.IsNullOrEmpty(_lastLatestVersionRaw))
                {
                    latestVersionRaw = _lastLatestVersionRaw;
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    string webTag = await ResolveLatestVersionViaWebRedirectAsync(cancellationToken);
                    if (!string.IsNullOrEmpty(webTag))
                    {
                        latestVersionRaw = webTag;
                        _lastLatestVersionRaw = latestVersionRaw;
                    }
                    else if (!string.IsNullOrEmpty(_lastLatestVersionRaw))
                    {
                        latestVersionRaw = _lastLatestVersionRaw;
                    }
                    else
                    {
                        return (false, null);
                    }
                }
                else
                {
                    response.EnsureSuccessStatusCode();
                    
                    if (response.Headers.ETag != null)
                    {
                        _lastReleaseEtag = response.Headers.ETag.Tag;
                    }

                    var content = await response.Content.ReadAsStringAsync(cancellationToken);
                    var releaseData = JsonConvert.DeserializeObject<dynamic>(content);
                    latestVersionRaw = releaseData?.tag_name != null ? (string)releaseData.tag_name : null;
                    if (string.IsNullOrEmpty(latestVersionRaw))
                    {
                        return (false, null);
                    }
                    _lastLatestVersionRaw = latestVersionRaw;
                }

                string parsedCurrentVersion = Regex.Match(currentVersionRaw, @"\d+(\.\d+){1,3}").Value;
                string parsedLatestVersion = Regex.Match(latestVersionRaw.ToString(), @"\d+(\.\d+){1,3}").Value;

                if (string.IsNullOrEmpty(parsedCurrentVersion) || string.IsNullOrEmpty(parsedLatestVersion))
                {
                    return (false, null);
                }

                Version currentVer = new Version(parsedCurrentVersion);
                Version latestVer = new Version(parsedLatestVersion);

                if (latestVer.CompareTo(currentVer) > 0 || VersionInfo.IsQA)
                {
                    return (true, latestVersionRaw);
                }
            }
            catch (OperationCanceledException)
            {
                // Canceled cleanly
            }
            catch (Exception ex)
            {
                _logService.LogWarning($"Could not check for new version: {ex.Message}");
            }

            return (false, null);
        }

        private async Task<string> ResolveLatestVersionViaWebRedirectAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using var response = await _httpClient.GetAsync("https://github.com/Neinndall/AssetsManager/releases/latest", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                var finalUri = response.RequestMessage?.RequestUri;
                if (finalUri != null)
                {
                    string tag = finalUri.Segments.LastOrDefault()?.Trim('/');
                    if (!string.IsNullOrEmpty(tag) && !string.Equals(tag, "latest", StringComparison.OrdinalIgnoreCase))
                    {
                        return tag;
                    }
                }
            }
            catch (Exception ex)
            {
                _logService.LogDebug($"[UpdateManager] Web redirect version check failed: {ex.Message}");
            }
            return null;
        }
    }
}
