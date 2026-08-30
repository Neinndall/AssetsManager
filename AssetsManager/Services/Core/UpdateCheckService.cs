using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using AssetsManager.Utils;
using AssetsManager.Services.Monitor;
using AssetsManager.Services.Updater;
using AssetsManager.Services.Downloads;
using AssetsManager.Services.News;
using AssetsManager.Views.Models.Notifications;
using AssetsManager.Views.Models.News;

namespace AssetsManager.Services.Core
{
    public class UpdateCheckService
    {
        private readonly AppSettings _appSettings;
        private readonly Status _status;
        private readonly UpdateManager _updateManager;
        private readonly GitHubApiService _gitHubApiService;
        private readonly LogService _logService;
        private readonly MonitorService _monitorService;
        private readonly PbeStatusService _pbeStatusService;
        private readonly NewsService _newsService;
        private Timer _updateTimer;
        private Timer _assetTrackerTimer;
        private Timer _pbeStatusTimer;
        private Timer _newsTimer;
        private readonly BackgroundJobGate _generalUpdatesJob = new();
        private readonly BackgroundJobGate _assetTrackerJob = new();
        private readonly BackgroundJobGate _pbeStatusJob = new();
        private readonly BackgroundJobGate _newsJob = new();
        private string _notifiedStableVersionThisSession;
        private string _notifiedQaBuildShaThisSession;

        public event Action<string, string, NotificationCategory, string, NewsItemModel, Func<Window, Task>> UpdatesFound;

        public string AvailableVersion { get; private set; }

        public UpdateCheckService(AppSettings appSettings, Status status, UpdateManager updateManager, GitHubApiService gitHubApiService, LogService logService, MonitorService monitorService, PbeStatusService pbeStatusService, NewsService newsService)
        {
            _appSettings = appSettings;
            _status = status;
            _updateManager = updateManager;
            _gitHubApiService = gitHubApiService;
            _logService = logService;
            _monitorService = monitorService;
            _pbeStatusService = pbeStatusService;
            _newsService = newsService;
        }

        public void Start()
        {
            _generalUpdatesJob.Start();
            _assetTrackerJob.Start();
            _pbeStatusJob.Start();
            _newsJob.Start();

            // Start general updates timer
            if (_appSettings.BackgroundUpdates)
            {
                if (_updateTimer == null)
                {
                    _updateTimer = new Timer();
                    _updateTimer.Elapsed += UpdateTimer_Elapsed;
                    _updateTimer.AutoReset = true;
                }
                _updateTimer.Interval = _appSettings.UpdateCheckFrequency * 60 * 1000;
                _updateTimer.Enabled = true;
            }

            // Start Asset Tracker timer
            if (_appSettings.AssetTrackerTimer && _appSettings.AssetTrackerFrequency > 0)
            {
                if (_assetTrackerTimer == null)
                {
                    _assetTrackerTimer = new Timer();
                    _assetTrackerTimer.Elapsed += AssetTrackerTimer_Elapsed;
                    _assetTrackerTimer.AutoReset = true;
                }
                _assetTrackerTimer.Interval = _appSettings.AssetTrackerFrequency * 60 * 1000;
                _assetTrackerTimer.Enabled = true;
            }

            // Start PBE Status timer
            if (_appSettings.CheckPbeStatus && _appSettings.PbeStatusFrequency > 0)
            {
                if (_pbeStatusTimer == null)
                {
                    _pbeStatusTimer = new Timer();
                    _pbeStatusTimer.Elapsed += PbeStatusTimer_Elapsed;
                    _pbeStatusTimer.AutoReset = true;
                }
                _pbeStatusTimer.Interval = _appSettings.PbeStatusFrequency * 60 * 1000;
                _pbeStatusTimer.Enabled = true;
            }

            // Start News timer
            if (_appSettings.NewsUpdates && _appSettings.NewsUpdateFrequency > 0)
            {
                if (_newsTimer == null)
                {
                    _newsTimer = new Timer();
                    _newsTimer.Elapsed += NewsTimer_Elapsed;
                    _newsTimer.AutoReset = true;
                }
                _newsTimer.Interval = _appSettings.NewsUpdateFrequency * 60 * 1000;
                _newsTimer.Enabled = true;
            }
        }

        private async void NewsTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            await RunTimerJobAsync(CheckForNewsAsync, "News check");
        }

