using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Map
{
    public sealed class MapTextureDecodeCacheTests
    {
        [Fact]
        public async Task ConcurrentRequestsForSameAssetAndWidthShareOneDecode()
        {
            using var cache = new MapTextureDecodeCache();
            MapResolvedAsset asset = Asset("same.tex");
            MapTextureImage expected = Image(64);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int calls = 0;

            async Task<MapTextureImage> Decode()
            {
                Interlocked.Increment(ref calls);
                await release.Task;
                return expected;
            }

            Task<MapTextureImage> first = cache.GetOrLoadAsync(asset, 64, Decode, CancellationToken.None);
            Task<MapTextureImage> second = cache.GetOrLoadAsync(asset, 64, Decode, CancellationToken.None);
            release.SetResult();

            MapTextureImage[] images = await Task.WhenAll(first, second);

            Assert.Equal(1, calls);
            Assert.Same(expected, images[0]);
            Assert.Same(expected, images[1]);
        }

        [Fact]
        public async Task CompletedDecodeCanBePeekedWithoutCreatingAnotherLoad()
        {
            using var cache = new MapTextureDecodeCache();
            MapResolvedAsset asset = Asset("full.tex");
            MapTextureImage full = Image(1024);
            int calls = 0;

            await cache.GetOrLoadAsync(
                asset,
                1024,
                () =>
                {
                    calls++;
                    return Task.FromResult(full);
                },
                CancellationToken.None);

            Assert.True(cache.TryGetCompleted(asset, 1024, out MapTextureImage completed));
            Assert.Same(full, completed);
            Assert.Equal(1, calls);
        }

        [Fact]
        public async Task DifferentRequestedWidthsRemainSeparateDecodeKeys()
        {
            using var cache = new MapTextureDecodeCache();
            MapResolvedAsset asset = Asset("sharpen.tex");
            int calls = 0;

            MapTextureImage preview = await cache.GetOrLoadAsync(
                asset,
                64,
                () =>
                {
                    calls++;
                    return Task.FromResult(Image(64));
                },
                CancellationToken.None);
            MapTextureImage full = await cache.GetOrLoadAsync(
                asset,
                1024,
                () =>
                {
                    calls++;
                    return Task.FromResult(Image(1024));
                },
                CancellationToken.None);

            Assert.Equal(2, calls);
            Assert.Equal(64, preview.BaseLevel.PixelWidth);
            Assert.Equal(1024, full.BaseLevel.PixelWidth);
        }

        [Fact]
        public async Task RuntimeHoldKeepsDecodedPixelsPastGraceAndReleaseStartsExpiry()
        {
            using var cache = new MapTextureDecodeCache(TimeSpan.FromMilliseconds(40));
            MapResolvedAsset asset = Asset("held.tex");
            MapTextureImage image = Image(64);

            await cache.GetOrLoadAsync(asset, 64, () => Task.FromResult(image), CancellationToken.None);
            Action release = cache.Hold(image);

            await Task.Delay(120);
            Assert.True(cache.TryGetCompleted(asset, 64, out MapTextureImage held));
            Assert.Same(image, held);

            release();
            Assert.True(SpinWait.SpinUntil(
                () => !cache.TryGetCompleted(asset, 64, out _),
                TimeSpan.FromSeconds(1)));
        }

        [Fact]
        public async Task CancellingOneWaiterDoesNotCancelSharedDecode()
        {
            using var cache = new MapTextureDecodeCache();
            MapResolvedAsset asset = Asset("cancel.tex");
            MapTextureImage expected = Image(64);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = new CancellationTokenSource();
            int calls = 0;

            async Task<MapTextureImage> Decode()
            {
                Interlocked.Increment(ref calls);
                await release.Task;
                return expected;
            }

            Task<MapTextureImage> cancelled = cache.GetOrLoadAsync(asset, 64, Decode, cancellation.Token);
            Task<MapTextureImage> survivor = cache.GetOrLoadAsync(asset, 64, Decode, CancellationToken.None);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

            release.SetResult();
            MapTextureImage image = await survivor;

            Assert.Equal(1, calls);
            Assert.Same(expected, image);
        }

        [Fact]
        public async Task ExpiredInFlightDecodeDoesNotLeaveReverseImageIndexBehind()
        {
            using var cache = new MapTextureDecodeCache(TimeSpan.FromMilliseconds(30));
            MapResolvedAsset asset = Asset("abandoned.tex");
            MapTextureImage expected = Image(64);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = new CancellationTokenSource();

            Task<MapTextureImage> abandoned = cache.GetOrLoadAsync(
                asset,
                64,
                async () =>
                {
                    await release.Task;
                    return expected;
                },
                cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
            await Task.Delay(100);

            release.SetResult();
            Assert.True(SpinWait.SpinUntil(() => cache.TrackedImageCount == 0, TimeSpan.FromSeconds(1)));
            Assert.False(cache.TryGetCompleted(asset, 64, out _));
        }

        private static MapResolvedAsset Asset(string fileName) =>
            MapResolvedAsset.FromPhysical(
                $"assets/maps/{fileName}",
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), fileName),
                MapAssetOrigin.ProjectFile);

        private static MapTextureImage Image(int width)
        {
            byte[] pixels = new byte[checked(width * 4)];
            BitmapSource bitmap = BitmapSource.Create(
                width,
                1,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                pixels,
                checked(width * 4));
            bitmap.Freeze();
            return new MapTextureImage(new[] { bitmap });
        }
    }
}
