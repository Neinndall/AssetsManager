using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Core;
using Xunit;

namespace AssetsManager.BenchmarkTests.Services.Core
{
    public sealed class AssetMemoryCacheServiceTests
    {
        [Fact]
        public void ByteTierEvictsTheLeastRecentlyUsedEntryBySize()
        {
            var cache = new AssetMemoryCacheService(10, 0, 0);
            cache.SetBytes("first", new byte[4]);
            cache.SetBytes("second", new byte[4]);
            Assert.True(cache.TryGetBytes("first", out _));

            cache.SetBytes("third", new byte[4]);

            Assert.False(cache.TryGetBytes("second", out _));
            Assert.True(cache.TryGetBytes("first", out _));
            Assert.True(cache.TryGetBytes("third", out _));
            var snapshot = cache.GetSnapshot().Bytes;
            Assert.Equal(8, snapshot.UsedBytes);
            Assert.Equal(2, snapshot.Count);
            Assert.Equal(1, snapshot.Evictions);
        }

        [Fact]
        public void EntryLargerThanItsTierBudgetIsNotRetained()
        {
            var cache = new AssetMemoryCacheService(4, 0, 0);
            cache.SetBytes("oversized", new byte[5]);

            Assert.False(cache.TryGetBytes("oversized", out _));
            Assert.Equal(0, cache.GetSnapshot().Bytes.UsedBytes);
        }

        [Fact]
        public void TextTierUsesUtf16MemoryWeightAndContentIdentity()
        {
            var cache = new AssetMemoryCacheService(0, 0, 8);
            byte[] firstInput = { 1 };
            byte[] secondInput = { 2 };
            string firstKey = cache.CreateTextKey("json", firstInput);
            string secondKey = cache.CreateTextKey("json", secondInput);
            cache.SetText(firstKey, "1234");

            Assert.True(cache.TryGetText(firstKey, out string value));
            Assert.Equal("1234", value);

            cache.SetText(secondKey, "5678");

            Assert.False(cache.TryGetText(firstKey, out _));
            Assert.True(cache.TryGetText(secondKey, out _));
            Assert.Equal(8, cache.GetSnapshot().Text.UsedBytes);
        }

        [Fact]
        public void ImageTierUsesDecodedPixelSizeAndSeparatesThumbnailDimensions()
        {
            var cache = new AssetMemoryCacheService(0, 128, 0);
            byte[] encodedData = { 1, 2, 3 };
            var bitmap = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, new byte[64], 16);
            bitmap.Freeze();
            string fullKey = cache.CreateImageKey(encodedData, ".png", 0, 0);
            string thumbnailKey = cache.CreateImageKey(encodedData, ".png", 2, 2);

            cache.SetImage(fullKey, bitmap);

            Assert.True(cache.TryGetImage(fullKey, out var cached));
            Assert.Same(bitmap, cached);
            Assert.False(cache.TryGetImage(thumbnailKey, out _));
            Assert.Equal(64, cache.GetSnapshot().Images.UsedBytes);
        }

        [Fact]
        public async Task ConcurrentAccessKeepsUsageWithinBudget()
        {
            const int budget = 1024;
            var cache = new AssetMemoryCacheService(budget, 0, 0);

            await Task.WhenAll(Enumerable.Range(0, 100).Select(index => Task.Run(() =>
            {
                string key = (index % 20).ToString();
                cache.SetBytes(key, new byte[128]);
                cache.TryGetBytes(key, out _);
            })));

            var snapshot = cache.GetSnapshot().Bytes;
            Assert.InRange(snapshot.UsedBytes, 0, budget);
            Assert.InRange(snapshot.Count, 0, budget / 128);
        }
    }
}
