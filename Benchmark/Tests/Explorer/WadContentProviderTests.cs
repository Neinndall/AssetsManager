using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Explorer;
using AssetsManager.Utils;
using LeagueToolkit.Core.Wad;
using Serilog;
using Xunit;

namespace AssetsManager.BenchmarkTests.Services.Explorer
{
    public sealed class WadContentProviderTests
    {
        [Fact]
        public async Task BatchLookupResolvesRequestedPathsAcrossWads()
        {
            string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            const string firstPath = "assets/test/first.json";
            const string secondPath = "assets/test/second.json";

            try
            {
                WadBuilder.Bake(
                    new[] { new WadBakeEntry(firstPath, () => new MemoryStream(Encoding.UTF8.GetBytes("first")), WadChunkCompression.None) },
                    Path.Combine(directory, "first.wad"),
                    new WadBakeSettings());
                WadBuilder.Bake(
                    new[] { new WadBakeEntry(secondPath, () => new MemoryStream(Encoding.UTF8.GetBytes("second")), WadChunkCompression.None) },
                    Path.Combine(directory, "second.wad"),
                    new WadBakeSettings());

                var provider = CreateProvider();
                var nodes = await provider.FindNodesByVirtualPathsAsync(
                    new[] { firstPath, secondPath, "assets/test/missing.json" },
                    directory);

                Assert.Equal(2, nodes.Count);
                Assert.Equal(Path.Combine(directory, "first.wad"), nodes[firstPath].SourceWadPath);
                Assert.Equal(Path.Combine(directory, "second.wad"), nodes[secondPath].SourceWadPath);
                Assert.DoesNotContain("assets/test/missing.json", nodes.Keys);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Fact]
        public async Task LookupResolvesEventHubFromDefaultAssetsWad()
        {
            string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string wadPath = Path.Combine(directory, "default-assets2.wad");

            try
            {
                WadBuilder.Bake(
                    new[] { new WadBakeEntry(RiotCatalogDefinitions.EventHubJsonPath, () => new MemoryStream(Encoding.UTF8.GetBytes("[]")), WadChunkCompression.None) },
                    wadPath,
                    new WadBakeSettings());

                var provider = CreateProvider();
                var node = await provider.FindNodeByVirtualPathAsync(RiotCatalogDefinitions.EventHubJsonPath, directory);

                Assert.NotNull(node);
                Assert.Equal(wadPath, node.SourceWadPath);
                Assert.Equal(RiotCatalogDefinitions.EventHubJsonPath, node.VirtualPath);
                Assert.Equal(Encoding.UTF8.GetBytes("[]"), await provider.GetVirtualFileBytesAsync(node));
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Fact]
        public async Task SingleLookupAcceptsBackslashSeparatedPath()
        {
            string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string wadPath = Path.Combine(directory, "paths.wad");

            try
            {
                const string virtualPath = "assets/test/first.json";
                WadBuilder.Bake(
                    new[] { new WadBakeEntry(virtualPath, () => new MemoryStream(Encoding.UTF8.GetBytes("content")), WadChunkCompression.None) },
                    wadPath,
                    new WadBakeSettings());

                var provider = CreateProvider();
                var node = await provider.FindNodeByVirtualPathAsync("assets\\test\\first.json", directory);

                Assert.NotNull(node);
                Assert.Equal(virtualPath, node.VirtualPath);
                Assert.Equal(wadPath, node.SourceWadPath);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Fact]
        public async Task BackupChunkReadingReturnsOriginalBytes()
        {
            string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            const string sourceWad = "default-assets2.wad";
            const ulong hash = 0x1234UL;
            byte[] expected = Encoding.UTF8.GetBytes("backup chunk payload");

            try
            {
                string chunkDirectory = Path.Combine(directory, "wad_chunks", "new", sourceWad);
                Directory.CreateDirectory(chunkDirectory);
                await File.WriteAllBytesAsync(Path.Combine(chunkDirectory, $"{hash:X16}.chunk"), expected);

                var provider = CreateProvider();
                byte[] actual = await provider.GetBackupChunkBytesAsync(directory, sourceWad, hash, WadChunkCompression.None, false);

                Assert.Equal(expected, actual);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Fact]
        public void GzipDecompressionReturnsOriginalBytes()
        {
            byte[] expected = Encoding.UTF8.GetBytes(new string('a', 4096));
            byte[] compressed;

            using (var stream = new MemoryStream())
            {
                using (var gzip = new GZipStream(stream, CompressionLevel.SmallestSize, leaveOpen: true))
                {
                    gzip.Write(expected);
                }

                compressed = stream.ToArray();
            }

            byte[] actual = WadChunkUtils.DecompressChunk(compressed, WadChunkCompression.GZip);

            Assert.Equal(expected, actual);
        }

        [Fact]
        public async Task BackupChunkReadingPropagatesCancellation()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var provider = CreateProvider();

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                provider.GetBackupChunkBytesAsync("unused", "unused.wad", 1, WadChunkCompression.None, false, cancellation.Token));
        }

        [Fact]
        public void MemoryGzipInputAvoidsTheSpanInputCopy()
        {
            byte[] expected = new byte[128 * 1024];
            new Random(42).NextBytes(expected);
            byte[] compressed = CompressGzip(expected);

            _ = WadChunkUtils.DecompressChunk(compressed.AsMemory(), WadChunkCompression.GZip);
            _ = WadChunkUtils.DecompressChunk(compressed.AsSpan(), WadChunkCompression.GZip);

            long memoryAllocated = MeasureAllocation(() => WadChunkUtils.DecompressChunk(compressed.AsMemory(), WadChunkCompression.GZip));
            long spanAllocated = MeasureAllocation(() => WadChunkUtils.DecompressChunk(compressed.AsSpan(), WadChunkCompression.GZip));

            Assert.True(memoryAllocated < spanAllocated - (compressed.Length / 2));
        }

        private static byte[] CompressGzip(byte[] data)
        {
            using var stream = new MemoryStream();
            using (var gzip = new GZipStream(stream, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                gzip.Write(data);
            }

            return stream.ToArray();
        }

        private static long MeasureAllocation(Func<byte[]> action)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long before = GC.GetAllocatedBytesForCurrentThread();
            byte[] result = action();
            GC.KeepAlive(result);
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        private static WadContentProvider CreateProvider()
        {
            return new WadContentProvider(new LogService(Log.Logger), null, null);
        }
    }
}
