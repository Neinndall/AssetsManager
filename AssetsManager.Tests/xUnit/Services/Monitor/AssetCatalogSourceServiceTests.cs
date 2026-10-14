using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using AssetsManager.Services.Monitor;
using AssetsManager.Tests.xUnit.Infrastructure;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Settings;
using AssetsManager.Views.Models.Monitor;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Monitor;

public sealed class AssetCatalogSourceServiceTests
{
    [Fact]
    public async Task CategoryFetchFindsInactiveAugmentsThroughApiAndDoesNotLookupOtherCategories()
    {
        using var bridge = new AssetsManagerTestBridge();
        string directory = bridge.CreateDirectory("LIVE");
        File.WriteAllText(Path.Combine(directory, "lockfile"), "LeagueClient:123:44321:fixture:https");
        using var http = AssetCatalogServiceTests.Client(request =>
        {
            string path = request.RequestUri.AbsolutePath;
            if (path == "/lol-store/v1/catalog")
            {
                Assert.Contains("[\"SKIN_AUGMENT\"]", Uri.UnescapeDataString(request.RequestUri.Query));
                return Json("[{\"inventoryType\":\"SKIN_AUGMENT\",\"itemId\":2,\"iconUrl\":\"https://d392eissrffsyf.cloudfront.net/storeImages/skinaugment/2.png\"}]");
            }
            if (path == "/lol-store/v1/catalog/items/skip-cache")
            {
                string query = Uri.UnescapeDataString(request.RequestUri.Query);
                Assert.DoesNotContain("BUNDLES", query);
                foreach (string id in new[] { "5", "6", "60007", "60008" }) Assert.Contains("\"itemId\":" + id, query);
                return Json("[{\"inventoryType\":\"SKIN_AUGMENT\",\"itemId\":60007,\"active\":false,\"iconUrl\":\"https://d392eissrffsyf.cloudfront.net/storeImages/skinaugment/60007.png\"},{\"inventoryType\":\"SKIN_AUGMENT\",\"itemId\":60008,\"active\":false,\"iconUrl\":\"https://d392eissrffsyf.cloudfront.net/storeImages/skinaugment/60008.png\"},{\"inventoryType\":\"SKIN_AUGMENT\",\"itemId\":5,\"iconUrl\":\"https://d392eissrffsyf.cloudfront.net/storeImages/skinaugment/5.png\"},{\"inventoryType\":\"SKIN_AUGMENT\",\"itemId\":6,\"iconUrl\":\"default.png\"},{\"inventoryType\":\"BUNDLES\",\"itemId\":79900910}]");
            }
            return Json(path switch
            {
                "/riotclient/region-locale" => "{\"region\":\"EUW\"}",
                "/lol-login/v1/session" => "{\"state\":\"SUCCEEDED\"}",
                "/lol-summoner/v1/current-summoner" => "{\"summonerId\":123456}",
                "/lol-store/v1/store-ready" => "true",
                "/lol-store/v1/featured" => "[{\"inventoryType\":\"BUNDLES\",\"itemId\":99901659}]",
                _ => throw new InvalidOperationException("Unexpected request: " + path)
            });
        });
        var query = new AssetCatalogQuery { CategoryId = "9", KnownItems = new() { ("SKIN_AUGMENT", 5), ("SKIN_AUGMENT", 6), ("SKIN_AUGMENT", 60007), ("SKIN_AUGMENT", 60008) } };
        var batch = await new AssetCatalogSourceService(Settings(directory), http, bridge.LogService).ReadAsync(default, query);
        Assert.True(batch.ApiReadSucceeded);
        Assert.All(batch.Articles, article => Assert.Equal("9", article.CategoryId));
        Assert.Equal(5, batch.Articles.Count);
        Assert.False(batch.Articles.Single(article => article.ItemId == 60007).Active);
        Assert.EndsWith("60008.png", batch.Articles.Single(article => article.ItemId == 60008).ImageUrl);
        Assert.Null(batch.Articles.Single(article => article.ItemId == 6).ImageUrl);
    }

