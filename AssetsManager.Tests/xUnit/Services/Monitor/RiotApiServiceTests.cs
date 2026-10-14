using System;
using System.Text.Json;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Tests.xUnit.Infrastructure;
using AssetsManager.Utils;
using AssetsManager.Services.Monitor;
using AssetsManager.Views.Models.Monitor;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Monitor;

public sealed class RiotApiServiceTests
{
    [Theory]
    [InlineData("rewards", "pbe", "https://pbe-red.lol.sgp.pvp.net")]
    [InlineData("rewards", "euw", "https://euw-red.lol.sgp.pvp.net")]
    [InlineData("sales", "euw", "https://euw-red.lol.sgp.pvp.net")]
    [InlineData("mythic_shop", "pbe", "https://pbe-red.lol.sgp.pvp.net")]
    public void UsesDynamicLeagueRegionForCatalogHosts(string endpoint, string region, string expected)
    {
        Assert.Equal(expected, Endpoints.GetRemoteBaseUrl(endpoint, region));
    }

    [Theory]
    [InlineData("pbe", "https://pbe.pp.sgp.pvp.net")]
    [InlineData("euw", "https://euc1-red.pp.sgp.pvp.net")]
    public async Task ReadsProgressionHostFromClientConfiguration(string region, string configuredUrl)
    {
        using var bridge = new AssetsManagerTestBridge();
        using var client = new HttpClient(new ConfigHandler(JsonSerializer.Serialize(configuredUrl + "/")));
        var service = CreateService(client, bridge);
        Assert.Equal(configuredUrl, await service.ResolveRemoteBaseUrlAsync("progression", region));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("unavailable")]
    [InlineData("\"https://untrusted.example\"")]
    [InlineData("\"http://euc1-red.pp.sgp.pvp.net\"")]
    public async Task DoesNotGuessProgressionHostWhenClientConfigurationIsUnavailableOrInvalid(string body)
    {
        using var bridge = new AssetsManagerTestBridge();
        using var client = new HttpClient(new ConfigHandler(body));
        var service = CreateService(client, bridge);
        Assert.Null(await service.ResolveRemoteBaseUrlAsync("progression", "euw"));
        Assert.Null(await service.ResolveRemoteBaseUrlAsync("progression", "pbe"));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task DoesNotGuessProgressionHostWhenConfigurationRequestFails(HttpStatusCode status)
    {
        using var bridge = new AssetsManagerTestBridge();
        using var client = new HttpClient(new ConfigHandler("null", status: status));
        var service = CreateService(client, bridge);
        Assert.Null(await service.ResolveRemoteBaseUrlAsync("progression", "euw"));
    }

    [Fact]
    public async Task RewardsKeepsLeagueHostWithoutRequestingPlayerPlatformConfiguration()
    {
        using var bridge = new AssetsManagerTestBridge();
        using var client = new HttpClient(new ConfigHandler("null", allowRequest: false));
        var service = CreateService(client, bridge);
        Assert.Equal("https://euw-red.lol.sgp.pvp.net", await service.ResolveRemoteBaseUrlAsync("rewards", "euw"));
    }

    [Theory]
    [InlineData("2026-09-30T19:15:00Z", "current")]
    [InlineData("2026-10-07T18:00:00Z", "worlds")]
    [InlineData("2027-02-01T00:00:00Z", null)]
    public void SelectsOnlyPassesWhoseStartAndEndDatesIncludeNow(string now, string expectedId)
    {
        using var hub = JsonDocument.Parse("""
            [
              {"event":{"eventHubType":"kSeasonPass","localizedName":"Expired","startDate":"2026-06-10T18:00:00Z","endDate":"2026-07-29T06:59:00Z","rewardTrack":{"trackConfig":{"id":"expired"}}}},
              {"event":{"eventHubType":"kSeasonPass","localizedName":"Current","startDate":"2026-07-29T18:00:00Z","endDate":"2026-10-07T06:59:00Z","rewardTrack":{"trackConfig":{"id":"current"}}}},
              {"event":{"eventHubType":"kSeasonPass","localizedName":"Worlds","startDate":"2026-10-07T20:00:00+02:00","endDate":"2027-01-07T07:59:00Z","rewardTrack":{"trackConfig":{"id":"worlds"}}}}
            ]
            """);
        var pass = RiotApiService.FindActivePass(hub.RootElement, DateTimeOffset.Parse(now));
        Assert.Equal(expectedId, pass.Id);
    }

    private static RiotApiService CreateService(HttpClient client, AssetsManagerTestBridge bridge)
    {
        var settings = new AppSettings();
        settings.ApiSettings.Connection.LocalApiUrl = "https://127.0.0.1:12345";
        settings.ApiSettings.Connection.Password = "test-password";
        return new RiotApiService(settings, client, bridge.LogService, bridge.Directories, null);
    }

    private sealed class ConfigHandler(string body, bool allowRequest = true, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.True(allowRequest);
            Assert.Equal("127.0.0.1", request.RequestUri!.Host);
            Assert.Equal("/client-config/v2/config/lol.client_settings.player_platform_edge.url", request.RequestUri.AbsolutePath);
            Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