        private async void UpdateTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            await RunTimerJobAsync(() => CheckForGeneralUpdatesAsync(true), "Background update check");
        }

        private async void AssetTrackerTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            await RunTimerJobAsync(CheckForAssetsAsync, "Asset Tracker check");
        }

        private async void PbeStatusTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            await RunTimerJobAsync(CheckForPbeStatusAsync, "PBE status check");
        }

        public void Stop()
        {
            _generalUpdatesJob.Stop();
            _assetTrackerJob.Stop();
            _pbeStatusJob.Stop();
            _newsJob.Stop();

            if (_updateTimer != null)
            {
                _updateTimer.Dispose();
                _updateTimer = null;
                _logService.LogDebug("Background update timer stopped.");
            }
            if (_assetTrackerTimer != null)
            {
                _assetTrackerTimer.Dispose();
                _assetTrackerTimer = null;
                _logService.LogDebug("Asset Tracker timer stopped.");
            }
            if (_pbeStatusTimer != null)
            {
                _pbeStatusTimer.Dispose();
                _pbeStatusTimer = null;
                _logService.LogDebug("PBE Status timer stopped.");
            }
            if (_newsTimer != null)
            {
                _newsTimer.Dispose();
                _newsTimer = null;
                _logService.LogDebug("News timer stopped.");
            }
        }

        /// <summary>
        /// Checks for new assets in the Asset Tracker functionality.
        /// This method is used by its dedicated background timer (_assetTrackerTimer).
        /// It fires an 'UpdatesFound' event as soon as a new asset is detected.
        /// </summary>
        private async Task CheckForAssetsAsync()
        {
            bool completed = await _assetTrackerJob.TryRunAsync(async cancellationToken =>
            {
                var updatedCategoryNames = new List<string>();
                await _monitorService.CheckAllAssetCategoriesAsync(true, (categoryName) =>
                {
                    if (!updatedCategoryNames.Contains(categoryName))
                    {
                        updatedCategoryNames.Add(categoryName);
                    }
                }, cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();
                if (updatedCategoryNames.Any())
                {
                    if (updatedCategoryNames.Count == 1)
                    {
                        UpdatesFound?.Invoke($"New assets have been found in {updatedCategoryNames[0]} category", null, NotificationCategory.Tracker, "Asset Tracker Discovery", null, null);
                    }
                    else
                    {
                        string categories = string.Join(", ", updatedCategoryNames);
                        UpdatesFound?.Invoke($"New assets found in categories: {categories}", null, NotificationCategory.Tracker, "Asset Tracker Discovery", null, null);
                    }
                }
            });
            if (!completed) _logService.LogDebug("Asset check skipped because it is already running or monitoring stopped.");
        }

        /// <summary>
        /// Checks for PBE status changes from Riot's endpoint.
        /// This method is used by its dedicated background timer (_pbeStatusTimer).
        /// It fires an 'UpdatesFound' event if the status has changed.
        /// </summary>
        private async Task CheckForPbeStatusAsync()
        {
            bool completed = await _pbeStatusJob.TryRunAsync(async cancellationToken =>
            {
                string pbeStatusMessage = await _pbeStatusService.CheckPbeStatusAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.IsNullOrEmpty(pbeStatusMessage)) UpdatesFound?.Invoke(pbeStatusMessage, null, NotificationCategory.System, "PBE Status Update", null, null);
            });
            if (!completed) _logService.LogDebug("PBE status check skipped because it is already running or monitoring stopped.");
        }

        /// <summary>
        /// Checks for newly published Riot news articles.
        /// This method is used by its dedicated background timer (_newsTimer)
        /// and on-demand news refreshes.
        /// It fires an 'UpdatesFound' event per new article.
        /// </summary>
        public async Task CheckForNewsAsync()
        {
            bool completed = await _newsJob.TryRunAsync(async cancellationToken =>
            {
                var newItems = await _newsService.CheckForNewNewsAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                foreach (var item in newItems)
                {
                    string message = string.IsNullOrEmpty(item.CategoryTitle)
                        ? item.Title
                        : $"{item.Title} ({item.CategoryTitle})";
                    UpdatesFound?.Invoke(message, null, NotificationCategory.News, "Riot News", item, null);
                }
            });
            if (!completed) _logService.LogDebug("News check skipped because it is already running or monitoring stopped.");
        }

        /// <summary>
        /// Checks for general updates: new application version, hashes, and monitored JSON files.
        /// This method is used by the background timer for general updates (_updateTimer).
        /// It fires individual 'UpdatesFound' events for each discovery.
        /// </summary>
        public async Task CheckForGeneralUpdatesAsync(bool silent = false)
        {
            bool completed = await _generalUpdatesJob.TryRunAsync(async cancellationToken =>
            {
                var tasks = new List<Task>();

                tasks.Add(CheckApplicationUpdateAsync());
                if (VersionInfo.IsQA) tasks.Add(CheckExperimentalBuildAsync());

                if (_appSettings.SyncHashesWithCDTB)
                {
                    tasks.Add(_status.SyncHashesIfNeeds(_appSettings.SyncHashesWithCDTB, silent, () =>
                    {
                        if (silent && !cancellationToken.IsCancellationRequested)
                            UpdatesFound?.Invoke("New hashes are available!", null, NotificationCategory.Updates, "Hash Update", null, null);
                    }));
                }

                if (_appSettings.AssetWatcherUpdates) tasks.Add(CheckMonitoredAssetsAsync());

                await Task.WhenAll(tasks);
                cancellationToken.ThrowIfCancellationRequested();

                async Task CheckApplicationUpdateAsync()
                {
                    var (appUpdateAvailable, newVersion) = await _updateManager.IsNewVersionAvailableAsync();
                    cancellationToken.ThrowIfCancellationRequested();
                    AvailableVersion = appUpdateAvailable ? newVersion : null;
                    if (!appUpdateAvailable) return;

                    string currentVerStr = VersionInfo.BaseVersion.Replace("v", "");
                    string latestVerStr = newVersion.Replace("v", "");
                    if (!Version.TryParse(currentVerStr, out var currentVer) ||
                        !Version.TryParse(latestVerStr, out var latestVer)) return;

                    if (string.Equals(_notifiedStableVersionThisSession, newVersion, StringComparison.OrdinalIgnoreCase)) return;

                    _notifiedStableVersionThisSession = newVersion;

                    string message = VersionInfo.IsQA && latestVer <= currentVer
                        ? $"New stable version {newVersion} is available!"
                        : $"New version {newVersion} is available!";
                    UpdatesFound?.Invoke(message, newVersion, NotificationCategory.Updates, "App Update Available", null, null);
                }

                async Task CheckExperimentalBuildAsync()
                {
                    var commits = await _gitHubApiService.GetEnrichedCommitsAsync("qa", "qa-testing", 100);
                    cancellationToken.ThrowIfCancellationRequested();

                    if (commits == null || commits.Count == 0) return;

                    string installedSha = VersionInfo.QaCommitSha;
                    if (string.IsNullOrEmpty(installedSha)) return;

                    var installedCommit = commits.FirstOrDefault(commit =>
                        string.Equals(commit.ShortSha, installedSha, StringComparison.OrdinalIgnoreCase));
                    if (installedCommit == null)
                    {
                        _logService.LogWarning($"Installed QA commit '{installedSha}' was not found in the recent QA history.");
                        return;
                    }

                    var latestBuild = commits.FirstOrDefault(commit =>
                        commit.DownloadableAsset != null &&
                        !string.IsNullOrWhiteSpace(commit.DownloadableAsset.DownloadUrl));
                    if (latestBuild == null) return;

                    int installedIndex = commits.IndexOf(installedCommit);
                    int latestBuildIndex = commits.IndexOf(latestBuild);
                    if (installedIndex < 0 || latestBuildIndex < 0 || latestBuildIndex >= installedIndex) return;

                    string latestBuildSha = latestBuild.ShortSha;
                    if (string.IsNullOrEmpty(latestBuildSha) ||
                        string.Equals(_notifiedQaBuildShaThisSession, latestBuildSha, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    string availableVersion = $"{VersionInfo.BaseVersion}-{latestBuildSha}";
                    _notifiedQaBuildShaThisSession = latestBuildSha;
                    UpdatesFound?.Invoke(
                        $"New experimental version {availableVersion} is available!",
                        null,
                        NotificationCategory.Updates,
                        "App Update Available",
                        null,
                        owner => _updateManager.DownloadAndInstallDevelopmentBuildAsync(
                            latestBuild.DownloadableAsset.DownloadUrl,
                            latestBuild.DownloadableAsset.Size,
                            latestBuildSha,
                            owner));
                }

                async Task CheckMonitoredAssetsAsync()
                {
                    var (anyUpdated, updatedNames) = await _monitorService.CheckAssetsUpdatesAsync(silent);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!anyUpdated) return;

                    string message = updatedNames.Count > 0
                        ? $"Monitored assets updated: {string.Join(", ", updatedNames)}"
                        : "Some monitored local assets have been updated!";
                    UpdatesFound?.Invoke(message, null, NotificationCategory.Watcher, "Monitored Assets Updated", null, null);
                }
            });
            if (!completed) _logService.LogDebug("General update check skipped because it is already running or monitoring stopped.");
        }

        /// <summary>
        /// Orchestrator method called ONLY ONCE on application startup.
        /// It invokes all the individual check methods to perform a complete initial scan.
        /// Each individual check method is responsible for firing its own notification event.
        /// </summary>
        public async Task CheckForAllUpdatesAsync(bool silent = false)
        {
            var tasks = new List<Task>();

            // Checkeo al arrancar de Watcher Updates, Hashes and New Version App
            tasks.Add(CheckForGeneralUpdatesAsync(silent));

            // Checkeo al arrancar de PbeStatus
            if (_appSettings.CheckPbeStatus && _appSettings.PbeStatusFrequency > 0)
            {
                tasks.Add(CheckForPbeStatusAsync());
            }

            // Checkeo al arrancar del Asset Tracker (CDN)
            if (_appSettings.AssetTrackerTimer && _appSettings.AssetTrackerFrequency > 0)
            {
                tasks.Add(CheckForAssetsAsync());
            }

            // Checkeo al arrancar de News
            if (_appSettings.NewsUpdates && _appSettings.NewsUpdateFrequency > 0)
            {
                tasks.Add(CheckForNewsAsync());
            }

            await Task.WhenAll(tasks);
        }

        private async Task RunTimerJobAsync(Func<Task> operation, string name)
        {
            try
            {
                await operation();
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"{name} failed.");
            }
        }
    }
}
