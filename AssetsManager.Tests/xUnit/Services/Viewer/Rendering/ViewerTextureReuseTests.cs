using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Viewer.Rendering.Core;
using AssetsManager.Services.Viewer.Resources;
using AssetsManager.Services.Viewer.Vfx.Resources;
using AssetsManager.Tests.xUnit.Services.Viewer.Map;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering;

public sealed class ViewerTextureReuseTests
{
    [Theory]
    [InlineData(1, 1, 4)]
    [InlineData(16, 8, 684)]
    [InlineData(16, 1, 124)]
    public void IdleBudgetIncludesEveryMipOfRectangularTextures(int width, int height, long bytes)
        => Assert.Equal(bytes, GlRetainedTextureCache<object>.MipChainBytes(width, height));

    private static ViewerDecodeKey Key(string path = "same.tex", long version = 1)
        => new(new MapResolvedAssetCacheKey(path, 0, 4, version), "game-bitmap");

    [Fact]
    public async Task ConcurrentLoadsAndCancelledWaiterShareOneDecode()
    {
        using var cache = new ViewerDecodedResourceCache<object>();
        var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        int decodes = 0;
        Task<object> Load() { decodes++; return completion.Task; }
        Task<object> cancelled = cache.GetAsync(Key(), Load, _ => 4, cancellation.Token);
        Task<object> other = cache.GetAsync(Key(), Load, _ => 4);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        var image = new object();
        completion.SetResult(image);
        Assert.Same(image, await other);
        Assert.Same(image, await cache.GetAsync(Key(), Load, _ => 4));
        Assert.Equal(1, decodes);
    }

    [Fact]
    public async Task FailedAndMissingLoadsDoNotPoisonRetry()
    {
        using var cache = new ViewerDecodedResourceCache<object>();
        await Assert.ThrowsAsync<IOException>(() => cache.GetAsync(Key(), () => Task.FromException<object>(new IOException()), _ => 1));
        Assert.Null(await cache.GetAsync(Key(), () => Task.FromResult<object>(null), _ => 1));
        var image = new object();
        Assert.Same(image, cache.Get(Key(), () => image, _ => 1));
    }

