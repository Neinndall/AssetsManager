using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.BenchmarkTests.Infrastructure;
using AssetsManager.Services.Monitor;
using AssetsManager.Views.Models.Monitor;
using Xunit;
using ZstdSharp;

namespace AssetsManager.BenchmarkTests.Services.Monitor;

public sealed class ManifestDownloaderTests
{
    [Fact]
    public async Task MergesBoundedGapWithoutWritingGapBytesToTheTarget()
    {
        using var bridge = new AssetsManagerTestBridge();
        (RmanManifest manifest, byte[] contiguousBundle) = CreateManifest("first"u8.ToArray(), "second"u8.ToArray());
        const int gapSize = 64 * 1024;
        byte[] bundleWithGap = AddUniformBundleGaps(manifest, contiguousBundle, gapSize);

        var handler = new RangeBundleHandler(bundleWithGap);
        using var httpClient = new HttpClient(handler);
        using var downloader = new ManifestDownloader(httpClient, bridge.LogService, bridge.Directories, new HashService());
        string output = bridge.CreateDirectory("client");

        Assert.Equal(1, await downloader.DownloadManifestAsync(manifest, output));
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(contiguousBundle.Length + gapSize, handler.BytesServed);
        Assert.Equal("firstsecond"u8.ToArray(), await File.ReadAllBytesAsync(Path.Combine(output, "Data", "test.wad.client")));
    }

    [Fact]
    public async Task DoesNotMergeGapAboveThePerGapLimit()
    {
        using var bridge = new AssetsManagerTestBridge();
        (RmanManifest manifest, byte[] contiguousBundle) = CreateManifest("first"u8.ToArray(), "second"u8.ToArray());
        byte[] bundleWithGap = AddUniformBundleGaps(manifest, contiguousBundle, 64 * 1024 + 1);
        var handler = new RangeBundleHandler(bundleWithGap);
        using var httpClient = new HttpClient(handler);
        using var downloader = new ManifestDownloader(httpClient, bridge.LogService, bridge.Directories, new HashService());
        string output = bridge.CreateDirectory("client");

        Assert.Equal(1, await downloader.DownloadManifestAsync(manifest, output));
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(contiguousBundle.Length, handler.BytesServed);
    }

    [Fact]
    public async Task CapsAccumulatedGapBytesToPreventUnboundedRangeChaining()
    {
        using var bridge = new AssetsManagerTestBridge();
        byte[][] payloads = Enumerable.Range(0, 6).Select(i => new[] { (byte)i }).ToArray();
        (RmanManifest manifest, byte[] contiguousBundle) = CreateManifest(payloads);
        const int gapSize = 64 * 1024;
        byte[] bundleWithGaps = AddUniformBundleGaps(manifest, contiguousBundle, gapSize);
        var handler = new RangeBundleHandler(bundleWithGaps);
        using var httpClient = new HttpClient(handler);
        using var downloader = new ManifestDownloader(httpClient, bridge.LogService, bridge.Directories, new HashService());
        string output = bridge.CreateDirectory("client");

        Assert.Equal(1, await downloader.DownloadManifestAsync(manifest, output));
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(contiguousBundle.Length + (4L * gapSize), handler.BytesServed);
        Assert.Equal(payloads.SelectMany(payload => payload), await File.ReadAllBytesAsync(Path.Combine(output, "Data", "test.wad.client")));
    }

