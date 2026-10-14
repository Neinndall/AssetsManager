using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Views.Models.Monitor;

namespace AssetsManager.Services.Monitor;

/// <summary>
/// Performs bounded CDN probes for both numeric tracker candidates and catalog URLs.
/// </summary>
public sealed class AssetTrackerScannerService
{
    private readonly HttpClient _httpClient;

    public AssetTrackerScannerService(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public int MaxAttempts { get; set; } = 3;

    public IReadOnlyList<long> BuildCandidateIds(AssetCategory category)
    {
        if (category == null) return Array.Empty<long>();

        EnsureRanges(category);
        var candidates = new SortedSet<long>();
        int window = Math.Max(1, category.ForwardScanWindow);

        foreach (var entry in category.Entries.Values
                     .Where(entry => entry.State is TrackedAssetState.Missing or TrackedAssetState.TemporaryError or TrackedAssetState.RemovedCandidate)
                     .OrderBy(entry => entry.AssetId).Take(window))
            candidates.Add(entry.AssetId);

        var ranges = category.Ranges.OrderBy(range => range.Start).ToArray();
        var cursors = ranges.Select(range => Math.Max(range.Start, range.NextId)).ToArray();
        int guard = 0;
        while (candidates.Count < window && guard++ < 1_000_000)
        {
            bool progressed = false;
            for (int index = 0; index < ranges.Length && candidates.Count < window; index++)
            {
                long limit = GetNextRangeStart(category, ranges[index]);
                while (cursors[index] < limit && cursors[index] < long.MaxValue)
                {
                    long id = cursors[index]++;
                    progressed = true;
                    if (candidates.Contains(id) || (category.Entries.TryGetValue(id, out var entry) && entry.WasCdnProbed)) continue;
                    candidates.Add(id);
                    break;
                }
            }
            if (!progressed) break;
        }

        return candidates.ToArray();
    }

    /// <summary>
    /// Advances only the ranges that supplied forward candidates. Retry candidates do not move a
    /// cursor, so a transient CDN failure remains eligible for a later pass.
    /// </summary>
    public void AdvanceRanges(AssetCategory category, IEnumerable<long> candidateIds)
    {
        if (category == null || candidateIds == null) return;
        EnsureRanges(category);

        foreach (long id in candidateIds.Distinct())
        {
            AssetTrackerRange range = category.Ranges
                .OrderByDescending(item => item.Start)
                .FirstOrDefault(item => id >= item.Start);
            if (range == null || id < range.NextId) continue;
            range.NextId = id == long.MaxValue ? long.MaxValue : id + 1;
        }
    }

    public async Task<AssetTrackerScanResult> ScanAsync(
        AssetCategory category,
        IEnumerable<long> assetIds,
        CancellationToken cancellationToken = default)
    {
        if (category == null) throw new ArgumentNullException(nameof(category));

        long[] ids = assetIds?.Distinct().OrderBy(id => id).ToArray() ?? Array.Empty<long>();
        if (ids.Length == 0) return new AssetTrackerScanResult();

        using var limiter = new SemaphoreSlim(Math.Clamp(category.MaxConcurrency, 1, 8));
        var results = new ConcurrentBag<AssetTrackerUrlProbeResult>();
        Task[] tasks = ids.Select(async id =>
        {
            await limiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                results.Add(await ProbeGeneratedUrlAsync(category, id, cancellationToken).ConfigureAwait(false));
            }
            finally
            {
                limiter.Release();
            }
        }).ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(false);

        var scanResult = new AssetTrackerScanResult();
        foreach (AssetTrackerUrlProbeResult result in results.OrderBy(item => item.AssetId))
        {
            ApplyResult(category, result, DateTime.UtcNow, preserveAvailableOnUnavailable: false);
            scanResult.Add(result);
        }

        return scanResult;
    }

    /// <summary>
    /// Probes a complete URL as supplied by the catalog. The URL is never reconstructed from a
    /// trailing numeric token, which preserves variants such as image_123_1.png.
    /// </summary>
    public Task<AssetTrackerUrlProbeResult> ProbeUrlAsync(
        string url,
        CancellationToken cancellationToken = default) => ProbeUrlCoreAsync(url, null, null, cancellationToken);