    [Fact]
    public void SourceVersionResolutionAndProfileRemainIndependent()
    {
        using var cache = new ViewerDecodedResourceCache<object>();
        var keys = new[] { Key(), Key("other/same.tex"), Key(version: 2), Key() with { Width = 64 },
            Key() with { Height = 64 }, Key() with { Profile = "cube" },
            Key() with { Asset = Key().Asset with { WadPathHash = 9 } } };
        var values = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var key in keys)
        {
            object value = cache.Get(key, () => new object(), _ => 1);
            Assert.True(values.Add(value));
            Assert.Same(value, cache.Get(key, () => throw new Exception("Unexpected decode"), _ => 1));
        }
    }

    [Fact]
    public void LiveImageKeepsIdentityAfterStrongRetentionExpiresOrExceedsBudget()
    {
        long now = 0;
        using var cache = new ViewerDecodedResourceCache<object>(byteLimit: 0, clock: () => now);
        var live = cache.Get(Key(), () => new object(), _ => 1024);
        now = 30_000;
        cache.Sweep();
        Assert.Same(live, cache.Get(Key(), () => throw new Exception("Live image was decoded again"), _ => 1024));
    }

    [Fact]
    public void IdleCpuImagesAreCollectibleWhenRetentionBudgetIsExhausted()
    {
        using var cache = new ViewerDecodedResourceCache<object>(byteLimit: 0);
        WeakReference<object> weak = CreateIdleImage(cache);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(weak.TryGetTarget(out _));
        cache.Sweep();
        Assert.NotNull(cache.Get(Key(), () => new object(), _ => 4));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<object> CreateIdleImage(ViewerDecodedResourceCache<object> cache)
        => new(cache.Get(Key(), () => new object(), _ => 4));

    [Fact]
    public void IndependentVfxResolversAndSkinLoaderShareGameTextureDecodeAndObserveEdits()
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "am-vfx-reuse-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string path = Path.Combine(root, "same.tex");
            File.WriteAllBytes(path, BlockTextureDataTests.Tex(10, 16, 8));
            using var first = new VfxResourceResolver();
            using var second = new VfxResourceResolver();
            var image = first.ResolveTexture("same.tex", root);
            Assert.NotNull(image);
            Assert.Same(image, second.ResolveTexture("same.tex", root));
            Assert.Same(image, ViewerTextureLoadingCache.LoadFile(path, viewer: true));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            Assert.NotSame(image, first.ResolveTexture("same.tex", root));
            Assert.Same(first.ResolveTexture("same.tex", root), second.ResolveTexture("same.tex", root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void MaterializedSceneCopiesReuseOriginalIdentityUntilTheCopyIsEdited()
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "am-scene-texture-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string original = Path.Combine(root, "original.tex");
            File.WriteAllBytes(original, BlockTextureDataTests.Tex(10, 16, 8));
            var source = MapResolvedAssetCacheKey.From(MapResolvedAsset.FromPhysical(original, original, MapAssetOrigin.SelectedFile));
            string firstPath = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "scene1")).FullName, "same.tex");
            string secondPath = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "scene2")).FullName, "same.tex");
            File.Copy(original, firstPath);
            File.Copy(original, secondPath);
            using var first = new VfxResourceResolver();
            using var second = new VfxResourceResolver();
            first.RegisterMaterializedSource(firstPath, source);
            second.RegisterMaterializedSource(secondPath, source);
            var originalImage = ViewerTextureLoadingCache.LoadFile(original, viewer: true);
            Assert.Same(originalImage, first.ResolveTexture("same.tex", Path.GetDirectoryName(firstPath)));
            Assert.Same(originalImage, second.ResolveTexture("same.tex", Path.GetDirectoryName(secondPath)));
            File.SetLastWriteTimeUtc(firstPath, DateTime.UtcNow.AddMinutes(1));
            Assert.NotSame(originalImage, first.ResolveTexture("same.tex", Path.GetDirectoryName(firstPath)));
            Assert.Same(originalImage, second.ResolveTexture("same.tex", Path.GetDirectoryName(secondPath)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DisposingDuringDecodeDoesNotRetainOrPublishIntoDisposedCache()
    {
        var cache = new ViewerDecodedResourceCache<object>();
        var completion = new TaskCompletionSource<object>();
        var waiting = cache.GetAsync(Key(), () => completion.Task, _ => 4);
        cache.Dispose();
        completion.SetResult(new object());
        Assert.NotNull(await waiting);
        Assert.Throws<ObjectDisposedException>(() => cache.Get(Key(), () => new object(), _ => 4));
    }

    [Fact]
    public void FileReloadReusesUnchangedImageAndInvalidatesChangedVersion()
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "am-texture-reuse-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string path = Path.Combine(root, "image.png");
            WritePng(path, 20);
            BitmapSource first = ViewerTextureLoadingCache.LoadFile(path, viewer: true);
            Assert.True(first.IsFrozen);
            Assert.Same(first, ViewerTextureLoadingCache.LoadFile(path, viewer: true));
            WritePng(path, 80);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            BitmapSource changed = ViewerTextureLoadingCache.LoadFile(path, viewer: true);
            Assert.NotSame(first, changed);
            byte[] pixels = new byte[4];
            changed.CopyPixels(pixels, 4, 0);
            Assert.Equal(80, pixels[0]);
        }
        finally { Directory.Delete(root, true); }
    }

    private static void WritePng(string path, byte blue)
    {
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { blue, 0, 0, 255 }, 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    [Fact]
    public void GpuReuseSurvivesReleaseAndProtectsActiveTexturesFromBudgetAndExpiry()
    {
        long now = 0;
        int uploads = 0;
        var deleted = new List<uint>();
        using var cache = new GlRetainedTextureCache<object>(deleted.Add, byteLimit: 16, clock: () => now);
        var shared = new object();
        uint Upload() => (uint)++uploads;
        Assert.Equal(1u, cache.Acquire(shared, 16, Upload));
        cache.Release(shared);
        now = 100;
        Assert.Equal(1u, cache.Acquire(shared, 16, Upload));
        var idle = new object();
        Assert.Equal(2u, cache.Get(idle, 32, Upload));
        cache.Sweep();
        Assert.Equal(new uint[] { 2 }, deleted);
        now = 30_000;
        cache.Sweep();
        Assert.True(cache.TryGet(shared, out uint id));
        Assert.Equal(1u, id);
        cache.Release(shared);
        now += 15_000;
        cache.Sweep();
        Assert.False(cache.TryGet(shared, out _));
        Assert.Equal(new uint[] { 2, 1 }, deleted);
        Assert.Equal(2, uploads);
    }

    [Fact]
    public void GraphOwnersRetainHiddenAndDeferredHandlesUntilTheyLeaveTheSession()
    {
        long now = 0;
        var deleted = new List<uint>();
        using var cache = new GlRetainedTextureCache<object>(deleted.Add, byteLimit: 0, clock: () => now);
        var old = new object();
        cache.Get(old, 4, () => 7);
        cache.SetActive(new HashSet<uint> { 7 });
        now = 30_000;
        cache.SetActive(new HashSet<uint> { 7 });
        Assert.Empty(deleted);
        cache.SetActive(new HashSet<uint>());
        Assert.Equal(new uint[] { 7 }, deleted);
    }

    [Fact]
    public void FormatAndContextOwnersNeverShareGpuHandlesAndDisposeExactlyOnce()
    {
        var image = new object();
        var deleted = new List<uint>();
        var raw = new GlRetainedTextureCache<object>(deleted.Add);
        using var srgb = new GlRetainedTextureCache<object>(deleted.Add);
        using var otherContext = new GlRetainedTextureCache<object>(deleted.Add);
        Assert.Equal(1u, raw.Acquire(image, 4, () => 1));
        Assert.Equal(2u, srgb.Acquire(image, 4, () => 2));
        Assert.Equal(3u, otherContext.Acquire(image, 4, () => 3));
        raw.Dispose();
        raw.Dispose();
        Assert.Equal(new uint[] { 1 }, deleted);
        Assert.True(srgb.TryGet(image, out _));
        Assert.True(otherContext.TryGet(image, out _));
    }
}
