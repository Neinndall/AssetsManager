using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
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
        private readonly AssetTrackerScannerService _assetTrackerScannerService;

        public ObservableRangeCollection<MonitoredAsset> MonitoredAssets { get; } = new ObservableRangeCollection<MonitoredAsset>();

        public event Action<AssetCategory> CategoryCheckStarted;
        public event Action<AssetCategory> CategoryCheckCompleted;

        public MonitorService(
            AppSettings appSettings,
            AssetWatcherService assetWatcherService,
            LogService logService,
            AssetTrackerScannerService assetTrackerScannerService)
        {
            _appSettings = appSettings;
            _assetWatcherService = assetWatcherService;
            _logService = logService;
            _assetTrackerScannerService = assetTrackerScannerService;

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

        public List<AssetCategory> AssetCategories { get; private set; } = new List<AssetCategory>();
        public void LoadAssetCategories()
        {
            AssetCategories = DefaultCategories.Get();
            foreach (var category in AssetCategories)
            {
                if (_appSettings.AssetTrackerUserRemovedIds.TryGetValue(category.Id, out var removedIds)) category.UserRemovedUrls = new List<long>(removedIds);
                if (_appSettings.AssetTrackerEntries.TryGetValue(category.Id, out var entries)) category.Entries = new Dictionary<long, AssetTrackerEntry>(entries);
            }
        }

        public List<TrackedAsset> GetAssetListForCategory(AssetCategory category)
        {
            if (category == null) return new List<TrackedAsset>();

            return GenerateAssetList(category);
        }

        private List<TrackedAsset> GenerateAssetList(AssetCategory category)
        {
            var removed = new HashSet<long>(category.UserRemovedUrls);
            var assets = category.Entries.Values
                .Where(entry => entry.WasCdnProbed && !removed.Contains(entry.AssetId) && entry.State == TrackedAssetState.Available)
                .OrderBy(entry => entry.AssetId)
                .Select(entry => new TrackedAsset
                {
                    AssetId = entry.AssetId,
                    Url = entry.Url ?? $"{category.BaseUrl}{entry.AssetId}.{category.Extension}",
                    DisplayName = entry.AssetId.ToString(),
                    State = entry.State,
                    Thumbnail = entry.State == TrackedAssetState.Available ? entry.Url : null
                })
                .ToList();

            IReadOnlyList<long> candidateIds = _assetTrackerScannerService.BuildCandidateIds(category);
            assets.AddRange(candidateIds.Select(id =>
            {
                if (category.Entries.TryGetValue(id, out AssetTrackerEntry entry))
                {
                    return new TrackedAsset
                    {
                        AssetId = id,
                        Url = entry.Url ?? $"{category.BaseUrl}{id}.{category.Extension}",
                        DisplayName = id.ToString(),
                        State = entry.State
                    };
                }

                return new TrackedAsset
                {
                    AssetId = id,
                    Url = $"{category.BaseUrl}{id}.{category.Extension}",
                    DisplayName = id.ToString(),
                    State = TrackedAssetState.Pending
                };
            }));
            return assets;
        }

        public List<TrackedAsset> GenerateMoreAssets(ObservableCollection<TrackedAsset> currentAssets, AssetCategory category, int amountToAdd)
        {
            var newAssets = new List<TrackedAsset>();
            if (category == null) return newAssets;

            long lastNumber = 0;
            if (currentAssets.Any())
            {
                var lastAssetId = GetAssetIdFromUrl(currentAssets.Last().Url);
                if (lastAssetId.HasValue) lastNumber = lastAssetId.Value;
            }

            // If the user has set a custom Start ID that is higher than current max, jump to it.
            if (category.Start > 0 && (category.Start - 1) > lastNumber)
            {
                lastNumber = category.Start - 1;
            }

            var existingIds = new HashSet<long>(currentAssets.Select(a => GetAssetIdFromUrl(a.Url) ?? -1));
            var trackedIds = new HashSet<long>(category.Entries.Keys);
            var removedIds = new HashSet<long>(category.UserRemovedUrls);

            int count = 0;
            while (count < amountToAdd)
            {
                lastNumber++;
                if (trackedIds.Contains(lastNumber) || existingIds.Contains(lastNumber) || removedIds.Contains(lastNumber)) continue;

                var url = $"{category.BaseUrl}{lastNumber}.{category.Extension}";
                try
                {
                    newAssets.Add(new TrackedAsset { Url = url, DisplayName = Path.GetFileNameWithoutExtension(new Uri(url).AbsolutePath), Status = "Pending" });
                    count++;
                }
                catch (UriFormatException)
                {
                    // Skip invalid URLs
                }
            }
            return newAssets;
        }

        private long? GetAssetIdFromUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            var match = Regex.Match(url, @"(\d+)(?!.*\d)");
            return match.Success && long.TryParse(match.Value, out long assetId) ? assetId : null;
        }

        private async Task<AssetTrackerScanResult> ScanAndSaveAsync(
            IEnumerable<long> idsToCheck,
            AssetCategory category,
            Action<string> onUpdatesFound = null,
            CancellationToken cancellationToken = default)
        {
            AssetTrackerScanResult result = await _assetTrackerScannerService.ScanAsync(category, idsToCheck, cancellationToken);
            SaveCategoryProgress(category);
            if (result.NewDiscoveries > 0) onUpdatesFound?.Invoke(category.Name);
            return result;
        }

        public async Task CheckAssetsAsync(List<TrackedAsset> assetsToCheck, AssetCategory category, CancellationToken cancellationToken)
        {
            await Application.Current.Dispatcher.InvokeAsync(() => category.Status = CategoryStatus.Checking);
            CategoryCheckStarted?.Invoke(category);
            try
            {
                var ids = assetsToCheck.Select(a => GetAssetIdFromUrl(a.Url)).Where(id => id.HasValue).Select(id => id.Value).ToList();
                if (!ids.Any())
                {
                    CategoryCheckCompleted?.Invoke(category);
                    return;
                }

                await ScanAndSaveAsync(ids, category, cancellationToken: cancellationToken);

                // After the core logic has run and updated the category, update the UI models
                foreach (var asset in assetsToCheck)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    long? assetId = GetAssetIdFromUrl(asset.Url);
                    if (!assetId.HasValue) continue;

                    if (category.Entries.TryGetValue(assetId.Value, out AssetTrackerEntry entry))
                    {
                        asset.AssetId = entry.AssetId;
                        asset.State = entry.State;
                        if (!string.IsNullOrWhiteSpace(entry.Url))
                        {
                            asset.Url = entry.Url;
                            if (entry.State == TrackedAssetState.Available)
                                asset.Thumbnail = entry.Url;
                        }
                    }
                }
                CategoryCheckCompleted?.Invoke(category);
            }
            finally
            {
                await Application.Current.Dispatcher.InvokeAsync(() => category.Status = CategoryStatus.CompletedSuccess);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(3000);
                        await Application.Current.Dispatcher.InvokeAsync(() => category.Status = CategoryStatus.Idle);
                    }
                    catch (Exception ex)
                    {
                        _logService.LogError(ex, "Failed to reset category status after delay.");
                    }
                });
            }
        }

        public async Task<bool> CheckAllAssetCategoriesAsync(bool silent, Action<string> onUpdatesFound = null, CancellationToken cancellationToken = default)
        {
            if (!AssetCategories.Any()) LoadAssetCategories();
            bool anyNewAssetFound = false;
            foreach (var category in AssetCategories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await CheckCategoryAsync(category, silent, onUpdatesFound, cancellationToken)) anyNewAssetFound = true;
            }
            return anyNewAssetFound;
        }

        private async Task<bool> CheckCategoryAsync(AssetCategory category, bool silent, Action<string> onUpdatesFound = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Application.Current.Dispatcher.InvokeAsync(() => category.Status = CategoryStatus.Checking);
            try
            {
                CategoryCheckStarted?.Invoke(category);

                IReadOnlyList<long> idsToCheck = _assetTrackerScannerService.BuildCandidateIds(category);

                AssetTrackerScanResult result = await ScanAndSaveAsync(idsToCheck, category, onUpdatesFound, cancellationToken);

                CategoryCheckCompleted?.Invoke(category);
                return result.NewDiscoveries > 0;
            }
            finally
            {
                await Application.Current.Dispatcher.InvokeAsync(() => category.Status = CategoryStatus.CompletedSuccess);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(3000);
                        await Application.Current.Dispatcher.InvokeAsync(() => category.Status = CategoryStatus.Idle);
                    }
                    catch (Exception ex)
                    {
                        _logService.LogError(ex, "Failed to reset category status after delay.");
                    }
                });
            }
        }

        private void SaveCategoryProgress(AssetCategory category)
        {
            _appSettings.AssetTrackerUserRemovedIds[category.Id] = category.UserRemovedUrls;
            _appSettings.AssetTrackerEntries[category.Id] = category.Entries;
            AppSettings.SaveSettings(_appSettings);
        }

        public void RemoveAsset(AssetCategory category, TrackedAsset assetToRemove)
        {
            long? assetId = GetAssetIdFromUrl(assetToRemove.Url);
            if (!assetId.HasValue) return;

            if (category.UserRemovedUrls.Contains(assetId.Value)) return;
            category.UserRemovedUrls.Add(assetId.Value);
            SaveCategoryProgress(category);
        }

        public void RemoveAllFoundAssets(AssetCategory category)
        {
            if (category == null) return;
            long[] foundIds = category.Entries.Values
                .Where(entry => entry.State == TrackedAssetState.Available)
                .Select(entry => entry.AssetId)
                .ToArray();
            if (foundIds.Length == 0) return;

            foreach (long id in foundIds)
                if (!category.UserRemovedUrls.Contains(id)) category.UserRemovedUrls.Add(id);
            SaveCategoryProgress(category);
        }

        #endregion
    }
}
