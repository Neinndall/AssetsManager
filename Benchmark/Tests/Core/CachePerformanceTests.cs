using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Explorer;
using AssetsManager.Views.Models.Explorer;
using LeagueToolkit.Core.Wad;
using Serilog;
using Xunit;
using Xunit.Abstractions;

namespace AssetsManager.BenchmarkTests.Services.Core
{
    public sealed class CachePerformanceTests
    {
        private readonly ITestOutputHelper _output;

        public CachePerformanceTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public async Task RepeatedWadReadsMeasureWarmCacheImprovement()
        {
            const int iterations = 25;
            string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string wadPath = Path.Combine(directory, "performance.wad");
            const string virtualPath = "assets/test/performance.bin";

            try
            {
                byte[] payload = new byte[2 * 1024 * 1024];
                new Random(42).NextBytes(payload);
                WadBuilder.Bake(
                    new[] { new WadBakeEntry(virtualPath, () => new MemoryStream(payload), WadChunkCompression.None) },
                    wadPath,
                    new WadBakeSettings());

                var uncachedProvider = CreateProvider(new AssetMemoryCacheService(0, 0, 0));
                FileSystemNodeModel node = await uncachedProvider.FindNodeByVirtualPathAsync(virtualPath, directory);
                await uncachedProvider.GetVirtualFileBytesAsync(node);
                var uncached = await MeasureAsync(() => uncachedProvider.GetVirtualFileBytesAsync(node), iterations);

                var cache = new AssetMemoryCacheService(4 * 1024 * 1024, 0, 0);
                var cachedProvider = CreateProvider(cache);
                await cachedProvider.GetVirtualFileBytesAsync(node);
                var cached = await MeasureAsync(() => cachedProvider.GetVirtualFileBytesAsync(node), iterations);

                double speedup = uncached.Elapsed.TotalMilliseconds / Math.Max(cached.Elapsed.TotalMilliseconds, 0.001);
                double allocationReduction = 1d - ((double)cached.AllocatedBytes / Math.Max(uncached.AllocatedBytes, 1));
                _output.WriteLine($"Uncached: {uncached.Elapsed.TotalMilliseconds:F2} ms, {uncached.AllocatedBytes / 1024d / 1024d:F2} MiB allocated");
                _output.WriteLine($"Cached: {cached.Elapsed.TotalMilliseconds:F2} ms, {cached.AllocatedBytes / 1024d / 1024d:F2} MiB allocated");
                _output.WriteLine($"Speedup: {speedup:F2}x; allocation reduction: {allocationReduction:P2}");

                Assert.True(cached.Elapsed < uncached.Elapsed);
                Assert.Equal(iterations, cache.GetSnapshot().Bytes.Hits);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        private static async Task<(TimeSpan Elapsed, long AllocatedBytes)> MeasureAsync(Func<Task<byte[]>> action, int iterations)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long before = GC.GetTotalAllocatedBytes(true);
            var stopwatch = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                byte[] result = await action();
                GC.KeepAlive(result);
            }
            stopwatch.Stop();
            return (stopwatch.Elapsed, GC.GetTotalAllocatedBytes(true) - before);
        }

        private static WadContentProvider CreateProvider(AssetMemoryCacheService cache) =>
            new(new LogService(Log.Logger), null, null, cache);
    }
}
