using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Downloads;
using AssetsManager.Tests.xUnit.Infrastructure;
using AssetsManager.Utils;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Downloads;

public sealed class HashSynchronizationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectionUsesDiskSizesAndChecksEveryCatalog(bool staleSettings)
    {
        using var bridge = new AssetsManagerTestBridge();
        bridge.Directories.CreateHashesDirectories();
        Write(bridge, "hashes.game.txt", 100);
        Write(bridge, "hashes.lcu.txt", 10);
        Write(bridge, "hashes.binhashes.txt", 8);
        Write(bridge, "hashes.binfields.txt", 4);
        var settings = new AppSettings();
        if (staleSettings) settings.HashesSizes = new Dictionary<string, long>
        { ["hashes.game.txt"] = 1, ["hashes.lcu.txt"] = 500 };
        using var client = Client(_ => Listing(("hashes.game.txt", 80), ("hashes.lcu.txt", 20),
            ("hashes.binhashes.txt", 12), ("hashes.binfields.txt", 4), ("hashes.bintypes.txt", 5)));
        var status = CreateStatus(bridge, settings, client);
        int notifications = 0;

        var selected = await status.GetOutdatedHashFilesAsync(true, () => notifications++);

        Assert.Equal(new[] { "hashes.lcu.txt", "hashes.binhashes.txt", "hashes.bintypes.txt" }, selected);
        Assert.Equal(1, notifications);
        Assert.Equal(100, settings.HashesSizes["hashes.game.txt"]);
        Assert.Equal(10, settings.HashesSizes["hashes.lcu.txt"]);
        Assert.Equal(8, settings.HashesSizes["hashes.binhashes.txt"]);
        Assert.Equal(0, settings.HashesSizes["hashes.bintypes.txt"]);
        Assert.False(status.IsSyncing);
    }

    [Fact]
    public async Task SmallerRemoteDoesNotRepairAnObsoleteRecordedSize()
    {
        using var bridge = new AssetsManagerTestBridge();
        bridge.Directories.CreateHashesDirectories();
        Write(bridge, "hashes.game.txt", 100);
        var settings = new AppSettings { HashesSizes = new Dictionary<string, long> { ["hashes.game.txt"] = 999 } };
        using var client = Client(_ => Listing(("hashes.game.txt", 80)));
        var status = CreateStatus(bridge, settings, client);

        Assert.Empty(await status.GetOutdatedHashFilesAsync(true));
        Assert.Equal(100, settings.HashesSizes["hashes.game.txt"]);
    }

    [Fact]
    public async Task PartialFailuresPreserveLocalSizesAndRetryOnlyFailedCatalogs()
    {
        using var bridge = new AssetsManagerTestBridge();
        bridge.Directories.CreateHashesDirectories();
        Write(bridge, "hashes.game.txt", 100);
        Write(bridge, "hashes.lcu.txt", 7);
        Write(bridge, "hashes.binhashes.txt", 8);
        var settings = new AppSettings();
        bool failLcu = true;
        var downloads = new List<string>();
        using var client = Client(request =>
        {
            string name = Path.GetFileName(request.RequestUri.AbsolutePath);
            if (name.Length == 0) return Listing(("hashes.game.txt", 80), ("hashes.lcu.txt", 20), ("hashes.binhashes.txt", 24));
            downloads.Add(name);
            return name == "hashes.lcu.txt" && failLcu
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : Content(name == "hashes.lcu.txt" ? 20 : 24);
        });
        var status = CreateStatus(bridge, settings, client);
        var completions = new List<bool>();
        status.HashSyncCompleted += completions.Add;

        Assert.False(await status.SyncHashesIfNeeds(true, true));
        Assert.Equal(7, settings.HashesSizes["hashes.lcu.txt"]);
        Assert.Equal(24, settings.HashesSizes["hashes.binhashes.txt"]);
        Assert.Equal(100, settings.HashesSizes["hashes.game.txt"]);
        Assert.False(status.IsSyncing);
        failLcu = false;
        Assert.True(await status.SyncHashesIfNeeds(true, true));

        Assert.Equal(new[] { false, true }, completions);
        Assert.Equal(2, downloads.Count(name => name == "hashes.lcu.txt"));
        Assert.Equal(1, downloads.Count(name => name == "hashes.binhashes.txt"));
        Assert.DoesNotContain("hashes.game.txt", downloads);
        Assert.Equal(20, settings.HashesSizes["hashes.lcu.txt"]);
        Assert.Empty(Directory.GetFiles(bridge.Directories.HashesPath, "*.tmp"));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(10, 30)]
    public async Task EmptyOrTruncatedDownloadsPreserveExistingFiles(int actualLength, int announcedLength)
    {
        using var bridge = new AssetsManagerTestBridge();
        bridge.Directories.CreateHashesDirectories();
        string path = Write(bridge, "hashes.game.txt", 5);
        byte[] original = File.ReadAllBytes(path);
        using var client = Client(_ =>
        {
            var response = Content(actualLength);
            response.Content.Headers.ContentLength = announcedLength;
            return response;
        });
        var requests = new Requests(client, bridge.Directories, bridge.LogService);

        Assert.False(await requests.DownloadHashesAsync("hashes.game.txt", bridge.Directories.HashesPath));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task GrowingLocalCatalogIsCheckedAgainBeforeReplacement()
    {
        using var bridge = new AssetsManagerTestBridge();
        bridge.Directories.CreateHashesDirectories();
        Write(bridge, "hashes.game.txt", 5);
        var settings = new AppSettings();
        using var client = Client(request =>
        {
            if (request.RequestUri.AbsolutePath.EndsWith('/')) return Listing(("hashes.game.txt", 30));
            Write(bridge, "hashes.game.txt", 40);
            return Content(30);
        });
        var status = CreateStatus(bridge, settings, client);

        Assert.True(await status.SyncHashesIfNeeds(true, true));
        Assert.Equal(40, settings.HashesSizes["hashes.game.txt"]);
        Assert.Empty(Directory.GetFiles(bridge.Directories.HashesPath, "*.tmp"));
    }

    [Fact]
    public async Task BinaryCacheIsRetainedAndRegeneratedFromUpdatedText()
    {
        using var bridge = new AssetsManagerTestBridge();
        bridge.Directories.CreateHashesDirectories();
        string path = Path.Combine(bridge.Directories.HashesPath, "hashes.game.txt");
        File.WriteAllText(path, "0000000000000001 old\n");
        using (var cache = new BinaryHashCache(path, bridge.LogService)) cache.Load();
        string binaryPath = Path.ChangeExtension(path, ".bin");
        byte[] originalCache = File.ReadAllBytes(binaryPath);
        File.SetLastWriteTimeUtc(binaryPath, DateTime.UtcNow.AddMinutes(-1));
        const string updated = "0000000000000001 updated_longer_path\n0000000000000002 second\n";
        using var client = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(updated) });
        var requests = new Requests(client, bridge.Directories, bridge.LogService);

        Assert.True(await requests.DownloadHashesAsync("hashes.game.txt", bridge.Directories.HashesPath));
        Assert.Equal(originalCache, File.ReadAllBytes(binaryPath));
        using var rebuilt = new BinaryHashCache(path, bridge.LogService);
        rebuilt.Load();
        Assert.Equal("updated_longer_path", rebuilt.Resolve(1));
        Assert.Equal("second", rebuilt.Resolve(2));
    }

    [Fact]
    public async Task UnavailableServerKeepsCatalogsAndRecordsLocalSizes()
    {
        using var bridge = new AssetsManagerTestBridge();
        bridge.Directories.CreateHashesDirectories();
        Write(bridge, "hashes.game.txt", 100);
        var settings = new AppSettings();
        using var client = Client(_ => throw new HttpRequestException("Offline"));
        var status = CreateStatus(bridge, settings, client);
        int completed = 0;
        status.HashSyncCompleted += _ => completed++;

        Assert.False(await status.SyncHashesIfNeeds(true, true));
        Assert.Equal(100, settings.HashesSizes["hashes.game.txt"]);
        Assert.Equal(0, completed);
        Assert.False(status.IsSyncing);
    }

    [Fact]
    public async Task DisabledSynchronizationMakesNoNetworkRequests()
    {
        using var bridge = new AssetsManagerTestBridge();
        int calls = 0;
        using var client = Client(_ => { calls++; return Listing(("hashes.game.txt", 100)); });
        var status = CreateStatus(bridge, new AppSettings(), client);
        Assert.False(await status.SyncHashesIfNeeds(false));
        Assert.Equal(0, calls);
    }

    private static Status CreateStatus(AssetsManagerTestBridge bridge, AppSettings settings, HttpClient client)
        => new(bridge.LogService, new Requests(client, bridge.Directories, bridge.LogService), settings, client, bridge.Directories);

    private static string Write(AssetsManagerTestBridge bridge, string name, int length)
    {
        string path = Path.Combine(bridge.Directories.HashesPath, name);
        File.WriteAllBytes(path, Enumerable.Repeat((byte)'x', length).ToArray());
        return path;
    }

    private static HttpResponseMessage Listing(params (string Name, int Length)[] files)
        => new(HttpStatusCode.OK) { Content = new StringContent(string.Join("\n", files.Select(file =>
            $"<a href=\"{file.Name}\">{file.Name}</a> {file.Length}"))) };

    private static HttpResponseMessage Content(int length)
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[length]) };

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => new(new StubHandler(respond));

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(respond(request));
    }
}
