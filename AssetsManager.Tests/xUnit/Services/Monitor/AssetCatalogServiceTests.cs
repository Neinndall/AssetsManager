using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Monitor;
using AssetsManager.Tests.xUnit.Infrastructure;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Monitor;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Monitor;

public sealed class AssetCatalogServiceTests
{
    private const string Root = "https://d392eissrffsyf.cloudfront.net/storeImages/";

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task MissingNumericBundlesDisplayNotFoundAfterFallbackAndRetryAcrossReload(HttpStatusCode status)
    {
        using var bridge = new AssetsManagerTestBridge();
        bool published = false;
        var requested = new List<string>();
        using var http = Client(request =>
        {
            lock (requested) requested.Add(request.RequestUri.AbsolutePath);
            return Task.FromResult(Response(published ? HttpStatusCode.OK : status));
        });
        var service = Service(bridge, http);
        var category = service.Categories.Single(item => item.Id == "2");
        category.ForwardScanWindow = 3;
        var targets = service.GetAssetListForCategory(category);
        await service.ScanAsync(category, targets, default);
        var rows = service.GetAssetListForCategory(category);
        Assert.Equal(targets.Select(row => row.AssetId), rows.Select(row => row.AssetId));
        Assert.All(rows, row => Assert.Equal("Not Found", row.Status));
        var missing = rows.First();
        Assert.Equal("Not Found", missing.Status);
        Assert.Equal(99901652, missing.AssetId);
        Assert.DoesNotContain(rows, row => row.State == TrackedAssetState.Pending);
        Assert.Equal("99901652", missing.DisplayName);
        var reloaded = Service(bridge, http);
        category = reloaded.Categories.Single(item => item.Id == "2");
        rows = reloaded.GetAssetListForCategory(category);
        Assert.Contains(rows, row => row.AssetId == missing.AssetId && row.Status == "Not Found");
        requested.Clear();
        published = true;
        await reloaded.ScanAsync(category, rows.Where(row => row.AssetId == missing.AssetId), default);
        Assert.Contains(requested, path => path.EndsWith("99901652.png", StringComparison.Ordinal));
        Assert.Contains(reloaded.GetAssetListForCategory(category), row => row.AssetId == missing.AssetId && row.CanDownload);
    }

