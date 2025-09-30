using AssetsManager.Utils;
using AssetsManager.Services.Downloads;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Timers;
using AssetsManager.Services.Monitor;

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
                    _updateTimer.Elapsed += async (sender, e) => await CheckForGeneralUpdatesAsync(true);
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
                    _assetTrackerTimer.Elapsed += async (sender, e) => await CheckForAssetsAsync();
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
                    _pbeStatusTimer.Elapsed += async (sender, e) => await CheckForPbeStatusAsync();
                    _pbeStatusTimer.AutoReset = true;
                }
                _pbeStatusTimer.Interval = _appSettings.PbeStatusFrequency * 60 * 1000;
                _pbeStatusTimer.Enabled = true;
                _logService.LogDebug($"PBE Status timer started. Frequency: {_appSettings.PbeStatusFrequency} minutes.");
            }
        }

        public void Stop()
        {
            if (_updateTimer != null)
            {
                _updateTimer.Enabled = false;
                _logService.LogDebug("Background update timer stopped.");
            }
            if (_assetTrackerTimer != null)
            {
                _assetTrackerTimer.Enabled = false;
                _logService.LogDebug("Asset Tracker timer stopped.");
            }
            if (_pbeStatusTimer != null)
            {
                _pbeStatusTimer.Enabled = false;
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
                await _monitorService.CheckAllAssetCategoriesAsync(true, () =>
                {
                    UpdatesFound?.Invoke("New category assets have been found!", null);
                });
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
                UpdatesFound?.Invoke($"Version {newVersion} is available!", newVersion);
            }
            
            if (_appSettings.SyncHashesWithCDTB)
            {
                await _status.SyncHashesIfNeeds(_appSettings.SyncHashesWithCDTB, silent, () =>
                {
                    if (silent)
                    {
                        UpdatesFound?.Invoke("New hashes are available!", null);
                    }
                });
            }

            if (_appSettings.CheckJsonDataUpdates)
            {
                await _jsonDataService.CheckJsonDataUpdatesAsync(silent, () => { UpdatesFound?.Invoke("JSON files have been updated!", null); });
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