    [Fact]
    public async Task DownloadsVerifiesAndRepairsOnlyTheCorruptedChunk()
    {
        using var bridge = new AssetsManagerTestBridge();
        byte[] first = "first manifest chunk"u8.ToArray();
        byte[] second = "second manifest chunk"u8.ToArray();
        (RmanManifest manifest, byte[] bundle) = CreateManifest(first, second);
        var handler = new RangeBundleHandler(bundle);
        using var httpClient = new HttpClient(handler);
        using var downloader = new ManifestDownloader(httpClient, bridge.LogService, bridge.Directories, new HashService());
        string output = bridge.CreateDirectory("client");
        string target = Path.Combine(output, "Data", "test.wad.client");

        Assert.Equal(1, await downloader.DownloadManifestAsync(manifest, output));
        Assert.Equal(first.Concat(second), await File.ReadAllBytesAsync(target));

        int requestsAfterDownload = handler.RequestCount;
        Assert.Equal(0, await downloader.DownloadManifestAsync(manifest, output));
        Assert.Equal(requestsAfterDownload, handler.RequestCount);

        await using (var stream = new FileStream(target, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            await stream.WriteAsync(new byte[] { 0xFF });
        }
        Assert.Equal(1, await downloader.DownloadManifestAsync(manifest, output));
        Assert.Equal(requestsAfterDownload, handler.RequestCount);
        Assert.Equal(first.Concat(second), await File.ReadAllBytesAsync(target));

        await using (var stream = new FileStream(target, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            stream.Position = first.Length;
            stream.WriteByte(0xFF);
        }

        Assert.Equal(1, await downloader.DownloadManifestAsync(manifest, output));
        Assert.Equal(requestsAfterDownload + 1, handler.RequestCount);
        Assert.Equal(first.Concat(second), await File.ReadAllBytesAsync(target));
    }

    [Fact]
    public async Task RefusesDownloadedContentThatDoesNotMatchTheManifestHash()
    {
        using var bridge = new AssetsManagerTestBridge();
        byte[] payload = "valid compressed payload"u8.ToArray();
        (RmanManifest manifest, _) = CreateManifest(payload);
        (_, byte[] bundle) = CreateManifest("xalid compressed payload"u8.ToArray());
        manifest.Bundles[0].Chunks[0].CompressedSize = (uint)bundle.Length;
        var handler = new RangeBundleHandler(bundle);
        using var httpClient = new HttpClient(handler);
        using var downloader = new ManifestDownloader(httpClient, bridge.LogService, bridge.Directories, new HashService());
        string output = bridge.CreateDirectory("client");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => downloader.DownloadManifestAsync(manifest, output));

        Assert.False(File.Exists(Path.Combine(output, "Data", "test.wad.client")));
    }

    [Fact]
    public async Task RejectsManifestPathsOutsideTheTargetDirectory()
    {
        using var bridge = new AssetsManagerTestBridge();
        (RmanManifest manifest, byte[] bundle) = CreateManifest("payload"u8.ToArray());
        manifest.Files[0].Name = "../escaped.wad";
        var handler = new RangeBundleHandler(bundle);
        using var httpClient = new HttpClient(handler);
        using var downloader = new ManifestDownloader(httpClient, bridge.LogService, bridge.Directories, new HashService());
        string output = bridge.CreateDirectory("client");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => downloader.DownloadManifestAsync(manifest, output));

        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task CancellationStopsAnActiveBundleRequestWithoutCreatingTheTarget()
    {
        using var bridge = new AssetsManagerTestBridge();
        (RmanManifest manifest, _) = CreateManifest("payload"u8.ToArray());
        var handler = new BlockingBundleHandler();
        using var httpClient = new HttpClient(handler);
        using var downloader = new ManifestDownloader(httpClient, bridge.LogService, bridge.Directories, new HashService());
        using var cancellation = new CancellationTokenSource();
        string output = bridge.CreateDirectory("client");

        Task<int> download = downloader.DownloadManifestAsync(manifest, output, cancellationToken: cancellation.Token);
        await handler.Entered.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        Assert.False(File.Exists(Path.Combine(output, "Data", "test.wad.client")));
    }

    private static (RmanManifest Manifest, byte[] Bundle) CreateManifest(params byte[][] payloads)
    {
        const ulong bundleId = 0x0123456789ABCDEF;
        var manifest = new RmanManifest();
        var bundle = new RmanBundle { BundleId = bundleId };
        var file = new RmanFile
        {
            Name = "Data/test.wad.client",
            HashType = HashType.Sha256
        };
        using var bundleStream = new MemoryStream();
        using var compressor = new Compressor();
        uint bundleOffset = 0;

        foreach (byte[] payload in payloads)
        {
            byte[] compressed = compressor.Wrap(payload).ToArray();
            ulong chunkId = ComputeSha256ChunkId(payload);
            bundle.Chunks.Add(new RmanChunk
            {
                ChunkId = chunkId,
                BundleId = bundleId,
                BundleOffset = bundleOffset,
                CompressedSize = (uint)compressed.Length,
                UncompressedSize = (uint)payload.Length
            });
            file.ChunkIds.Add(chunkId);
            file.FileSize += (ulong)payload.Length;
            bundleStream.Write(compressed);
            bundleOffset += (uint)compressed.Length;
        }

        manifest.Bundles.Add(bundle);
        manifest.Files.Add(file);
        manifest.BuildChunkLookup();
        return (manifest, bundleStream.ToArray());
    }

    private static ulong ComputeSha256ChunkId(ReadOnlySpan<byte> payload)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(payload, hash);
        return BinaryPrimitives.ReadUInt64LittleEndian(hash);
    }

    private static byte[] AddUniformBundleGaps(RmanManifest manifest, byte[] contiguousBundle, int gapSize)
    {
        RmanChunk[] chunks = manifest.Bundles[0].Chunks.ToArray();
        byte[] result = new byte[contiguousBundle.Length + (gapSize * (chunks.Length - 1))];
        for (int i = 0; i < chunks.Length; i++)
        {
            RmanChunk chunk = chunks[i];
            int originalOffset = checked((int)chunk.BundleOffset);
            int newOffset = checked(originalOffset + (gapSize * i));
            contiguousBundle.AsSpan(originalOffset, checked((int)chunk.CompressedSize)).CopyTo(result.AsSpan(newOffset));
            chunk.BundleOffset = checked((uint)newOffset);
        }

        return result;
    }

    private sealed class RangeBundleHandler(byte[] bundle) : HttpMessageHandler
    {
        private int _requestCount;
        private long _bytesServed;

        public int RequestCount => Volatile.Read(ref _requestCount);
        public long BytesServed => Interlocked.Read(ref _bytesServed);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            RangeItemHeaderValue range = Assert.Single(request.Headers.Range!.Ranges);
            int start = checked((int)range.From!.Value);
            int end = checked((int)range.To!.Value);
            byte[] content = bundle.AsSpan(start, end - start + 1).ToArray();
            Interlocked.Add(ref _bytesServed, content.Length);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(content)
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, bundle.Length);
            return Task.FromResult(response);
        }
    }

    private sealed class BlockingBundleHandler : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The request should have been cancelled.");
        }
    }
}