    public Task<AssetTrackerUrlProbeResult> ProbeUrlAsync(
        long assetId,
        string url,
        string extension,
        CancellationToken cancellationToken = default) => ProbeUrlCoreAsync(url, assetId, extension, cancellationToken);

    public async Task<AssetTrackerUrlScanResult> ScanUrlsAsync(
        AssetCategory category,
        IEnumerable<TrackedAsset> assets,
        CancellationToken cancellationToken = default)
    {
        if (category == null) throw new ArgumentNullException(nameof(category));

        string[] urls = assets?.Select(asset => asset?.Url)
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? Array.Empty<string>();
        if (urls.Length == 0) return new AssetTrackerUrlScanResult();

        using var limiter = new SemaphoreSlim(Math.Clamp(category.MaxConcurrency, 1, 8));
        var results = new ConcurrentBag<AssetTrackerUrlProbeResult>();
        Task[] tasks = urls.Select(async url =>
        {
            await limiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                results.Add(await ProbeUrlAsync(url, cancellationToken).ConfigureAwait(false));
            }
            finally
            {
                limiter.Release();
            }
        }).ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(false);

        var scanResult = new AssetTrackerUrlScanResult();
        foreach (AssetTrackerUrlProbeResult result in results.OrderBy(item => item.Url, StringComparer.Ordinal))
            scanResult.Add(result);
        return scanResult;
    }

    public static void ApplyResult(
        AssetCategory category,
        AssetTrackerUrlProbeResult result,
        DateTime utcNow,
        bool preserveAvailableOnUnavailable)
    {
        if (category == null || result == null || !result.AssetId.HasValue) return;

        bool existed = category.Entries.TryGetValue(result.AssetId.Value, out AssetTrackerEntry entry);
        entry ??= new AssetTrackerEntry { AssetId = result.AssetId.Value };
        bool wasAvailable = entry.State == TrackedAssetState.Available;

        entry.LastChecked = utcNow;
        entry.WasCdnProbed = true;
        entry.LastHttpStatus = result.StatusCode.HasValue ? (int)result.StatusCode.Value : null;
        if (result.IsAvailable)
        {
            entry.Url = result.Url;

            entry.Extension = result.Extension?.TrimStart('.')
                ?? PathExtension(result.Url);
            entry.State = TrackedAssetState.Available;
            entry.FirstSeen ??= utcNow;
            entry.LastSeen = utcNow;
            entry.FailureCount = 0;
            result.WasNewDiscovery = !existed || !wasAvailable;
        }
        else if (result.IsTemporaryFailure)
        {
            entry.State = TrackedAssetState.TemporaryError;
            entry.FailureCount++;
        }
        else
        {
            entry.FailureCount++;
            entry.State = preserveAvailableOnUnavailable && wasAvailable
                ? TrackedAssetState.Available
                : wasAvailable
                    ? TrackedAssetState.RemovedCandidate
                    : entry.State == TrackedAssetState.RemovedCandidate && entry.FailureCount >= 3
                        ? TrackedAssetState.Removed
                        : TrackedAssetState.Missing;
        }

        category.Entries[result.AssetId.Value] = entry;
    }

    private async Task<AssetTrackerUrlProbeResult> ProbeGeneratedUrlAsync(
        AssetCategory category,
        long assetId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> extensions = category.Extensions?.Count > 0
            ? category.Extensions
            : new[] { category.Extension };
        AssetTrackerUrlProbeResult last = null;

        foreach (string suffix in (category.FileNameSuffixes ?? new() { "" }).Where(value => value != null).Distinct(StringComparer.Ordinal))
        foreach (string extension in extensions.Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string cleanExtension = extension.TrimStart('.');
            string url = $"{category.BaseUrl}{assetId}{suffix}.{cleanExtension}";
            last = await ProbeUrlAsync(assetId, url, cleanExtension, cancellationToken).ConfigureAwait(false);
            if (last.IsAvailable || last.IsTemporaryFailure) return last;
        }

        return last ?? new AssetTrackerUrlProbeResult
        {
            AssetId = assetId,
            Extension = category.Extension,
            StatusCode = HttpStatusCode.NotFound
        };
    }

    private async Task<AssetTrackerUrlProbeResult> ProbeUrlCoreAsync(
        string url,
        long? assetId,
        string extension,
        CancellationToken cancellationToken)
    {
        var result = new AssetTrackerUrlProbeResult
        {
            AssetId = assetId ?? AssetCatalogRouting.GetImageNumber(url),
            Url = url,
            Extension = extension ?? PathExtension(url)
        };

        if (!Uri.TryCreate(url, UriKind.Absolute, out _)) return result;

        int attempts = Math.Clamp(MaxAttempts, 1, 5);
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            result.AttemptCount = attempt;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (RequestTimeout > TimeSpan.Zero && RequestTimeout != Timeout.InfiniteTimeSpan)
                    timeout.CancelAfter(RequestTimeout);

                using var request = new HttpRequestMessage(HttpMethod.Head, url);
                using HttpResponseMessage response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token).ConfigureAwait(false);

                bool hasImageContent = response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;
                result.StatusCode = response.StatusCode;
                result.IsAvailable = response.StatusCode == HttpStatusCode.OK && hasImageContent;
                result.IsTemporaryFailure = IsTemporaryStatus(response.StatusCode);
                if (!result.IsTemporaryFailure || attempt == attempts) return result;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                result.IsTemporaryFailure = true;
                result.Error = "The CDN request timed out.";
                if (attempt == attempts) return result;
            }
            catch (HttpRequestException ex)
            {
                result.IsTemporaryFailure = true;
                result.Error = ex.Message;
                if (attempt == attempts) return result;
            }
            catch (TimeoutException ex)
            {
                result.IsTemporaryFailure = true;
                result.Error = ex.Message;
                if (attempt == attempts) return result;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt), cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private static bool IsTemporaryStatus(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.TooManyRequests || (int)statusCode >= 500;

    private static string PathExtension(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri)) return null;
        string extension = System.IO.Path.GetExtension(uri.AbsolutePath);
        return string.IsNullOrWhiteSpace(extension) ? null : extension.TrimStart('.');
    }

    private static void EnsureRanges(AssetCategory category)
    {
        category.Ranges ??= new List<AssetTrackerRange>();
        if (category.Ranges.Count == 0)
            category.Ranges.Add(new AssetTrackerRange { Start = category.Start, NextId = category.Start });

        foreach (AssetTrackerRange range in category.Ranges)
        {
            if (range.NextId < range.Start) range.NextId = range.Start;
            long limit = GetNextRangeStart(category, range);
            while (range.NextId < limit && range.NextId < long.MaxValue
                   && category.Entries.TryGetValue(range.NextId, out var entry) && entry.WasCdnProbed)
                range.NextId++;

        }
    }

    private static long GetNextRangeStart(AssetCategory category, AssetTrackerRange range)
    {
        return category.Ranges.Where(item => item.Start > range.Start)
            .Select(item => item.Start)
            .DefaultIfEmpty(long.MaxValue)
            .Min();
    }

    private static bool IsInRange(long id, long start, long nextStart) =>
        id >= start && (nextStart == long.MaxValue || id < nextStart);
}

