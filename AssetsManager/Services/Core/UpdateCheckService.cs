using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using AssetsManager.Utils;
using AssetsManager.Services.Monitor;
using AssetsManager.Services.Updater;
using AssetsManager.Services.Downloads;

namespace AssetsManager.Services.Core
{
    public class UpdateCheckService
    {
        private readonly AppSettings _appSettings;
        private readonly Status _status;
        private readonly JsonDataService _jsonDataService;
        private readonly UpdateManager _updateManager;
        private readonly LogService _logService;
        private readonly MonitorService _monitorService;
        private readonly PbeStatusService _pbeStatusService;
        private Timer _updateTimer;
        private Timer _assetTrackerTimer;
        private Timer _pbeStatusTimer;
        private bool _isCheckingAssets = false;

        public event Action<string, string> UpdatesFound;

        public string AvailableVersion { get; private set; }

        public UpdateCheckService(AppSettings appSettings, Status status, JsonDataService jsonDataService, UpdateManager updateManager, LogService logService, MonitorService monitorService, PbeStatusService pbeStatusService)
        {
            _appSettings = appSettings;
            _status = status;
            _jsonDataService = jsonDataService;
            _updateManager = updateManager;
            _logService = logService;
            _monitorService = monitorService;
            _pbeStatusService = pbeStatusService;
        }

        public void Start()
        {
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
                _logService.LogDebug($"Background update timer started. Frequency: {_appSettings.UpdateCheckFrequency} minutes.");
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
                _logService.LogDebug($"Asset Tracker timer started. Frequency: {_appSettings.AssetTrackerFrequency} minutes.");
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
                _logService.LogDebug($"PBE Status timer started. Frequency: {_appSettings.PbeStatusFrequency} minutes.");
            }
        }

        private async void UpdateTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            await CheckForGeneralUpdatesAsync(true);
        }

        private async void AssetTrackerTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            await CheckForAssetsAsync();
        }

        private async void PbeStatusTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            await CheckForPbeStatusAsync();
        }

        public void Stop()
        {
            if (_updateTimer != null)
            {
                _updateTimer.Enabled = false;
                _updateTimer.Elapsed -= UpdateTimer_Elapsed;
                _updateTimer.Dispose();
                _updateTimer = null;
                _logService.LogDebug("Background update timer stopped.");
            }
            if (_assetTrackerTimer != null)
            {
                _assetTrackerTimer.Enabled = false;
                _assetTrackerTimer.Elapsed -= AssetTrackerTimer_Elapsed;
                _assetTrackerTimer.Dispose();
                _assetTrackerTimer = null;
                _logService.LogDebug("Asset Tracker timer stopped.");
            }
            if (_pbeStatusTimer != null)
            {
                _pbeStatusTimer.Enabled = false;
                _pbeStatusTimer.Elapsed -= PbeStatusTimer_Elapsed;
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
            if (_isCheckingAssets)
            {
                _logService.LogDebug("Asset check is already in progress. Skipping this run.");
                return;
            }

            _isCheckingAssets = true;
            try
            {
                var updatedCategoryNames = new List<string>();
                await _monitorService.CheckAllAssetCategoriesAsync(true, (categoryName) =>
                {
                    if (!updatedCategoryNames.Contains(categoryName))
                    {
                        updatedCategoryNames.Add(categoryName);
                    }
                });

                if (updatedCategoryNames.Any())
                {
                    if (updatedCategoryNames.Count == 1)
                    {
                        UpdatesFound?.Invoke($"New assets have been found in {updatedCategoryNames[0]} category", null);
                    }
                    else
                    {
                        string categories = string.Join(", ", updatedCategoryNames);
                        UpdatesFound?.Invoke($"New assets found in categories: {categories}", null);
                    }
                }
            }
            finally
            {
                _isCheckingAssets = false;
            }
        }

        /// <summary>
        /// Checks for PBE status changes from Riot's endpoint.
        /// This method is used by its dedicated background timer (_pbeStatusTimer).
        /// It fires an 'UpdatesFound' event if the status has changed.
        /// </summary>
        private async Task CheckForPbeStatusAsync()
        {
            string pbeStatusMessage = await _pbeStatusService.CheckPbeStatusAsync();
            if (!string.IsNullOrEmpty(pbeStatusMessage))
            {
                UpdatesFound?.Invoke(pbeStatusMessage, null);
            }
        }

        /// <summary>
        /// Checks for general updates: new application version, hashes, and monitored JSON files.
        /// This method is used by the background timer for general updates (_updateTimer).
        /// It fires individual 'UpdatesFound' events for each discovery.
        /// </summary>
        public async Task CheckForGeneralUpdatesAsync(bool silent = false)
        {
            var (appUpdateAvailable, newVersion) = await _updateManager.IsNewVersionAvailableAsync();

            if (appUpdateAvailable)
            {
                AvailableVersion = newVersion;
                UpdatesFound?.Invoke($"Version {newVersion} is available", newVersion);
            }
            else 
            {
                AvailableVersion = null;
            }

            if (_appSettings.SyncHashesWithCDTB)
            {
                await _status.SyncHashesIfNeeds(_appSettings.SyncHashesWithCDTB, silent, () =>
                {
                    if (silent)
                    {
                        UpdatesFound?.Invoke("New hashes are available", null);
                    }
                });
            }

            if (_appSettings.CheckJsonDataUpdates)
            {
                await _jsonDataService.CheckJsonDataUpdatesAsync(silent, (updatedFiles) =>
                {
                    if (updatedFiles != null && updatedFiles.Any())
                    {
                        if (updatedFiles.Count == 1)
                        {
                            UpdatesFound?.Invoke($"Monitored file updated: {updatedFiles[0]}", null);
                        }
                        else
                        {
                            string files = string.Join(", ", updatedFiles);
                            UpdatesFound?.Invoke($"Monitored files updated: {files}", null);
                        }
                    }
                });
            }
        }

        /// <summary>
        /// Orchestrator method called ONLY ONCE on application startup.
        /// It invokes all the individual check methods to perform a complete initial scan.
        /// Each individual check method is responsible for firing its own notification event.
        /// </summary>
        public async Task CheckForAllUpdatesAsync(bool silent = false)
        {
            // Checkeo al arrancar de Json Updates, Hashes and New Version App
            await CheckForGeneralUpdatesAsync(silent);

            // Checkeo al arrancar de PbeStatus
            if (_appSettings.CheckPbeStatus)
            {
                await CheckForPbeStatusAsync();
            }
        }
    }
}
