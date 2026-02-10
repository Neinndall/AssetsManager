using System;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Reflection;
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

        public async Task CheckForUpdatesAsync(Window owner = null, bool showNoUpdatesMessage = true)
        {
            string currentVersionRaw = Assembly.GetExecutingAssembly().GetName().Version.ToString();
            string apiUrl = "https://api.github.com/repos/Neinndall/AssetsManager/releases/latest";
            string downloadUrl = "";
            long totalBytes = 0;

            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("AssetsManager");

            try
            {
                // Llamamos a _directoriesCreator para crear la carpeta de update cache
                await _directoriesCreator.CreateDirectoryAsync(_directoriesCreator.UpdateCachePath);

                var response = await _httpClient.GetStringAsync(apiUrl);
                var releaseData = JsonConvert.DeserializeObject<dynamic>(response);

                string latestVersionRaw = releaseData.tag_name;
                downloadUrl = releaseData.assets[0].browser_download_url;
                totalBytes = releaseData.assets[0].size;

                string parsedCurrentVersion = Regex.Match(currentVersionRaw, @"\d+(\.\d+){1,3}").Value;
                string parsedLatestVersion = Regex.Match(latestVersionRaw.ToString(), @"\d+(\.\d+){1,3}").Value;

                if (string.IsNullOrEmpty(parsedCurrentVersion) || string.IsNullOrEmpty(parsedLatestVersion))
                {
                    _customMessageBoxService.ShowError("Error", "Could not parse version numbers.", owner);
                    return;
                }

                Version currentVer = new Version(parsedCurrentVersion);
                Version latestVer = new Version(parsedLatestVersion);

                if (latestVer.CompareTo(currentVer) > 0)
                {
                    bool? result = _customMessageBoxService.ShowYesNo(
                        "Update available",
                        $"New version available {latestVersionRaw}. Do you want to download it?",
                        owner
                    );

                    if (result == true)
                    {
                        string fileName = $"PBE_AssetsDownloader_{latestVersionRaw}.zip";
                        string downloadPath = Path.Combine(_directoriesCreator.UpdateCachePath, fileName);

                        // Check if the file already exists and has the correct size
                        if (File.Exists(downloadPath) && new FileInfo(downloadPath).Length == totalBytes)
                        {
                            _logService.Log("Update package already exists and has the correct size. Skipping download.");
                        }
                        else
                        {
                            UpdateProgressWindow progressWindow = null;
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                progressWindow = _serviceProvider.GetRequiredService<UpdateProgressWindow>();
                                progressWindow.Show();
                                progressWindow.UpdateLayout();
                            });

                            string downloadSize = $"{(totalBytes / 1024.0 / 1024.0):0.00} MB";

                            progressWindow.Dispatcher.Invoke(() =>
                            {
                                progressWindow.SetProgress(0, $"Downloading {downloadSize}...");
                            });
                            await Task.Delay(500);

                            using (var responseDownload = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
                            {
                                responseDownload.EnsureSuccessStatusCode();
                                long bytesDownloaded = 0;

                                using (var fs = new FileStream(downloadPath, FileMode.Create))
                                {
                                    byte[] buffer = new byte[8192];
                                    int bytesRead;

                                    using (var stream = await responseDownload.Content.ReadAsStreamAsync())
                                    {
                                        while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                                        {
                                            await fs.WriteAsync(buffer, 0, bytesRead);
                                            bytesDownloaded += bytesRead;

                                            if (totalBytes > 0)
                                            {
                                                int progressPercentage = (int)((bytesDownloaded * 100.0) / totalBytes);
                                                progressWindow.Dispatcher.Invoke(() =>
                                                {
                                                    progressWindow.SetProgress(progressPercentage,
                                                                                $"Downloading... {(bytesDownloaded / 1024.0 / 1024.0):0.00} MB / {downloadSize}");
                                                });
                                            }
                                        }
                                    }
                                }
                                await Task.Delay(1200);

                                progressWindow.Dispatcher.Invoke(() => { progressWindow.Close(); });
                            }
                        }

                        var dialog = _serviceProvider.GetRequiredService<UpdateModeDialog>();
                        dialog.Owner = owner;
                        bool? dialogResult = dialog.ShowDialog();

                        if (dialogResult == true)
                        {
                            if (dialog.SelectedMode == UpdateMode.CleanWithSaving)
                            {
                                await _updateExtractor.ExtractAndRestart(downloadPath, true, owner);
                            }
                            else if (dialog.SelectedMode == UpdateMode.CleanWithoutSaving)
                            {
                                await _updateExtractor.ExtractAndRestart(downloadPath, false, owner);
                            }
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
            catch (Exception ex)
            {
                _logService.LogError(ex, "Error checking for updates in UpdateManager.");
                _customMessageBoxService.ShowError("Error", "Error checking for updates:\n" + ex.Message, owner);
            }
        }

        public async Task<(bool, string)> IsNewVersionAvailableAsync()
        {
            string currentVersionRaw = Assembly.GetExecutingAssembly().GetName().Version.ToString();
            string apiUrl = "https://api.github.com/repos/Neinndall/AssetsManager/releases/latest";

            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("AssetsManager");

            try
            {
                var response = await _httpClient.GetStringAsync(apiUrl);
                var releaseData = JsonConvert.DeserializeObject<dynamic>(response);
                string latestVersionRaw = releaseData.tag_name;

                string parsedCurrentVersion = Regex.Match(currentVersionRaw, @"\d+(\.\d+){1,3}").Value;
                string parsedLatestVersion = Regex.Match(latestVersionRaw.ToString(), @"\d+(\.\d+){1,3}").Value;

                if (string.IsNullOrEmpty(parsedCurrentVersion) || string.IsNullOrEmpty(parsedLatestVersion))
                {
                    return (false, null);
                }

                Version currentVer = new Version(parsedCurrentVersion);
                Version latestVer = new Version(parsedLatestVersion);

                if (latestVer.CompareTo(currentVer) > 0)
                {
                    return (true, latestVersionRaw);
                }
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Error checking for new version in IsNewVersionAvailableAsync.");
            }

            return (false, null);
        }
    }
}