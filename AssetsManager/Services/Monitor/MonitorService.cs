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
using AssetsManager.Services.Core;
using AssetsManager.Views.Models.Monitor;

namespace AssetsManager.Services.Monitor
{
    public class MonitorService : IDisposable
    {
        private readonly AppSettings _appSettings;
        private readonly JsonDataService _jsonDataService;
        private readonly LogService _logService;
        private readonly HttpClient _httpClient;

        public ObservableCollection<MonitoredUrl> MonitoredItems { get; } = new ObservableCollection<MonitoredUrl>();

        public event Action<AssetCategory> CategoryCheckStarted;
        public event Action<AssetCategory> CategoryCheckCompleted;

        public MonitorService(AppSettings appSettings, JsonDataService jsonDataService, LogService logService, HttpClient httpClient)
        {
            _appSettings = appSettings;
            _jsonDataService = jsonDataService;
            _logService = logService;
            _httpClient = httpClient;

            LoadMonitoredUrls();

            _jsonDataService.FileUpdated += OnFileUpdated;
        }

        public void Dispose()
        {
            _jsonDataService.FileUpdated -= OnFileUpdated;
        }

        public void LoadMonitoredUrls()
        {
            MonitoredItems.Clear();
            foreach (var url in _appSettings.MonitoredJsonFiles)
            {
                _appSettings.JsonDataModificationDates.TryGetValue(url, out DateTime lastUpdated);

                string statusText = "Pending check";
                string lastChecked = "N/A";
                Brush statusColor = new SolidColorBrush(Colors.Gray);

                if (lastUpdated != DateTime.MinValue)
                {
                    statusText = "Up-to-date";
                    lastChecked = $"Last Update: {lastUpdated:yyyy-MMM-dd HH:mm}";
                    statusColor = new SolidColorBrush(Colors.MediumSeaGreen);
                }

                MonitoredItems.Add(new MonitoredUrl
                {
                    Alias = GetAliasForUrl(url),
                    Url = url,
                    StatusText = statusText,
                    StatusColor = statusColor,
                    LastChecked = lastChecked,
                    HasChanges = false
                });
            }
        }

