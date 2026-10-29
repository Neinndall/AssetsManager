using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Monitor;

namespace AssetsManager.Services.Monitor;

public sealed class AssetCatalogService
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly AssetTrackerScannerService _scanner;
    private readonly IAssetCatalogSource _source;
    private readonly LogService _log;
    private readonly string _path;
    private CatalogState _state;
    public List<AssetCategory> Categories { get; }
    public string LastSyncStatus { get { lock (_sync) return _state.LastSyncStatus ?? "Saved catalog ready. Sync sources to discover more candidates."; } }
    public DateTime? LastSyncUtc { get { lock (_sync) return _state.LastSyncUtc; } }
    public event EventHandler Changed;
    public event Action<AssetCategory> CategoryCheckStarted;
    public event Action<AssetCategory> CategoryCheckCompleted;

    public AssetCatalogService(AssetTrackerScannerService scanner, IAssetCatalogSource source, LogService log, DirectoriesCreator directories)
    {
        _scanner = scanner;
        _source = source;
        _log = log;
        _path = Path.Combine(directories.ApiCachePath, "asset_tracker", "catalog.json");
        _state = Load();
        Categories = DefaultCategories.Get();
        lock (_sync)
        {
            foreach (var category in Categories)
            {
                var saved = GetCategoryState(category.Id);
                if (saved.Start > 0) category.Start = saved.Start;
                if (saved.Ranges.Count > 0) category.Ranges = Clone(saved.Ranges);
                if (category.Ranges.Count == 0) category.Ranges.Add(new() { Start = category.Start, NextId = category.Start });
                if (category.Id == "9" && !category.Ranges.Any(range => range.Start == 60007))
                    category.Ranges.Add(new() { Start = 60007, NextId = 60007 });
                category.UserRemovedAssetUrls = saved.ExcludedUrls;
                saved.Start = category.Start;
                saved.Ranges = category.Ranges;
            }
            ProjectImages();
            SaveLocked();
        }
    }

    public List<TrackedAsset> GetAssetListForCategory(AssetCategory category)
    {
        if (category == null) return new();
        lock (_sync)
        {
            AlignPrimaryRange(category);
            var rows = _state.Images.Values.Where(image => image.CategoryIds.Contains(category.Id)
                    && image.State == TrackedAssetState.Available && !Excluded(category.Id, image))
                .Select(image => Row(image, category.Id)).ToList();
            rows.AddRange(GetScanCandidatesForCategory(category));
            return rows.OrderBy(row => row.AssetId).ThenBy(row => row.Url, StringComparer.Ordinal).ToList();
        }
    }

    public List<TrackedAsset> GetScanCandidatesForCategory(AssetCategory category)
    {
        if (category == null) return new();
        lock (_sync)
        {
            AlignPrimaryRange(category);
            int limit = Math.Max(1, category.ForwardScanWindow);
            var result = new List<TrackedAsset>();
            var urls = new HashSet<string>(StringComparer.Ordinal);
            void Add(TrackedAsset row)
            {
                if (result.Count < limit && row.Url != null && !row.CanDownload
                    && !category.UserRemovedAssetUrls.Contains(row.Url) && urls.Add(row.Url)) result.Add(row);
            }
            foreach (var entry in category.Entries.Values.Where(entry => entry.State is TrackedAssetState.Missing
                         or TrackedAssetState.TemporaryError or TrackedAssetState.RemovedCandidate).OrderBy(entry => entry.AssetId))
            {
                Add(NumericRow(category, entry.AssetId));
                if (result.Count == limit) break;
            }
            if (result.Count < limit)
            foreach (var image in _state.Images.Values.Where(image => image.CategoryIds.Contains(category.Id) && !Excluded(category.Id, image)
                         && image.State != TrackedAssetState.Available && image.ArticleKeys.Any(key => _state.Articles.TryGetValue(key, out var article)
                             && (article.ImageUrl ?? article.CandidateUrl) == image.Url)).OrderBy(image => image.AssetId))
            {
                Add(Row(image, category.Id));
                if (result.Count == limit) break;
            }
            if (result.Count < limit)
            foreach (long id in _scanner.BuildCandidateIds(category))
                if (!category.UserRemovedAssetUrls.Contains($"{category.BaseUrl}{id}.{category.Extension}")) Add(NumericRow(category, id));
            return result.OrderBy(row => row.AssetId).ThenBy(row => row.Url, StringComparer.Ordinal).ToList();
        }
    }

    public void RefreshCategories()
    {
        lock (_sync)
        {
            foreach (var category in Categories) AlignPrimaryRange(category);
            ProjectImages();
        }
    }

    public List<TrackedAsset> GetCatalogAssets(string categoryId = null)
    {
        lock (_sync)
        {
            var result = new List<TrackedAsset>();
            var represented = new HashSet<string>(StringComparer.Ordinal);
            foreach (var article in _state.Articles.Values.Where(article => categoryId == null || article.CategoryId == categoryId))
            {
                string url = article.ImageUrl ?? article.CandidateUrl;
                _state.Images.TryGetValue(url ?? "", out var image);
                if (image != null && Excluded(article.CategoryId, image)) continue;
                if (image != null) represented.Add(image.Url);
                result.Add(Row(image, article.CategoryId, article));
            }
            foreach (var image in _state.Images.Values.Where(image => !represented.Contains(image.Url)))
            {
                string category = categoryId ?? image.CategoryIds.FirstOrDefault();
                if (category == null || !image.CategoryIds.Contains(category) || Excluded(category, image)) continue;
                result.Add(Row(image, category));
            }
            return result.OrderByDescending(row => row.ItemId ?? row.AssetId).ThenBy(row => row.Environment).ToList();
        }
    }

    public List<TrackedAsset> GenerateMoreAssets(IEnumerable<TrackedAsset> current, AssetCategory category, int amount)
    {
        if (category == null || amount <= 0) return new();
        lock (_sync)
        {
            AlignPrimaryRange(category);
            var existing = new HashSet<string>((current ?? Array.Empty<TrackedAsset>()).Select(row => row.Url), StringComparer.Ordinal);
            var result = new List<TrackedAsset>();
            var cursors = category.Ranges.Select(range => Math.Max(range.Start, range.NextId)).ToArray();
            int attempts = 0;
            while (result.Count < amount && attempts++ < amount * 100 + 100)
                for (int index = 0; index < cursors.Length && result.Count < amount; index++)
                {
                    if (cursors[index] == long.MaxValue) continue;
                    long id = cursors[index]++;
                    long upper = category.Ranges.Where(range => range.Start > category.Ranges[index].Start).Select(range => range.Start).DefaultIfEmpty(long.MaxValue).Min();
                    if (id >= upper) continue;
                    string url = $"{category.BaseUrl}{id}.{category.Extension}";
                    if (category.UserRemovedAssetUrls.Contains(url) || !existing.Add(url) || _state.Images.ContainsKey(url)) continue;
                    result.Add(Candidate(category, id, url));
                }
            return result;
        }
    }

    public async Task<AssetCatalogImportBatch> SyncSourcesAsync(CancellationToken token, string categoryId = null)
    {
        await _operations.WaitAsync(token);
        try
        {
            var query = new AssetCatalogQuery { CategoryId = string.IsNullOrEmpty(categoryId) ? null : categoryId };
            lock (_sync)
            {
                if (query.CategoryId != null && !Categories.Any(category => category.Id == query.CategoryId))
                    throw new ArgumentException("Unknown catalog category.", nameof(categoryId));
                var keys = new HashSet<(string Type, long Id)>();
                foreach (var article in _state.Articles.Values.Where(article => query.CategoryId == null || article.CategoryId == query.CategoryId))
                    keys.Add((article.InventoryType, article.ItemId));
                foreach (var category in Categories.Where(category => query.CategoryId == null || category.Id == query.CategoryId))
                {
                    string type = AssetCatalogRouting.GetInventoryType(category.Id);
                    foreach (var image in _state.Images.Values.Where(image => image.CategoryIds.Contains(category.Id)))
                        keys.Add((type, image.AssetId));
                    foreach (var range in category.Ranges)
                        foreach (long start in new[] { range.Start, range.NextId })
                            for (int i = 0; i < category.ForwardScanWindow && start <= long.MaxValue - i; i++)
                                keys.Add((type, start + i));
                    if (category.Id == "9")
                        for (int i = 1; i <= 10; i++) keys.Add((type, i));
                }
                query.KnownItems.AddRange(keys.Where(key => key.Id > 0));
            }
            var batch = await _source.ReadAsync(token, query);
            token.ThrowIfCancellationRequested();
            lock (_sync)
            {
                foreach (var incoming in batch.Articles.Where(article => query.CategoryId == null || article.CategoryId == query.CategoryId)) Import(incoming);
                _state.LastSyncStatus = batch.Status;
                _state.LastSyncUtc = DateTime.UtcNow;
                ProjectImages();
                SaveLocked();
            }
            Changed?.Invoke(this, EventArgs.Empty);
            return batch;
        }
        finally { _operations.Release(); }
    }

    public async Task<AssetTrackerScanResult> ScanAsync(AssetCategory category, IEnumerable<TrackedAsset> assets, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(category);
        await _operations.WaitAsync(token);
        try
        {
            category.Status = CategoryStatus.Checking;
            CategoryCheckStarted?.Invoke(category);
            AssetCategory working;
            List<TrackedAsset> targets;
            lock (_sync)
            {
                AlignPrimaryRange(category);
                working = Clone(category);
                targets = (assets ?? GetAssetListForCategory(category)).Where(row => row.Url != null)
                    .Where(row => !category.UserRemovedAssetUrls.Contains(row.Url))
                    .GroupBy(row => row.Url, StringComparer.Ordinal).Select(group => group.First()).ToList();
            }
            var numeric = targets.Where(row => row.IsGenerated && row.AssetId > 0).Select(row => row.AssetId).Distinct().ToArray();
            var forward = targets.Where(row => row.IsGenerated && row.ItemId == null && row.AssetId > 0).Select(row => row.AssetId).Distinct().ToArray();
            var explicitUrls = targets.Where(row => !row.IsGenerated).ToArray();
            var numericResults = await _scanner.ScanAsync(working, numeric, token);
            var urlResults = await _scanner.ScanUrlsAsync(working, explicitUrls, token);
            token.ThrowIfCancellationRequested();
            var result = new AssetTrackerScanResult();
            lock (_sync)
            {
                foreach (var probe in numericResults.Results.Concat(urlResults.Results))
                {
                    if (probe.Url == null) continue;
                    if (!_state.Images.TryGetValue(probe.Url, out var image))
                        _state.Images[probe.Url] = image = new() { Url = probe.Url, AssetId = probe.AssetId ?? 0 };
                    AddUnique(image.CategoryIds, category.Id);
                    // An inferred numeric candidate may resolve to a different extension.
                    // Preserve its article association and keep the resolved URL for subsequent reads.
                    foreach (var target in targets.Where(row => row.IsGenerated && row.AssetId == image.AssetId))
                    {
                        if (!_state.Images.TryGetValue(target.Url, out var candidate)) continue;
                        foreach (string key in candidate.ArticleKeys.ToArray())
                        {
                            AddUnique(image.ArticleKeys, key);
                            if (probe.IsAvailable && _state.Articles.TryGetValue(key, out var article) && article.ImageUrl == null)
                                article.CandidateUrl = probe.Url;
                        }
                    }
                    Apply(image, probe);
                    if (Excluded(category.Id, image)) probe.WasNewDiscovery = false;
                    result.Add(probe);
                }
                _scanner.AdvanceRanges(category, forward);
                category.HasNewAssets |= result.NewDiscoveries > 0;
                ProjectImages();
                SaveLocked();
            }
            Changed?.Invoke(this, EventArgs.Empty);
            return result;
        }
        finally
        {
            category.Status = CategoryStatus.Idle;
            _operations.Release();
            CategoryCheckCompleted?.Invoke(category);
        }
    }

    public async Task<bool> ScanAllAsync(Action<string> onUpdatesFound, CancellationToken token)
    {
        bool discovered = false;
        foreach (var category in Categories)
        {
            List<TrackedAsset> targets;
            lock (_sync)
            {
                targets = GetScanCandidatesForCategory(category);
            }
            var result = await ScanAsync(category, targets, token);
            if (result.NewDiscoveries == 0) continue;
            discovered = true;
            onUpdatesFound?.Invoke(category.Name);
        }
        return discovered;
    }

    public void RemoveAsset(AssetCategory category, TrackedAsset asset)
    {
        if (category == null || string.IsNullOrWhiteSpace(asset?.Url)) return;
        lock (_sync)
        {
            AddUnique(category.UserRemovedAssetUrls, asset.Url);
            ProjectImages();
            SaveLocked();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveAllFoundAssets(AssetCategory category)
    {
        if (category == null) return;
        lock (_sync)
        {
            foreach (var image in _state.Images.Values.Where(image => image.CategoryIds.Contains(category.Id) && image.State == TrackedAssetState.Available))
                AddUnique(category.UserRemovedAssetUrls, image.Url);
            ProjectImages();
            SaveLocked();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Import(AssetCatalogArticle incoming)
    {
        if (incoming.CategoryId == null) return;
        if (!_state.Articles.TryGetValue(incoming.Key, out var article)) _state.Articles[incoming.Key] = article = Clone(incoming);
        else
        {
            if (!string.IsNullOrWhiteSpace(incoming.Name) && (incoming.Source == "LCU" || article.Name == null)) article.Name = incoming.Name;
            article.ImageUrl = incoming.ImageUrl ?? article.ImageUrl;
            article.CandidateUrl = incoming.CandidateUrl ?? article.CandidateUrl;
            article.Active = incoming.Active ?? article.Active;
            article.SubInventoryType = incoming.SubInventoryType ?? article.SubInventoryType;
            article.FirstSeenApi ??= incoming.FirstSeenApi;
            article.LastSeenApi = incoming.LastSeenApi ?? article.LastSeenApi;
            if (incoming.Source == "LCU") article.Source = "LCU";
        }
        string url = article.ImageUrl ?? article.CandidateUrl;
        if (url == null) return;
        if (!_state.Images.TryGetValue(url, out var image))
            _state.Images[url] = image = new() { Url = url, AssetId = AssetCatalogRouting.GetImageNumber(url) ?? 0 };
        AddUnique(image.ArticleKeys, article.Key);
        AddUnique(image.CategoryIds, article.CategoryId);
        string imageCategory = AssetCatalogRouting.GetImageCategory(url);
        if (imageCategory != null) AddUnique(image.CategoryIds, imageCategory);
        var category = Categories.FirstOrDefault(item => item.Id == imageCategory);
        if (category == null || image.AssetId < category.Start) return;
        var range = category.Ranges.Where(range => range.Start <= image.AssetId && image.AssetId - range.Start <= 10000)
            .OrderByDescending(range => range.Start).FirstOrDefault();
        if (range == null)
        {
            range = new() { Start = image.AssetId, NextId = image.AssetId };
            category.Ranges.Add(range);
        }
    }

    private static void Apply(AssetCatalogImage image, AssetTrackerUrlProbeResult probe)
    {
        bool known = image.FirstSeen.HasValue;
        image.LastChecked = DateTime.UtcNow;
        image.WasCdnProbed = true;
        image.LastHttpStatus = probe.StatusCode.HasValue ? (int)probe.StatusCode.Value : null;
        if (probe.IsAvailable)
        {
            image.State = TrackedAssetState.Available;
            image.FirstSeen ??= image.LastChecked;
            image.LastSeen = image.LastChecked;
            image.Extension = probe.Extension;
            image.FailureCount = 0;
            probe.WasNewDiscovery = !known;
        }
        else if (probe.IsTemporaryFailure) image.State = TrackedAssetState.TemporaryError;
        else image.State = known ? TrackedAssetState.RemovedCandidate : TrackedAssetState.Missing;
        if (!probe.IsAvailable) image.FailureCount++;
    }

    private TrackedAsset Row(AssetCatalogImage image, string categoryId, AssetCatalogArticle article = null)
    {
        article ??= image?.ArticleKeys.Select(key => _state.Articles.GetValueOrDefault(key))
            .Where(item => item != null).OrderByDescending(item => item.LastSeenApi).FirstOrDefault();
        string url = image?.Url ?? article?.ImageUrl ?? article?.CandidateUrl;
        bool publishedUrl = image != null && image.ArticleKeys.Any(key => _state.Articles.TryGetValue(key, out var linked) && linked.ImageUrl == image.Url);
        string source = article?.FirstSeenApi != null ? "LCU" : "CDN";
        if (image?.FirstSeen != null && source != "CDN") source += " + CDN";
        long id = image?.AssetId ?? AssetCatalogRouting.GetImageNumber(url) ?? article?.ItemId ?? 0;
        var row = new TrackedAsset
        {
            Url = url, AssetId = id, ItemId = article?.ItemId, InventoryType = article?.InventoryType,
            CategoryId = categoryId, CategoryName = Categories.FirstOrDefault(category => category.Id == categoryId)?.Name ?? categoryId,
            DisplayName = article?.Name ?? (image?.State == TrackedAssetState.Available && url != null
                ? Path.GetFileName(new Uri(url).AbsolutePath) : id.ToString()),
            Source = source, Environment = article?.Environment ?? "CDN", State = image?.State ?? TrackedAssetState.Pending,
            Thumbnail = image?.State == TrackedAssetState.Available ? image.Url : null,
            FirstSeenCdn = image?.FirstSeen, FirstSeenApi = article?.FirstSeenApi, LastChecked = image?.LastChecked,
            IsGenerated = image?.FirstSeen == null && !publishedUrl,
            IdentityText = article == null ? $"Image {id}" : $"{article.InventoryType} {article.ItemId} · Image {id}",
            TimingText = $"CDN: {FormatDate(image?.FirstSeen)} · API: {FormatDate(article?.FirstSeenApi)}"
        };
        if (image == null && url == null) row.Status = "Image URL unavailable";
        return row;
    }

    private static TrackedAsset Candidate(AssetCategory category, long id, string url) => new()
    {
        Url = url, AssetId = id, DisplayName = id.ToString(), CategoryId = category.Id, CategoryName = category.Name,
        Source = "CDN", Environment = "CDN", IsGenerated = true, IdentityText = $"Image {id}", TimingText = "Not checked"
    };

    private TrackedAsset NumericRow(AssetCategory category, long id)
    {
        string url = $"{category.BaseUrl}{id}.{category.Extension}";
        if (_state.Images.TryGetValue(url, out var image)) return Row(image, category.Id);
        if (category.Entries.TryGetValue(id, out var entry) && entry.Url != null && _state.Images.TryGetValue(entry.Url, out image))
            return Row(image, category.Id);
        return Candidate(category, id, url);
    }

    private bool Excluded(string categoryId, AssetCatalogImage image)
    {
        var saved = GetCategoryState(categoryId);
        return saved.ExcludedUrls.Contains(image.Url);
    }

    private void ProjectImages()
    {
        foreach (var category in Categories)
        {
            foreach (var group in _state.Images.Values.Where(image => image.CategoryIds.Contains(category.Id) && image.AssetId > 0 && !Excluded(category.Id, image)).GroupBy(image => image.AssetId))
                category.Entries[group.Key] = group.OrderByDescending(image => image.State == TrackedAssetState.Available).ThenByDescending(image => image.LastChecked).First();
            foreach (long key in category.Entries.Where(pair => category.UserRemovedAssetUrls.Contains(pair.Value.Url)).Select(pair => pair.Key).ToArray())
                category.Entries.Remove(key);
        }
    }

    private void AlignPrimaryRange(AssetCategory category)
    {
        var saved = GetCategoryState(category.Id);
        if (saved.Start == category.Start) return;
        var primary = category.Ranges.FirstOrDefault(range => range.Start == saved.Start) ?? category.Ranges.FirstOrDefault();
        if (primary != null) { primary.Start = category.Start; primary.NextId = category.Start; }
        saved.Start = category.Start;
    }

    private CategoryState GetCategoryState(string id)
    {
        if (!_state.Categories.TryGetValue(id, out var state)) _state.Categories[id] = state = new();
        return state;
    }

    private CatalogState Load()
    {
        if (!File.Exists(_path)) return new();
        try
        {
            var state = JsonSerializer.Deserialize<CatalogState>(File.ReadAllText(_path)) ?? throw new InvalidDataException("Empty asset catalog cache.");
            if (state.Version != 1) throw new InvalidDataException("Unsupported asset catalog cache version.");
            state.Images ??= new(StringComparer.Ordinal);
            state.Articles ??= new(StringComparer.Ordinal);
            state.Categories ??= new(StringComparer.Ordinal);
            foreach (var image in state.Images.Values)
            {
                if (image == null || !Uri.TryCreate(image.Url, UriKind.Absolute, out _))
                    throw new InvalidDataException("Invalid image in the asset catalog cache.");
                image.CategoryIds ??= new();
                image.ArticleKeys ??= new();
            }
            foreach (var category in state.Categories.Values)
            {
                if (category == null) throw new InvalidDataException("Invalid category in the asset catalog cache.");
                category.Ranges ??= new();
                category.ExcludedUrls ??= new();
                if (category.Ranges.Any(range => range == null))
                    throw new InvalidDataException("Invalid range in the asset catalog cache.");
            }
            if (state.Articles.Values.Any(article => article == null))
                throw new InvalidDataException("Invalid article in the asset catalog cache.");
            return state;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not read the asset catalog cache; the original file is being preserved for recovery.");
            File.Copy(_path, _path + ".recovery-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"), false);
            return new();
        }
    }

    private void SaveLocked()
    {
        foreach (var category in Categories)
        {
            var saved = GetCategoryState(category.Id);
            saved.Start = category.Start;
            saved.Ranges = category.Ranges;
            saved.ExcludedUrls = category.UserRemovedAssetUrls;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(_path));
        string temporary = _path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(_state));
            File.Move(temporary, _path, true);
        }
        catch (Exception ex) { _log.LogError(ex, "Could not save the asset catalog cache."); throw; }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static T Clone<T>(T value) => value == null ? default : JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value));
    private static void AddUnique(List<string> list, string value) { if (value != null && !list.Contains(value)) list.Add(value); }
    private static string FormatDate(DateTime? date) => date?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—";

    private sealed class CatalogState
    {
        public CatalogState() { }
        public int Version { get; set; } = 1;
        public Dictionary<string, AssetCatalogImage> Images { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, AssetCatalogArticle> Articles { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, CategoryState> Categories { get; set; } = new(StringComparer.Ordinal);
        public string LastSyncStatus { get; set; }
        public DateTime? LastSyncUtc { get; set; }
    }
    private sealed class CategoryState
    {
        public CategoryState() { }
        public long Start { get; set; }
        public List<AssetTrackerRange> Ranges { get; set; } = new();
        public List<string> ExcludedUrls { get; set; } = new();
    }
}
