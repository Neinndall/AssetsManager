using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Tests.xUnit.Infrastructure;
using AssetsManager.Services.Monitor;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Monitor;

public sealed class RmanApiServiceTests
{
    [Fact]
    public async Task FetchesAllGameManifestsByDefaultAndRemovesDuplicates()
    {
        using var bridge = new AssetsManagerTestBridge();
        using var client = new HttpClient(new JsonHandler());
        var service = new RmanApiService(client, bridge.LogService);

        var versions = await service.FetchVersionsAsync();

        Assert.Equal(4, versions.Count);
        Assert.Equal(3, versions.Count(item => item.Product == "Game Client"));
        Assert.Contains(versions, item => item.Product == "Game Client"
                                          && item.Category == "lol-game-client"
                                          && item.Version == "16.14.9");
        Assert.Contains(versions, item => item.Product == "Game Client"
                                          && item.Category == "lol-game-client"
                                          && item.Version == "16.15.1");
        Assert.Contains(versions, item => item.Product == "Game Client"
                                          && item.Category == "lol-game-client"
                                          && item.Version == "16.15.2");
        Assert.Single(versions, item => item.Product == "League Client");
    }

    [Fact]
    public async Task FetchesOnlyCurrentDayGameManifestsWhenRequested()
    {
        using var bridge = new AssetsManagerTestBridge();
        using var client = new HttpClient(new JsonHandler());
        var service = new RmanApiService(client, bridge.LogService);

        var versions = await service.FetchVersionsAsync(DateTime.Today);

        Assert.Equal(3, versions.Count);
        Assert.Equal(2, versions.Count(item => item.Product == "Game Client"));
        Assert.DoesNotContain(versions, item => item.Version == "16.14.9");
    }

    [Fact]
    public async Task FetchesGameManifestsForTheRequestedDay()
    {
        using var bridge = new AssetsManagerTestBridge();
        using var client = new HttpClient(new JsonHandler());
        var service = new RmanApiService(client, bridge.LogService);

        var versions = await service.FetchVersionsAsync(DateTime.Today.AddDays(-1));

        var gameVersion = Assert.Single(versions, item => item.Product == "Game Client");
        Assert.Equal("16.14.9", gameVersion.Version);
    }

    [Fact]
    public async Task PropagatesCancellationToBothRequests()
    {
        using var bridge = new AssetsManagerTestBridge();
        using var client = new HttpClient(new BlockingHandler());
        var service = new RmanApiService(client, bridge.LogService);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.FetchVersionsAsync(cancellation.Token));
    }

    private sealed class JsonHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.Now;
            string yesterday = new DateTimeOffset(now.Date.AddDays(-1).AddHours(23), now.Offset)
                .ToUniversalTime()
                .ToString("O");
            string todayAt20 = new DateTimeOffset(now.Date.AddHours(20), now.Offset)
                .ToUniversalTime()
                .ToString("O");
            string todayAt23 = new DateTimeOffset(now.Date.AddHours(23), now.Offset)
                .ToUniversalTime()
                .ToString("O");

            string json;
            if (request.RequestUri!.Host.StartsWith("sieve", StringComparison.OrdinalIgnoreCase))
            {
                json = """
                       {"releases":[
                         {"release":{"created_at":"YESTERDAY","labels":{"riot:artifact_type_id":{"values":["lol-game-client"]},"riot:artifact_version_id":{"values":["16.14.9+old"]}}},"download":{"url":"https://example/old-game.rman"}},
                         {"release":{"created_at":"TODAY_20","labels":{"riot:artifact_type_id":{"values":["lol-game-client"]},"riot:artifact_version_id":{"values":["16.15.1+abc"]}}},"download":{"url":"https://example/game-20.rman"}},
                         {"release":{"created_at":"TODAY_23","labels":{"riot:artifact_type_id":{"values":["lol-game-client"]},"riot:artifact_version_id":{"values":["16.15.2+abc"]}}},"download":{"url":"https://example/game-23.rman"}},
                         {"release":{"labels":{}},"download":{}}
                       ]}
                       """
                    .Replace("YESTERDAY", yesterday)
                    .Replace("TODAY_20", todayAt20)
                    .Replace("TODAY_23", todayAt23);
            }
            else
            {
                json = """
                       {"keystone.products.league_of_legends.patchlines.pbe":{"platforms":{"win":{"configurations":[
                         {"patch_url":"https://example/client.rman"},
                         {"patch_url":"https://example/client.rman"},
                         {}
                       ]}}}}
                       """;
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }
    }
}