public sealed class AssetTrackerScanResult
{
    public int Checked { get; set; }
    public int Available { get; set; }
    public int NewDiscoveries { get; set; }
    public int TemporaryErrors { get; set; }
    public List<AssetTrackerUrlProbeResult> Results { get; } = new();

    internal void Add(AssetTrackerUrlProbeResult result)
    {
        Results.Add(result);
        Checked++;
        if (result.IsAvailable) Available++;
        if (result.WasNewDiscovery) NewDiscoveries++;
        if (result.IsTemporaryFailure) TemporaryErrors++;
    }
}

public sealed class AssetTrackerUrlScanResult
{
    public int Checked { get; private set; }
    public int Available { get; private set; }
    public int TemporaryErrors { get; private set; }
    public List<AssetTrackerUrlProbeResult> Results { get; } = new();

    internal void Add(AssetTrackerUrlProbeResult result)
    {
        Results.Add(result);
        Checked++;
        if (result.IsAvailable) Available++;
        if (result.IsTemporaryFailure) TemporaryErrors++;
    }
}

public sealed class AssetTrackerUrlProbeResult
{
    public long? AssetId { get; set; }
    public string Url { get; set; }
    public string Extension { get; set; }
    public HttpStatusCode? StatusCode { get; set; }
    public bool IsAvailable { get; set; }
    public bool IsTemporaryFailure { get; set; }
    public bool WasNewDiscovery { get; set; }
    public int AttemptCount { get; set; }
    public string Error { get; set; }
}
