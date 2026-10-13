using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Downloads;
using AssetsManager.Tests.xUnit.Infrastructure;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Downloads;

public sealed class AssetDownloaderTests
{
    [Fact]
    public async Task CancelledRequestPreservesExistingDestination()
    {
        using var bridge = new AssetsManagerTestBridge();
        using var cancel = new CancellationTokenSource();
        string path = Path.Combine(bridge.RootPath, "existing.png");
        File.WriteAllText(path, "original");
        using var http = new HttpClient(new Handler((request, token) =>
        {
            cancel.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(cancel.Token);
        }));
        var downloader = new AssetDownloader(http, bridge.LogService, bridge.Directories);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => downloader.DownloadAssetToCustomPathAsync("https://cdn.example/1.png", path, cancel.Token));
        Assert.Equal("original", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(bridge.RootPath, "*.partial"));
    }

    [Fact]
    public async Task FailedCopyPreservesDestinationAndRemovesPartialFile()
    {
        using var bridge = new AssetsManagerTestBridge();
        string path = Path.Combine(bridge.RootPath, "existing.png");
        File.WriteAllText(path, "original");
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new FailingContent() })));
        var downloader = new AssetDownloader(http, bridge.LogService, bridge.Directories);
        await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadAssetToCustomPathAsync("https://cdn.example/1.png", path));
        Assert.Equal("original", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(bridge.RootPath, "*.partial"));
    }

    [Fact]
    public async Task CompleteDownloadReplacesDestinationOnlyAfterSuccess()
    {
        using var bridge = new AssetsManagerTestBridge();
        string path = Path.Combine(bridge.RootPath, "existing.png");
        File.WriteAllText(path, "original");
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("complete") })));
        var downloader = new AssetDownloader(http, bridge.LogService, bridge.Directories);
        await downloader.DownloadAssetToCustomPathAsync("https://cdn.example/1.png", path);
        Assert.Equal("complete", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(bridge.RootPath, "*.partial"));
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => response(request, token);
    }
    private sealed class FailingContent : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 4; return true; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext context)
        {
            await stream.WriteAsync(new byte[] { 1, 2 });
            throw new IOException("Fixture interrupted transfer");
        }
    }
}
