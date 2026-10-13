using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using AssetsManager.Utils;
using AssetsManager.Utils.Framework;
using AssetsManager.Services.Core;
using AssetsManager.Views.Models.Monitor;

namespace AssetsManager.Services.Monitor
{
    public class MonitorService : IDisposable
    {
        private readonly AppSettings _appSettings;
        private readonly AssetWatcherService _assetWatcherService;
        private readonly LogService _logService;
        private readonly AssetCatalogService _assetCatalog;
        public AssetCatalogService Catalog => _assetCatalog;

        public ObservableRangeCollection<MonitoredAsset> MonitoredAssets { get; } = new ObservableRangeCollection<MonitoredAsset>();

        public event Action<AssetCategory> CategoryCheckStarted { add => _assetCatalog.CategoryCheckStarted += value; remove => _assetCatalog.CategoryCheckStarted -= value; }
        public event Action<AssetCategory> CategoryCheckCompleted { add => _assetCatalog.CategoryCheckCompleted += value; remove => _assetCatalog.CategoryCheckCompleted -= value; }

        public MonitorService(
            AppSettings appSettings,
            AssetWatcherService assetWatcherService,
            LogService logService,
            AssetCatalogService assetCatalog)
        {
            _appSettings = appSettings;
            _assetWatcherService = assetWatcherService;
            _logService = logService;
            _assetCatalog = assetCatalog;

            LoadMonitoredAssets();

            _assetWatcherService.AssetUpdated += OnAssetUpdated;
        }

        public void Dispose()
        {
            if (_assetWatcherService != null)
            {
                _assetWatcherService.AssetUpdated -= OnAssetUpdated;
            }
        }

        public void LoadMonitoredAssets()
        {
            MonitoredAssets.Clear();
            if (_appSettings.MonitoredAssets != null)
            {
                foreach (var asset in _appSettings.MonitoredAssets)
                {
                    // Update visual state based on current data
                    if (asset.LastUpdated != DateTime.MinValue && !asset.HasChanges)
                    {
                        asset.Status = AssetStatus.UpToDate;
                        asset.StatusColor = (SolidColorBrush)Application.Current.FindResource("AccentGreen");
                    }
                    else if (asset.HasChanges)
                    {
                        asset.Status = AssetStatus.Updated;
                        asset.StatusColor = (SolidColorBrush)Application.Current.FindResource("AccentBlue");
                    }
                    else
                    {
                        asset.Status = AssetStatus.Pending;
                        asset.StatusColor = (SolidColorBrush)Application.Current.FindResource("TextMuted");
                    }

                    MonitoredAssets.Add(asset);
                }
            }
        }

        private void OnAssetUpdated(MonitoredAsset asset)
        {
            // The AssetWatcherService already updates the asset object properties.
            // Since MonitoredAssets contains the same references as _appSettings.MonitoredAssets,
            // the UI will update automatically if the asset implements INotifyPropertyChanged.
        }

        public async Task<(bool anyUpdated, List<string> updatedAssetNames)> CheckAssetsUpdatesAsync(bool silent = false)
        {
            return await _assetWatcherService.CheckAssetsAsync(MonitoredAssets, silent);
        }

        #region Asset Tracker

        public List<AssetCategory> AssetCategories => _assetCatalog.Categories;
        public void LoadAssetCategories() => _assetCatalog.RefreshCategories();
        public List<TrackedAsset> GetAssetListForCategory(AssetCategory category) => _assetCatalog.GetAssetListForCategory(category);
        public List<TrackedAsset> GenerateMoreAssets(ObservableCollection<TrackedAsset> current, AssetCategory category, int amount) =>
            _assetCatalog.GenerateMoreAssets(current, category, amount);

        public async Task CheckAssetsAsync(List<TrackedAsset> assets, AssetCategory category, CancellationToken token)
        {
            await _assetCatalog.ScanAsync(category, assets, token);
            var refreshed = _assetCatalog.GetAssetListForCategory(category);
            foreach (var asset in assets)
            {
                var row = refreshed.FirstOrDefault(item => item.Url == asset.Url)
                    ?? refreshed.FirstOrDefault(item => item.AssetId == asset.AssetId && item.State == TrackedAssetState.Available);
                if (row == null) continue;
                asset.State = row.State;
                asset.Url = row.Url;
                asset.Thumbnail = row.Thumbnail;
            }
        }

        public Task<bool> CheckAllAssetCategoriesAsync(bool silent, Action<string> onUpdatesFound = null, CancellationToken cancellationToken = default) =>
            _assetCatalog.ScanAllAsync(onUpdatesFound, cancellationToken);
        public void RemoveAsset(AssetCategory category, TrackedAsset asset) => _assetCatalog.RemoveAsset(category, asset);
        public void RemoveAllFoundAssets(AssetCategory category) => _assetCatalog.RemoveAllFoundAssets(category);

        #endregion
    }
}