    [Fact]
    public async Task ClosedClientDoesNotReadGameFilesOrMakeNetworkRequests()
    {
        using var bridge = new AssetsManagerTestBridge();
        string directory = bridge.CreateDirectory("LIVE");
        string plugins = bridge.CreateDirectory("LIVE/Plugins/rcp-be-lol-game-data");
        bridge.BakeWad(plugins, "default-assets.wad",
            ("plugins/rcp-be-lol-game-data/global/default/v1/skinaugments.json",
             "[{\"itemId\":60007,\"capType\":\"kChampionSkinAugment\"}]"));
        using var http = AssetCatalogServiceTests.Client(_ => throw new InvalidOperationException("Closed client must not issue requests."));
        var batch = await new AssetCatalogSourceService(Settings(directory), http, bridge.LogService).ReadAsync(default);
        Assert.False(batch.ApiReadSucceeded);
        Assert.False(batch.ClientReadSucceeded);
        Assert.Empty(batch.Articles);
        Assert.Contains("open and sign in", batch.Status);
    }

    [Fact]
    public async Task UsesReadOnlyClientCatalogueAndTargetedFeaturedKeysWithoutChangingSettings()
    {
        using var bridge = new AssetsManagerTestBridge();
        string directory = bridge.CreateDirectory("LIVE");
        File.WriteAllText(Path.Combine(directory, "lockfile"), "LeagueClient:123:44321:fixture:https\n");
        var paths = new List<string>();
        bool requestedFeaturedKey = false;
        using var http = AssetCatalogServiceTests.Client(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("127.0.0.1", request.RequestUri.Host);
            string path = request.RequestUri.AbsolutePath;
            paths.Add(path);
            if (path == "/lol-store/v1/catalog")
            {
                string query = Uri.UnescapeDataString(request.RequestUri.Query);
                Assert.Contains("[\"BUNDLES\",", query);
                return Json("[{\"inventoryType\":\"SKIN_AUGMENT\",\"itemId\":60007,\"active\":false,\"iconUrl\":\"https://d392eissrffsyf.cloudfront.net/storeImages/skinaugment/60007.png\"}]");
            }
            if (path == "/lol-store/v1/catalog/items/skip-cache")
            {
                string query = Uri.UnescapeDataString(request.RequestUri.Query);
                requestedFeaturedKey |= query.Contains("99901659", StringComparison.Ordinal);
                return Json("[{\"inventoryType\":\"BUNDLES\",\"subInventoryType\":\"HEXTECH_BUNDLE\",\"itemId\":99901659,\"iconUrl\":\"https://d392eissrffsyf.cloudfront.net/storeImages/bundles/99901659_1.png\"}]");
            }
            return Json(path switch
            {
                "/riotclient/region-locale" => "{\"region\":\"EUW\"}",
                "/lol-login/v1/session" => "{\"state\":\"SUCCEEDED\"}",
                "/lol-store/v1/store-ready" => "true",
            "/lol-summoner/v1/current-summoner" => "{\"summonerId\":123456}",
                "/lol-store/v1/featured" => "{\"items\":[{\"inventoryType\":\"BUNDLES\",\"itemId\":99901659}]}",
                _ => throw new InvalidOperationException("Unexpected client request: " + path)
            });
        });
        var settings = Settings(directory);
        string before = JsonSerializer.Serialize(settings.ApiSettings);
        var batch = await new AssetCatalogSourceService(settings, http, bridge.LogService).ReadAsync(default);
        Assert.True(batch.ApiReadSucceeded);
        Assert.True(requestedFeaturedKey);
        Assert.Equal(123456, batch.SummonerId);
        Assert.True(batch.ClientReadSucceeded);
        Assert.Equal(before, JsonSerializer.Serialize(settings.ApiSettings));
        Assert.False(batch.Articles.Single(article => article.ItemId == 60007).Active);
        Assert.EndsWith("99901659_1.png", batch.Articles.Single(article => article.ItemId == 99901659).ImageUrl);
        Assert.DoesNotContain(paths, path => path.Contains("auth", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("PBE", "SUCCEEDED", 1)]
    [InlineData("EUW", "IN_PROGRESS", 2)]
    public async Task EnvironmentMismatchOrSignedOutClientStopsBeforeStoreRequests(string region, string state, int count)
    {
        using var bridge = new AssetsManagerTestBridge();
        string directory = bridge.CreateDirectory("LIVE");
        File.WriteAllText(Path.Combine(directory, "lockfile"), "LeagueClient:123:44321:fixture:https");
        int requests = 0;
        using var http = AssetCatalogServiceTests.Client(request =>
        {
            requests++;
            Assert.Equal(HttpMethod.Get, request.Method);
            return Json(request.RequestUri.AbsolutePath switch
            {
                "/riotclient/region-locale" => JsonSerializer.Serialize(new { region }),
                "/lol-login/v1/session" => JsonSerializer.Serialize(new { state }),
                _ => throw new InvalidOperationException("The store must not be requested before a valid session.")
            });
        });
        var batch = await new AssetCatalogSourceService(Settings(directory), http, bridge.LogService).ReadAsync(default);
        Assert.Equal(count, requests);
        Assert.False(batch.ApiReadSucceeded);
    }

    [Fact]
    public async Task FailedFeaturedReadRetainsAlreadyRetrievedCatalog()
    {
        using var bridge = new AssetsManagerTestBridge();
        string directory = bridge.CreateDirectory("LIVE");
        File.WriteAllText(Path.Combine(directory, "lockfile"), "LeagueClient:123:44321:fixture:https");
        using var http = AssetCatalogServiceTests.Client(request => Json(request.RequestUri.AbsolutePath switch
        {
            "/riotclient/region-locale" => "{\"region\":\"EUW\"}",
            "/lol-login/v1/session" => "{\"state\":\"SUCCEEDED\"}",
            "/lol-store/v1/store-ready" => "true",
            "/lol-summoner/v1/current-summoner" => "{\"summonerId\":123456}",
            "/lol-store/v1/catalog" => "[{\"inventoryType\":\"SKIN_AUGMENT\",\"itemId\":60008}]",
            _ => throw new HttpRequestException("Fixture disconnected")
        }));
        var batch = await new AssetCatalogSourceService(Settings(directory), http, bridge.LogService).ReadAsync(default);
        Assert.True(batch.ApiReadSucceeded);
        Assert.Equal(60008, Assert.Single(batch.Articles).ItemId);
    }

    [Theory]
    [InlineData("BUNDLES", 79900910, "1")]
    [InlineData("BUNDLES", 99901659, "2")]
    [InlineData("BUNDLES", 89900045, "3")]
    [InlineData("BUNDLES", 69901087, "4")]
    [InlineData("EVENT_PASS", 9006, "5")]
    [InlineData("CURRENCY", 99, "6")]
    [InlineData("HEXTECH_CRAFTING", 817, "7")]
    [InlineData("REGALIA_BANNER", 13, "8")]
    [InlineData("SKIN_AUGMENT", 60008, "9")]
    [InlineData("BUNDLES", 91000011, "10")]
    [InlineData("BUNDLES", 33300247, null)]
    [InlineData("EVENT_PASS", 3333049, null)]
    public void KeepsExistingCategoriesAndRejectsTftPrefixes(string inventoryType, long id, string category) =>
        Assert.Equal(category, AssetCatalogRouting.GetArticleCategory(inventoryType, id));

    private static AppSettings Settings(string directory) => new() { LolLiveDirectory = directory, ApiSettings = new() { ClientTarget = ApiClientTarget.LIVE } };
    private static Task<HttpResponseMessage> Json(string body) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
}
