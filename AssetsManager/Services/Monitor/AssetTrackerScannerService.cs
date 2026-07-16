using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Views.Models.Monitor;

namespace AssetsManager.Services.Monitor
{
    public sealed class AssetTrackerScannerService
    {
        private readonly HttpClient _httpClient;

        public AssetTrackerScannerService(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public IReadOnlyList<long> BuildCandidateIds(AssetCategory category)
        {
            if (category == null) return Array.Empty<long>();

            var candidates = new SortedSet<long>();
            int candidateLimit = Math.Max(1, category.ForwardScanWindow);
            foreach (AssetTrackerEntry entry in category.Entries.Values
                         .Where(entry => entry.State is TrackedAssetState.Missing or TrackedAssetState.TemporaryError or TrackedAssetState.RemovedCandidate)
                         .OrderBy(entry => entry.AssetId)
                         .Take(candidateLimit))
            {
                candidates.Add(entry.AssetId);
            }
            candidates.ExceptWith(category.UserRemovedUrls);

            long highestCdnProbe = category.Entries.Values
                .Where(entry => entry.WasCdnProbed)
                .Select(entry => entry.AssetId)
                .DefaultIfEmpty(category.Start - 1)
                .Max();
            long frontier = Math.Max(category.Start, highestCdnProbe + 1);
            for (int offset = 0; candidates.Count < candidateLimit; offset++)
            {
                long candidate = frontier + offset;
                if (!category.UserRemovedUrls.Contains(candidate)) candidates.Add(candidate);
            }
            return candidates.ToList();
        }

        public async Task<AssetTrackerScanResult> ScanAsync(
            AssetCategory category,
            IEnumerable<long> assetIds,
            CancellationToken cancellationToken)
        {
            if (category == null) throw new ArgumentNullException(nameof(category));

            long[] ids = assetIds?.Distinct().OrderBy(id => id).ToArray() ?? Array.Empty<long>();
            if (ids.Length == 0) return new AssetTrackerScanResult();

            var results = new ConcurrentBag<AssetProbeResult>();
            using var limiter = new SemaphoreSlim(Math.Clamp(category.MaxConcurrency, 1, 8));
            Task[] tasks = ids.Select(async id =>
            {
                await limiter.WaitAsync(cancellationToken);
                try
                {
                    results.Add(await ProbeAsync(category, id, cancellationToken));
                }
                finally
                {
                    limiter.Release();
                }
            }).ToArray();

            await Task.WhenAll(tasks);
            var scanResult = new AssetTrackerScanResult();
            foreach (AssetProbeResult result in results.OrderBy(result => result.AssetId))
            {
                AssetTrackerEntry entry = ApplyResult(category, result, DateTime.UtcNow);
                scanResult.Checked++;
                if (entry.State == TrackedAssetState.Available) scanResult.Available++;
                if (result.WasNewDiscovery) scanResult.NewDiscoveries++;
                if (entry.State == TrackedAssetState.TemporaryError) scanResult.TemporaryErrors++;
            }

            return scanResult;
        }

        private async Task<AssetProbeResult> ProbeAsync(AssetCategory category, long assetId, CancellationToken cancellationToken)
        {
            IReadOnlyList<string> extensions = category.Extensions?.Count > 0
                ? category.Extensions
                : new[] { category.Extension };
            AssetProbeResult lastResult = null;

            foreach (string extension in extensions.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string url = $"{category.BaseUrl}{assetId}.{extension.TrimStart('.')}";
                lastResult = await ProbeUrlAsync(assetId, url, extension, cancellationToken);
                if (lastResult.IsAvailable || lastResult.IsTemporaryFailure) return lastResult;
            }

            return lastResult ?? new AssetProbeResult(assetId, null, category.Extension, HttpStatusCode.NotFound, false, false);
        }

        private async Task<AssetProbeResult> ProbeUrlAsync(long assetId, string url, string extension, CancellationToken cancellationToken)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, url);
                using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                bool image = response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;
                bool available = response.IsSuccessStatusCode && image;
                bool temporary = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
                return new AssetProbeResult(assetId, url, extension, response.StatusCode, available, temporary);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException)
            {
                return new AssetProbeResult(assetId, url, extension, null, false, true);
            }
        }

        private static AssetTrackerEntry ApplyResult(AssetCategory category, AssetProbeResult result, DateTime utcNow)
        {
            bool existed = category.Entries.TryGetValue(result.AssetId, out AssetTrackerEntry entry);
            entry ??= new AssetTrackerEntry { AssetId = result.AssetId };
            bool wasAvailable = entry.State == TrackedAssetState.Available;

            entry.LastChecked = utcNow;
            entry.WasCdnProbed = true;
            entry.LastHttpStatus = result.StatusCode.HasValue ? (int)result.StatusCode.Value : null;
            if (result.IsAvailable)
            {
                entry.Url = result.Url;
                entry.Extension = result.Extension.TrimStart('.');
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
                entry.State = wasAvailable
                    ? TrackedAssetState.RemovedCandidate
                    : entry.State == TrackedAssetState.RemovedCandidate && entry.FailureCount >= 3
                        ? TrackedAssetState.Removed
                        : TrackedAssetState.Missing;
            }

            category.Entries[result.AssetId] = entry;
            return entry;
        }

    }

    public sealed class AssetTrackerScanResult
    {
        public int Checked { get; set; }
        public int Available { get; set; }
        public int NewDiscoveries { get; set; }
        public int TemporaryErrors { get; set; }
    }

    internal sealed class AssetProbeResult
    {
        public AssetProbeResult(long assetId, string url, string extension, HttpStatusCode? statusCode, bool isAvailable, bool isTemporaryFailure)
        {
            AssetId = assetId;
            Url = url;
            Extension = extension;
            StatusCode = statusCode;
            IsAvailable = isAvailable;
            IsTemporaryFailure = isTemporaryFailure;
        }

        public long AssetId { get; }
        public string Url { get; }
        public string Extension { get; }
        public HttpStatusCode? StatusCode { get; }
        public bool IsAvailable { get; }
        public bool IsTemporaryFailure { get; }
        public bool WasNewDiscovery { get; set; }
    }
}