    [Fact]
    public async Task ExactUrlsRetainSuffixesAndIndependentExclusionsAcrossReload()
    {
        using var bridge = new AssetsManagerTestBridge();
        using var http = Client();
        var source = new Source(Article("2", "BUNDLES", 99901659, "bundles/99901659_1.png"),
            Article("2", "BUNDLES", 99901660, "bundles/99901659.png"));
        var service = Service(bridge, http, source: source);
        await service.SyncSourcesAsync(default);
        var category = service.Categories.Single(c => c.Id == "2");
        await service.ScanAsync(category, service.GetCatalogAssets("2"), default);
        var rows = service.GetCatalogAssets("2");
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => { Assert.Equal(99901659, row.AssetId); Assert.True(row.CanDownload); });
        service.RemoveAsset(category, rows.Single(row => row.Url.EndsWith("_1.png")));
        var reloaded = Service(bridge, http);
        Assert.EndsWith("99901659.png", Assert.Single(reloaded.GetCatalogAssets("2")).Url);
    }

    [Fact]
    public async Task MultipleArticlesCanShareOneVerifiedImageWithoutLosingArticleIds()
    {
        using var bridge = new AssetsManagerTestBridge();
        using var http = Client();
        var source = new Source(Article("7", "HEXTECH_CRAFTING", 817, "hextechCrafting/816.png"),
            Article("4", "BUNDLES", 69901087, "hextechCrafting/816.png"));
        var service = Service(bridge, http, source: source);
        await service.SyncSourcesAsync(default);
        var result = await service.ScanAsync(service.Categories.Single(c => c.Id == "7"), service.GetCatalogAssets(), default);
        Assert.Equal(1, result.Checked);
        Assert.Equal(1, result.NewDiscoveries);
        var rows = service.GetCatalogAssets();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, row => row.ItemId == 817 && row.AssetId == 816);
        Assert.Contains(rows, row => row.ItemId == 69901087 && row.AssetId == 816);
        Assert.All(rows, row => Assert.True(row.CanDownload));
    }

    [Fact]
    public async Task CatalogPresenceDoesNotMarkCdnAvailableAnd403RetainsHistory()
    {
        using var bridge = new AssetsManagerTestBridge();
        HttpStatusCode status = HttpStatusCode.Forbidden;
        using var http = Client(_ => Task.FromResult(Response(status)));
        var service = Service(bridge, http, source: new Source(Article("9", "SKIN_AUGMENT", 60008, "skinaugment/60008.png")));
        await service.SyncSourcesAsync(default);
        var row = Assert.Single(service.GetCatalogAssets());
        Assert.NotNull(row.FirstSeenApi);
        Assert.Null(row.FirstSeenCdn);
        Assert.False(row.CanDownload);
        var category = service.Categories.Single(c => c.Id == "9");
        await service.ScanAsync(category, new[] { row }, default);
        Assert.Equal(TrackedAssetState.Missing, Assert.Single(service.GetCatalogAssets()).State);
        status = HttpStatusCode.OK;
        Assert.Equal(1, (await service.ScanAsync(category, service.GetCatalogAssets(), default)).NewDiscoveries);
        var firstSeen = Assert.Single(service.GetCatalogAssets()).FirstSeenCdn;
        status = HttpStatusCode.Forbidden;
        await service.ScanAsync(category, service.GetCatalogAssets(), default);
        row = Assert.Single(service.GetCatalogAssets());
        Assert.Equal(TrackedAssetState.RemovedCandidate, row.State);
        Assert.Equal("Not Found", row.Status);
        Assert.Equal(firstSeen, row.FirstSeenCdn);
        status = HttpStatusCode.OK;
        Assert.Equal(0, (await service.ScanAsync(category, service.GetCatalogAssets(), default)).NewDiscoveries);
    }

    [Fact]
    public async Task CancelledScanDoesNotCommitProbesOrMoveRangesAndNextScanWorks()
    {
        using var bridge = new AssetsManagerTestBridge();
        using var cancel = new CancellationTokenSource();
        using var http = Client(_ => { cancel.Cancel(); return Task.FromResult(Response(HttpStatusCode.OK)); });
        var service = Service(bridge, http);
        var category = service.Categories.Single(c => c.Id == "9");
        var targets = service.GetAssetListForCategory(category);
        var cursors = category.Ranges.Select(range => range.NextId).ToArray();
        string before = File.ReadAllText(CachePath(bridge));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ScanAsync(category, targets, cancel.Token));
        Assert.Equal(cursors, category.Ranges.Select(range => range.NextId));
        Assert.Equal(before, File.ReadAllText(CachePath(bridge)));
        Assert.Empty(service.GetCatalogAssets());
        Assert.Equal(CategoryStatus.Idle, category.Status);
        var result = await service.ScanAsync(category, targets.Take(1), default);
        Assert.Equal(1, result.Available);
    }

    [Fact]
    public async Task FailedRangesKeepTheirCardsAndOnlyExpandWhenMoreCandidatesAreLoaded()
    {
        using var bridge = new AssetsManagerTestBridge();
        using var http = Client(_ => Task.FromResult(Response(HttpStatusCode.Forbidden)));
        var service = Service(bridge, http);
        var category = service.Categories.Single(c => c.Id == "9");
        category.ForwardScanWindow = 3;
        var first = service.GetAssetListForCategory(category);
        Assert.Equal(new long[] { 5, 6, 60007 }, first.Select(row => row.AssetId));
        await service.ScanAsync(category, first, default);
        var next = service.GetAssetListForCategory(category).Select(row => row.AssetId).ToArray();
        Assert.Equal(first.Select(row => row.AssetId), next);
        var more = service.GenerateMoreAssets(service.GetAssetListForCategory(category), category, 4);
        Assert.Equal(new long[] { 7, 60008, 8, 60009 }, more.Select(row => row.AssetId));
        await service.ScanAsync(category, more, default);
        Assert.Equal(new long[] { 5, 6, 7 }, service.GetScanCandidatesForCategory(category).Select(row => row.AssetId));
        Assert.Equal(7, service.GetCatalogAssets("9").Count);
        var reloaded = Service(bridge, http);
        Assert.Equal(new long[] { 9, 60010 }, reloaded.Categories.Single(c => c.Id == "9").Ranges.Select(range => range.NextId));
    }

    [Fact]
    public async Task EightFoundAndTwoMissingKeepFoundCardsAndFillOnlyEightNewSlots()
    {
        using var bridge = new AssetsManagerTestBridge();
        using var http = Client(request =>
        {
            long id = AssetCatalogRouting.GetImageNumber(request.RequestUri.AbsoluteUri).Value;
            return Task.FromResult(Response(id is 79900918 or 79900919 ? HttpStatusCode.NotFound : HttpStatusCode.OK));
        });
        var source = new Source();
        var service = Service(bridge, http, source);
        var category = service.Categories.Single(item => item.Id == "1");
        var first = service.GetScanCandidatesForCategory(category);
        Assert.Equal(Enumerable.Range(0, 10).Select(index => 79900910L + index), first.Select(row => row.AssetId));
        await service.ScanAsync(category, first, default);
        for (int pass = 0; pass < 3; pass++)
        {
            var rows = service.GetAssetListForCategory(category);
            var pending = rows.Where(row => !row.CanDownload).ToList();
            Assert.Equal(10, pending.Count);
            Assert.Equal(2, pending.Count(row => row.Status == "Not Found"));
            Assert.Equal(8, pending.Count(row => row.Status == "Pending"));
            Assert.Equal(new long[] { 79900918, 79900919 }, pending.Where(row => row.Status == "Not Found").Select(row => row.AssetId));
            Assert.All(pending.Where(row => row.Status == "Not Found"), row => Assert.Equal(row.AssetId.ToString(), row.DisplayName));
            Assert.Equal(8 + 8 * pass, rows.Count(row => row.CanDownload));
            await service.ScanAsync(category, pending, default);
        }
        Assert.Equal(0, source.ReadCalls);
        var reloaded = Service(bridge, http);
        Assert.Equal(32, reloaded.GetAssetListForCategory(reloaded.Categories.Single(item => item.Id == "1")).Count(row => row.CanDownload));
    }

    [Fact]
    public async Task FullFailedBatchStaysStableAndEveryIntermediateIdRemainsInCatalog()
    {
        using var bridge = new AssetsManagerTestBridge();
        using var http = Client(_ => Task.FromResult(Response(HttpStatusCode.NotFound)));
        var service = Service(bridge, http);
        var category = service.Categories.Single(item => item.Id == "1");
        var first = service.GetScanCandidatesForCategory(category);
        await service.ScanAsync(category, first, default);
        for (int pass = 0; pass < 3; pass++)
        {
            Assert.Equal(first.Select(row => row.AssetId), service.GetScanCandidatesForCategory(category).Select(row => row.AssetId));
            await service.ScanAsync(category, service.GetScanCandidatesForCategory(category), default);
        }
        var more = service.GenerateMoreAssets(service.GetAssetListForCategory(category), category, 10);
        await service.ScanAsync(category, more, default);
        Assert.Equal(20, service.GetCatalogAssets("1").Count);
        Assert.Equal(Enumerable.Range(0, 20).Select(index => 79900910L + index), service.GetCatalogAssets("1").OrderBy(row => row.AssetId).Select(row => row.AssetId));
        Assert.Equal(first.Select(row => row.AssetId), service.GetScanCandidatesForCategory(category).Select(row => row.AssetId));
        Assert.Equal(10, service.GetAssetListForCategory(category).Count);
        Assert.Equal(79900930, category.Ranges.Single().NextId);
    }

    [Fact]
    public async Task TwoFailuresPlusEightNewFailuresKeepExactlyNineteenThroughTwentyEight()
    {
        using var bridge = new AssetsManagerTestBridge();
        using var http = Client(request => Task.FromResult(Response(AssetCatalogRouting.GetImageNumber(request.RequestUri.AbsoluteUri) < 19
            ? HttpStatusCode.OK : HttpStatusCode.NotFound)));
        var service = Service(bridge, http);
        var category = service.Categories.Single(item => item.Id == "1");
        category.Start = 1;
        await service.ScanAsync(category, service.GetScanCandidatesForCategory(category), default);
        Assert.Equal(Enumerable.Range(11, 10).Select(id => (long)id), service.GetScanCandidatesForCategory(category).Select(row => row.AssetId));
        await service.ScanAsync(category, service.GetScanCandidatesForCategory(category), default);
        var mixed = service.GetScanCandidatesForCategory(category);
        Assert.Equal(new long[] { 19, 20 }, mixed.Where(row => row.Status == "Not Found").Select(row => row.AssetId));
        Assert.Equal(Enumerable.Range(21, 8).Select(id => (long)id), mixed.Where(row => row.Status == "Pending").Select(row => row.AssetId));
        await service.ScanAsync(category, mixed, default);
        for (int pass = 0; pass < 3; pass++)
        {
            var same = service.GetScanCandidatesForCategory(category);
            Assert.Equal(Enumerable.Range(19, 10).Select(id => (long)id), same.Select(row => row.AssetId));
            Assert.All(same, row => Assert.Equal("Not Found", row.Status));
            await service.ScanAsync(category, same, default);
        }
        Assert.Equal(18, service.GetAssetListForCategory(category).Count(row => row.CanDownload));
        Assert.Equal(29, category.Ranges.Single().NextId);
    }

    [Fact]
    public async Task SyncKeepsDistantArticleQueuedWhenAllTenSlotsAreFailed()
    {
        using var bridge = new AssetsManagerTestBridge();
        using var http = Client(request => Task.FromResult(Response(AssetCatalogRouting.GetImageNumber(request.RequestUri.AbsoluteUri) == 300
            ? HttpStatusCode.OK : HttpStatusCode.NotFound)));
        var source = new Source(Article("1", "BUNDLES", 300, "bundles/300.png"));
        var service = Service(bridge, http, source);
        var category = service.Categories.Single(item => item.Id == "1");
        category.Start = 1;
        await service.ScanAsync(category, service.GetScanCandidatesForCategory(category), default);
        await service.SyncSourcesAsync(default);
        var pending = service.GetScanCandidatesForCategory(category);
        Assert.Equal(Enumerable.Range(1, 10).Select(id => (long)id), pending.Select(row => row.AssetId));
        var imported = service.GetCatalogAssets("1").Single(row => row.ItemId == 300);
        Assert.False(imported.CanDownload);
        await service.ScanAsync(category, new[] { imported }, default);
        Assert.Equal(11, category.Ranges.Single().NextId);
        Assert.Equal(10, service.GetScanCandidatesForCategory(category).Count);
        Assert.True(service.GetAssetListForCategory(category).Single(row => row.AssetId == 300).CanDownload);
    }

    [Fact]
    public async Task SyncedDistantArticleUsesOneSlotWithoutSkippingNumericSequenceOrReadingApiOnScan()
    {
        using var bridge = new AssetsManagerTestBridge();
        using var http = Client(request => Task.FromResult(Response(AssetCatalogRouting.GetImageNumber(request.RequestUri.AbsoluteUri) is 4 or 5
            ? HttpStatusCode.NotFound : HttpStatusCode.OK)));
        var source = new Source(Article("1", "BUNDLES", 300, "bundles/300.png"));
        var service = Service(bridge, http, source);
        var category = service.Categories.Single(item => item.Id == "1");
        category.Start = 1;
        await service.ScanAsync(category, service.GetScanCandidatesForCategory(category).Where(row => row.AssetId <= 5), default);
        Assert.Equal(0, source.ReadCalls);
        await service.SyncSourcesAsync(default);
        Assert.Equal(1, source.ReadCalls);
        Assert.Equal(6, category.Ranges.Single().NextId);
        var next = service.GetScanCandidatesForCategory(category);
        Assert.Equal(10, next.Count);
        Assert.Equal(new long[] { 4, 5, 6, 7, 8, 9, 10, 11, 12, 300 }, next.Select(row => row.AssetId));
        await service.ScanAsync(category, next, default);
        Assert.Equal(1, source.ReadCalls);
        Assert.Equal(13, category.Ranges.Single().NextId);
        var visible = service.GetAssetListForCategory(category);
        Assert.Contains(visible, row => row.AssetId == 300 && row.CanDownload);
        Assert.Equal(10, visible.Count(row => !row.CanDownload));
    }

    [Fact]
    public async Task AutomaticMonitorScanDoesNotSynchronizeApiSources()
    {
        using var bridge = new AssetsManagerTestBridge();
        using var http = Client(_ => Task.FromResult(Response(HttpStatusCode.NotFound)));
        var source = new Source();
        var service = Service(bridge, http, source);
        await service.ScanAllAsync(null, default);
        Assert.Equal(0, source.ReadCalls);
    }

    [Fact]
    public async Task ExtensionFallbackRetainsApiArticleAndResolvedUrl()
    {
        using var bridge = new AssetsManagerTestBridge();
        using var http = Client(request => Task.FromResult(Response(request.RequestUri.AbsolutePath.EndsWith(".jpg")
            ? HttpStatusCode.OK : HttpStatusCode.Forbidden)));
        var article = Article("9", "SKIN_AUGMENT", 60007, "skinaugment/60007.png");
        article.CandidateUrl = article.ImageUrl;
        article.ImageUrl = null;
        var service = Service(bridge, http, source: new Source(article));
        await service.SyncSourcesAsync(default);
        await service.ScanAsync(service.Categories.Single(c => c.Id == "9"), service.GetCatalogAssets(), default);
        var row = service.GetCatalogAssets().Single(row => row.CanDownload);
        Assert.Equal(60007, row.ItemId);
        Assert.Equal("LCU + CDN", row.Source);
        Assert.EndsWith(".jpg", row.Url);
        var reloaded = Service(bridge, http);
        Assert.EndsWith(".jpg", reloaded.GetCatalogAssets().Single(row => row.ItemId == 60007 && row.CanDownload).Url);
    }

    [Fact]
    public void CorruptCacheIsPreservedAndFreshCatalogRemainsUsable()
    {
        using var bridge = new AssetsManagerTestBridge();
        string path = CachePath(bridge);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, "{broken");
        using var http = Client();
        var service = Service(bridge, http);
        Assert.Equal(10, service.Categories.Count);
        Assert.Equal("{broken", File.ReadAllText(Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path), "*.recovery-*"))));
    }

    internal static string CachePath(AssetsManagerTestBridge bridge) => Path.Combine(bridge.Directories.ApiCachePath, "asset_tracker", "catalog.json");
    private static AssetCatalogService Service(AssetsManagerTestBridge bridge, HttpClient http, Source source = null) =>
        new(new AssetTrackerScannerService(http) { MaxAttempts = 1 }, source ?? new Source(), bridge.LogService, bridge.Directories);
    private static AssetCatalogArticle Article(string category, string type, long id, string path) => new()
    {
        Environment = "LIVE", InventoryType = type, ItemId = id, CategoryId = category, ImageUrl = Root + path,
        Source = "LCU", Name = "Article " + id, FirstSeenApi = DateTime.UtcNow, LastSeenApi = DateTime.UtcNow
    };
    internal static HttpClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> response = null) =>
        new(new Handler(response ?? (_ => Task.FromResult(Response(HttpStatusCode.OK)))));
    internal static HttpResponseMessage Response(HttpStatusCode status)
    {
        var result = new HttpResponseMessage(status) { Content = new ByteArrayContent(Array.Empty<byte>()) };
        result.Content.Headers.ContentType = new("image/png");
        return result;
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => response(request);
    }
    [Fact]
    public async Task CategoryFetchUsesItsKnownRangesWithoutImportingOtherCategoriesOrAdvancingMonitor()
    {
        using var bridge = new AssetsManagerTestBridge();
        using var http = Client(_ => throw new InvalidOperationException("Fetching must not probe the CDN."));
        var source = new Source(Article("9", "SKIN_AUGMENT", 60007, "skinaugment/60007.png"),
            Article("1", "BUNDLES", 79900910, "bundles/79900910.jpg"));
        var service = Service(bridge, http, source: source);
        var category = service.Categories.Single(item => item.Id == "9");
        var cursors = category.Ranges.Select(range => range.NextId).ToArray();
        await service.SyncSourcesAsync(default, "9");
        Assert.Equal("9", source.LastQuery.CategoryId);
        Assert.All(source.LastQuery.KnownItems, key => Assert.Equal("SKIN_AUGMENT", key.Type));
        foreach (long id in new long[] { 4, 5, 6, 60007, 60008 })
            Assert.Contains(("SKIN_AUGMENT", id), source.LastQuery.KnownItems);
        Assert.All(service.GetCatalogAssets(), row => Assert.Equal("9", row.CategoryId));
        Assert.Equal(cursors, category.Ranges.Select(range => range.NextId));
    }

    private sealed class Source(params AssetCatalogArticle[] articles) : IAssetCatalogSource
    {
        public int ReadCalls { get; private set; }
        public AssetCatalogQuery LastQuery { get; private set; }
        public Task<AssetCatalogImportBatch> ReadAsync(CancellationToken token, AssetCatalogQuery query = null)
        {
            ReadCalls++;
            LastQuery = query;
            return Task.FromResult(new AssetCatalogImportBatch
            { Environment = "LIVE", Status = "Fixture catalog loaded", ApiReadSucceeded = true, Articles = articles.ToList() });
        }
    }
}
