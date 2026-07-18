using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.BenchmarkTests.Infrastructure;
using AssetsManager.Services.Monitor;
using Xunit;

namespace AssetsManager.BenchmarkTests.Services.Monitor;

public sealed class RmanApiServiceTests
{
    [Fact]
    public async Task FetchesBothSourcesDefensivelyAndRemovesDuplicates()
    {
        using var bridge = new AssetsManagerTestBridge();
        using var client = new HttpClient(new JsonHandler());
        var service = new RmanApiService(client, bridge.LogService);

        var versions = await service.FetchVersionsAsync();

        Assert.Equal(2, versions.Count);
        Assert.Contains(versions, item => item.Product == "Game Client"
                                          && item.Category == "lol-game-client"
                                          && item.Version == "16.15.1");
        Assert.Single(versions, item => item.Product == "League Client");
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
            string json = request.RequestUri!.Host.StartsWith("sieve", StringComparison.OrdinalIgnoreCase)
                ? """
                  {"releases":[
                    {"release":{"labels":{"riot:artifact_type_id":{"values":["lol-game-client"]},"riot:artifact_version_id":{"values":["16.15.1+abc"]}}},"download":{"url":"https://example/game.rman"}},
                    {"release":{"labels":{}},"download":{}}
                  ]}
                  """
                : """
                  {"keystone.products.league_of_legends.patchlines.pbe":{"platforms":{"win":{"configurations":[
                    {"patch_url":"https://example/client.rman"},
                    {"patch_url":"https://example/client.rman"},
                    {}
                  ]}}}}
                  """;
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
