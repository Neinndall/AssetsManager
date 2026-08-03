using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using System.Reflection;
using System.Text.RegularExpressions;
using AssetsManager.Utils;
using AssetsManager.Services.Monitor;
using AssetsManager.Services.Updater;
using AssetsManager.Services.Downloads;
using AssetsManager.Info;
using AssetsManager.Views.Models.Notifications;

namespace AssetsManager.Services.Core
{
    public class UpdateCheckService
    {
        private readonly AppSettings _appSettings;
        private readonly Status _status;
        private readonly UpdateManager _updateManager;
        private readonly LogService _logService;
        private readonly MonitorService _monitorService;
        private readonly PbeStatusService _pbeStatusService;
        private Timer _updateTimer;
        private Timer _assetTrackerTimer;
        private Timer _pbeStatusTimer;
        private readonly BackgroundJobGate _generalUpdatesJob = new();
        private readonly BackgroundJobGate _assetTrackerJob = new();
        private readonly BackgroundJobGate _pbeStatusJob = new();

        public event Action<string, string, NotificationCategory, string> UpdatesFound;

        public string AvailableVersion { get; private set; }

        public UpdateCheckService(AppSettings appSettings, Status status, UpdateManager updateManager, LogService logService, MonitorService monitorService, PbeStatusService pbeStatusService)
        {
            _appSettings = appSettings;
            _status = status;
            _updateManager = updateManager;
            _logService = logService;
            _monitorService = monitorService;
            _pbeStatusService = pbeStatusService;
        }

        public void Start()
        {
            _generalUpdatesJob.Start();
            _assetTrackerJob.Start();
            _pbeStatusJob.Start();

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
                        UpdatesFound?.Invoke($"New assets have been found in {updatedCategoryNames[0]} category", null, NotificationCategory.Tracker, "Asset Tracker Discovery");
                    }
                    else
                    {
                        string categories = string.Join(", ", updatedCategoryNames);
                        UpdatesFound?.Invoke($"New assets found in categories: {categories}", null, NotificationCategory.Tracker, "Asset Tracker Discovery");
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
                if (!string.IsNullOrEmpty(pbeStatusMessage)) UpdatesFound?.Invoke(pbeStatusMessage, null, NotificationCategory.System, "PBE Status Update");
            });
            if (!completed) _logService.LogDebug("PBE status check skipped because it is already running or monitoring stopped.");
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

                if (_appSettings.SyncHashesWithCDTB)
                {
                    tasks.Add(_status.SyncHashesIfNeeds(_appSettings.SyncHashesWithCDTB, silent, () =>
                    {
                        if (silent && !cancellationToken.IsCancellationRequested)
                            UpdatesFound?.Invoke("New hashes are available!", null, NotificationCategory.Updates, "Hash Update");
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

                    string currentVerStr = ApplicationInfos.Version.Split('-')[0].Replace("v", "");
                    string latestVerStr = newVersion.Replace("v", "");
                    if (!Version.TryParse(currentVerStr, out var currentVer) ||
                        !Version.TryParse(latestVerStr, out var latestVer)) return;

                    string message = ApplicationInfos.IsQA && latestVer <= currentVer
                        ? $"New stable version {newVersion} is available!"
                        : $"New version {newVersion} is available!";
                    UpdatesFound?.Invoke(message, newVersion, NotificationCategory.Updates, "App Update Available");
                }

                async Task CheckMonitoredAssetsAsync()
                {
                    var (anyUpdated, updatedNames) = await _monitorService.CheckAssetsUpdatesAsync(silent);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!anyUpdated) return;

                    string message = updatedNames.Count > 0
                        ? $"Monitored assets updated: {string.Join(", ", updatedNames)}"
                        : "Some monitored local assets have been updated!";
                    UpdatesFound?.Invoke(message, null, NotificationCategory.Watcher, "Monitored Assets Updated");
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

            // Checkeo al arrancar de Json Updates, Hashes and New Version App
            tasks.Add(CheckForGeneralUpdatesAsync(silent));

            // Checkeo al arrancar de PbeStatus
            if (_appSettings.CheckPbeStatus)
            {
                tasks.Add(CheckForPbeStatusAsync());
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