        private void OnFileUpdated(FileUpdateInfo fileUpdateInfo)
        {
            var item = MonitoredItems.FirstOrDefault(x => x.Url == fileUpdateInfo.FullUrl);
            if (item != null)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    item.StatusText = "Updated";
                    item.StatusColor = new SolidColorBrush(Colors.DodgerBlue);
                    item.LastChecked = $"Last Update: {fileUpdateInfo.Timestamp:yyyy-MMM-dd HH:mm}";
                    item.HasChanges = true;
                    item.OldFilePath = fileUpdateInfo.OldFilePath;
                    item.NewFilePath = fileUpdateInfo.NewFilePath;
                });
                _appSettings.JsonDataModificationDates[fileUpdateInfo.FullUrl] = fileUpdateInfo.Timestamp;
                AppSettings.SaveSettings(_appSettings);
            }
        }

        private string GetAliasForUrl(string url)
        {
            try
            {
                var uri = new Uri(url);
                return uri.Segments.Last();
            }
            catch
            {
                return url;
            }
        }

        #region Asset Tracker

        public List<AssetCategory> AssetCategories { get; private set; } = new List<AssetCategory>();
        public void LoadAssetCategories()
        {
            AssetCategories = DefaultCategories.Get();
            foreach (var category in AssetCategories)
            {
                if (_appSettings.AssetTrackerProgress.TryGetValue(category.Id, out long lastValid)) category.LastValid = lastValid;
                if (_appSettings.AssetTrackerFailedIds.TryGetValue(category.Id, out var failedIds)) category.FailedUrls = new List<long>(failedIds);
                if (_appSettings.AssetTrackerUserRemovedIds.TryGetValue(category.Id, out var removedIds)) category.UserRemovedUrls = new List<long>(removedIds);
                if (_appSettings.AssetTrackerFoundIds.TryGetValue(category.Id, out var foundIds)) category.FoundUrls = new List<long>(foundIds);
                if (_appSettings.AssetTrackerUrlOverrides.TryGetValue(category.Id, out var overrides)) category.FoundUrlOverrides = new Dictionary<long, string>(overrides);
            }
        }

        public ObservableCollection<TrackedAsset> GetAssetListForCategory(AssetCategory category)
        {
            if (category == null) return new ObservableCollection<TrackedAsset>();

            var assets = GenerateNewAssetList(category);
            return new ObservableCollection<TrackedAsset>(assets);
        }

        private List<TrackedAsset> GenerateNewAssetList(AssetCategory category)
        {
            var assets = new List<TrackedAsset>();
            if (category == null) return assets;

            // 1. Add all "OK" assets first, sorted by ID
            var foundIds = new HashSet<long>(category.FoundUrls);
            foundIds.ExceptWith(category.UserRemovedUrls);

            foreach (long id in foundIds.OrderBy(i => i))
            {
                string url = category.FoundUrlOverrides.TryGetValue(id, out var overrideUrl) ? overrideUrl : $"{category.BaseUrl}{id}.{category.Extension}";
                try
                {
                    string displayName = Path.GetFileName(new Uri(url).AbsolutePath);
                    if (string.IsNullOrEmpty(displayName)) displayName = $"Asset ID: {id}";

                    assets.Add(new TrackedAsset
                    {
                        Url = url,
                        DisplayName = displayName,
                        Status = "OK",
                        Thumbnail = url
                    });
                }
                catch (UriFormatException) { /* Skip invalid URLs */ }
            }

            // 2. Prepare the list of 10 "Checkable" assets (Failed + Pending)
            var checkableAssets = new List<TrackedAsset>();
            var failedIds = new HashSet<long>(category.FailedUrls);
            failedIds.ExceptWith(category.UserRemovedUrls);

            foreach (long id in failedIds.OrderBy(i => i))
            {
                string url = $"{category.BaseUrl}{id}.{category.Extension}";
                try
                {
                    string displayName = Path.GetFileName(new Uri(url).AbsolutePath);
                    if (string.IsNullOrEmpty(displayName)) displayName = $"Asset ID: {id}";
                    checkableAssets.Add(new TrackedAsset
                    {
                        Url = url,
                        DisplayName = displayName,
                        Status = "Not Found"
                    });
                }
                catch (UriFormatException) { /* Skip invalid URLs */ }
            }

            // 3. Fill up to 10 with "Pending"
            int needed = 10 - checkableAssets.Count;
            if (needed > 0)
            {
                long lastKnownId = 0;
                var allKnownIds = new HashSet<long>(foundIds);
                allKnownIds.UnionWith(failedIds);
                allKnownIds.UnionWith(category.UserRemovedUrls);

                if (allKnownIds.Any())
                {
                    lastKnownId = allKnownIds.Max();
                }
                else
                {
                    lastKnownId = category.Start > 0 ? category.Start - 1 : 0;
                }

                int count = 0;
                while (count < needed)
                {
                    lastKnownId++;
                    if (allKnownIds.Contains(lastKnownId)) continue;

                    string url = $"{category.BaseUrl}{lastKnownId}.{category.Extension}";
                    try
                    {
                        string displayName = Path.GetFileName(new Uri(url).AbsolutePath);
                        if (string.IsNullOrEmpty(displayName)) displayName = $"Asset ID: {lastKnownId}";
                        checkableAssets.Add(new TrackedAsset { Url = url, DisplayName = displayName, Status = "Pending" });
                        count++;
                    }
                    catch (UriFormatException) { /* Skip invalid URLs */ }
                }
            }

            // 4. Add the checkable assets to the main list
            assets.AddRange(checkableAssets);

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
            else
            {
                lastNumber = category.Start > 0 ? category.Start - 1 : 0;
            }

            var existingIds = new HashSet<long>(currentAssets.Select(a => GetAssetIdFromUrl(a.Url) ?? -1));
            var foundIds = new HashSet<long>(category.FoundUrls); // Get Found IDs
            var failedIds = new HashSet<long>(category.FailedUrls);
            var removedIds = new HashSet<long>(category.UserRemovedUrls); // Get removed IDs

            int count = 0;
            while (count < amountToAdd)
            {
                lastNumber++;
                // Check against all lists to prevent duplicates
                if (foundIds.Contains(lastNumber) || failedIds.Contains(lastNumber) || existingIds.Contains(lastNumber) || removedIds.Contains(lastNumber)) continue;

                var url = $"{category.BaseUrl}{lastNumber}.{category.Extension}";
                try
                {
                    newAssets.Add(new TrackedAsset { Url = url, DisplayName = Path.GetFileName(new Uri(url).AbsolutePath), Status = "Pending" });
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

        private async Task<(bool IsSuccess, string FoundUrl)> PerformCheckAsync(long id, AssetCategory category)
        {
            string primaryUrl = $"{category.BaseUrl}{id}.{category.Extension}";
            using var request = new HttpRequestMessage(HttpMethod.Head, primaryUrl);
            var response = await _httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode) return (true, primaryUrl);

            if (category.Id == "3" || category.Id == "11")
            {
                string fallbackUrl = $"{category.BaseUrl}{id}.png";
                using var fallbackRequest = new HttpRequestMessage(HttpMethod.Head, fallbackUrl);
                var fallbackResponse = await _httpClient.SendAsync(fallbackRequest);
                if (fallbackResponse.IsSuccessStatusCode) return (true, fallbackUrl);
            }

            return (false, null);
        }

        private async Task<(bool anyNewAssetFound, bool progressChanged)> _InternalCheckLogicAsync(IEnumerable<long> idsToCheck, AssetCategory category, Action<string> onUpdatesFound = null)
        {
            bool anyNewAssetFound = false;
            bool progressChanged = false;
            bool notificationSent = false;

            foreach (long id in idsToCheck.OrderBy(i => i))
            {
                var (isSuccess, foundUrl) = await PerformCheckAsync(id, category);
                if (isSuccess)
                {
                    if (!category.FoundUrls.Contains(id))
                    {
                        category.FoundUrls.Add(id);
                        anyNewAssetFound = true;
                        progressChanged = true;
                        if (onUpdatesFound != null && !notificationSent)
                        {
                            onUpdatesFound.Invoke(category.Name);
                            notificationSent = true;
                        }
                    }
                    string primaryUrl = $"{category.BaseUrl}{id}.{category.Extension}";
                    if (foundUrl != primaryUrl) { category.FoundUrlOverrides[id] = foundUrl; progressChanged = true; }
                    if (category.FailedUrls.Remove(id)) progressChanged = true;
                }
                else
                {
                    if (!category.FailedUrls.Contains(id)) { category.FailedUrls.Add(id); progressChanged = true; }
                }
            }

            if (progressChanged)
            {
                SaveCategoryProgress(category);
            }

            return (anyNewAssetFound, progressChanged);
        }

        public async Task CheckAssetsAsync(List<TrackedAsset> assetsToCheck, AssetCategory category, CancellationToken cancellationToken)
        {
            Application.Current.Dispatcher.Invoke(() => category.Status = CategoryStatus.Checking);
            CategoryCheckStarted?.Invoke(category);
            try
            {
                var ids = assetsToCheck.Select(a => GetAssetIdFromUrl(a.Url)).Where(id => id.HasValue).Select(id => id.Value).ToList();
                if (!ids.Any())
                {
                    CategoryCheckCompleted?.Invoke(category);
                    return;
                }

                await _InternalCheckLogicAsync(ids, category);

                // After the core logic has run and updated the category, update the UI models
                foreach (var asset in assetsToCheck)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    long? assetId = GetAssetIdFromUrl(asset.Url);
                    if (!assetId.HasValue) continue;

                    if (category.FoundUrls.Contains(assetId.Value))
                    {
                        asset.Status = "OK";
                        asset.Thumbnail = category.FoundUrlOverrides.TryGetValue(assetId.Value, out var url) ? url : asset.Url;
                    }
                    else
                    {
                        asset.Status = "Not Found";
                    }
                }
                CategoryCheckCompleted?.Invoke(category);
            }
            finally
            {
                await Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    category.Status = CategoryStatus.CompletedSuccess;
                    await Task.Delay(3000);
                    category.Status = CategoryStatus.Idle;
                });
            }
        }

        public async Task<bool> CheckAllAssetCategoriesAsync(bool silent, Action<string> onUpdatesFound = null)
        {
            if (!AssetCategories.Any()) LoadAssetCategories();
            bool anyNewAssetFound = false;
            foreach (var category in AssetCategories)
            {
                if (await _CheckCategoryAsync(category, silent, onUpdatesFound)) anyNewAssetFound = true;
            }
            return anyNewAssetFound;
        }

        private async Task<bool> _CheckCategoryAsync(AssetCategory category, bool silent, Action<string> onUpdatesFound = null)
        {
            Application.Current.Dispatcher.Invoke(() => category.Status = CategoryStatus.Checking);
            try
            {
                CategoryCheckStarted?.Invoke(category);

                var idsToCheck = new HashSet<long>(category.FailedUrls);
                idsToCheck.ExceptWith(category.UserRemovedUrls);

                int needed = 10 - idsToCheck.Count;
                if (needed > 0)
                {
                    var allKnownIds = new HashSet<long>(category.FoundUrls);
                    allKnownIds.UnionWith(category.FailedUrls);
                    allKnownIds.UnionWith(category.UserRemovedUrls);

                    long lastKnownId = 0;
                    if (allKnownIds.Any())
                    {
                        lastKnownId = allKnownIds.Max();
                    }
                    else
                    {
                        lastKnownId = category.Start > 0 ? category.Start - 1 : 0;
                    }

                    int count = 0;
                    while (count < needed)
                    {
                        lastKnownId++;
                        if (allKnownIds.Contains(lastKnownId)) continue;

                        idsToCheck.Add(lastKnownId);
                        count++;
                    }
                }

                var (anyNewAssetFound, _) = await _InternalCheckLogicAsync(idsToCheck, category, onUpdatesFound);

                CategoryCheckCompleted?.Invoke(category);
                return anyNewAssetFound;
            }
            finally
            {
                await Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    category.Status = CategoryStatus.CompletedSuccess;
                    await Task.Delay(3000);
                    category.Status = CategoryStatus.Idle;
                });
            }
        }

        private void SaveCategoryProgress(AssetCategory category)
        {
            if (category.FoundUrls.Any()) category.LastValid = category.FoundUrls.Max();
            _appSettings.AssetTrackerProgress[category.Id] = category.LastValid;
            _appSettings.AssetTrackerFailedIds[category.Id] = category.FailedUrls;
            _appSettings.AssetTrackerUserRemovedIds[category.Id] = category.UserRemovedUrls;
            _appSettings.AssetTrackerFoundIds[category.Id] = category.FoundUrls;
            _appSettings.AssetTrackerUrlOverrides[category.Id] = category.FoundUrlOverrides;
            AppSettings.SaveSettings(_appSettings);
        }

        public void RemoveAsset(AssetCategory category, TrackedAsset assetToRemove)
        {
            long? assetId = GetAssetIdFromUrl(assetToRemove.Url);
            if (!assetId.HasValue) return;

            bool changed = false;
            // Remove from found lists
            if (category.FoundUrls.Remove(assetId.Value))
            {
                changed = true;
            }
            if (category.FoundUrlOverrides.Remove(assetId.Value))
            {
                changed = true;
            }

            // Add to the permanent user-removed list
            if (!category.UserRemovedUrls.Contains(assetId.Value))
            {
                category.UserRemovedUrls.Add(assetId.Value);
                changed = true;
            }

            if (changed)
            {
                SaveCategoryProgress(category);
            }
        }

        public void RemoveAllFoundAssets(AssetCategory category)
        {
            if (category == null || !category.FoundUrls.Any()) return;

            bool changed = false;
            var foundIds = new List<long>(category.FoundUrls);

            foreach (var id in foundIds)
            {
                if (!category.UserRemovedUrls.Contains(id))
                {
                    category.UserRemovedUrls.Add(id);
                    changed = true;
                }
            }

            if (foundIds.Any())
            {
                category.FoundUrls.Clear();
                category.FoundUrlOverrides.Clear();
                changed = true;
            }

            if (changed)
            {
                SaveCategoryProgress(category);
            }
        }

        #endregion
    }
}
