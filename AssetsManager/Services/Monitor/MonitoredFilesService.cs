using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using AssetsManager.Utils;
using AssetsManager.Utils.Framework;
using AssetsManager.Views.Models.Monitor;

namespace AssetsManager.Services.Monitor;

public sealed class MonitoredFilesService
{
    private readonly AppSettings _appSettings;
    private readonly AssetWatcherService _assetWatcherService;
    public ObservableRangeCollection<MonitoredAsset> MonitoredAssets { get; } = new();

    public MonitoredFilesService(AppSettings appSettings, AssetWatcherService assetWatcherService)
    {
        _appSettings = appSettings;
        _assetWatcherService = assetWatcherService;
        LoadMonitoredAssets();
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

    public Task<(bool anyUpdated, List<string> updatedAssetNames)> CheckAssetsUpdatesAsync(bool silent = false) =>
        _assetWatcherService.CheckAssetsAsync(MonitoredAssets, silent);
}
