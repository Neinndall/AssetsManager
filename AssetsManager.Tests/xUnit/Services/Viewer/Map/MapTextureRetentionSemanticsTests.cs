using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Map
{
    public sealed class MapTextureRetentionSemanticsTests
    {
        [Theory]
        [InlineData(32, true)]
        [InlineData(64, false)]
        [InlineData(512, false)]
        [InlineData(1024, true)]
        [InlineData(2048, true)]
        public void PreviewSharpenRuleMatchesCurrentUpstream(int landedWidth, bool satisfiesFull)
        {
            Assert.Equal(satisfiesFull, MapTextureLoadingService.PreviewSatisfiesFull(Image(landedWidth)));
        }

        [Fact]
        public void PhysicalAssetCacheIdentityChangesWhenSourceChanges()
        {
            string path = Path.Combine(Path.GetTempPath(), $"am-map-cache-{Guid.NewGuid():N}.tex");
            try
            {
                File.WriteAllBytes(path, new byte[] { 1 });
                MapResolvedAsset asset = MapResolvedAsset.FromPhysical(
                    "assets/maps/cache.tex",
                    path,
                    MapAssetOrigin.ProjectFile);
                MapResolvedAssetCacheKey first = MapResolvedAssetCacheKey.From(asset);

                File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
                MapResolvedAssetCacheKey second = MapResolvedAssetCacheKey.From(asset);

                Assert.NotEqual(first, second);
                Assert.NotEqual(first.SourceLength, second.SourceLength);
            }
            finally
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        [Fact]
        public void PhysicalAssetCacheIdentityChangesWhenSameSizedSourceIsRewritten()
        {
            string path = Path.Combine(Path.GetTempPath(), $"am-map-cache-time-{Guid.NewGuid():N}.tex");
            try
            {
                File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
                DateTime firstWrite = DateTime.UtcNow.AddMinutes(-2);
                File.SetLastWriteTimeUtc(path, firstWrite);
                MapResolvedAsset asset = MapResolvedAsset.FromPhysical(
                    "assets/maps/cache.tex",
                    path,
                    MapAssetOrigin.ProjectFile);
                MapResolvedAssetCacheKey first = MapResolvedAssetCacheKey.From(asset);

                File.WriteAllBytes(path, new byte[] { 4, 5, 6 });
                File.SetLastWriteTimeUtc(path, firstWrite.AddMinutes(1));
                MapResolvedAssetCacheKey second = MapResolvedAssetCacheKey.From(asset);

                Assert.Equal(first.SourceLength, second.SourceLength);
                Assert.NotEqual(first, second);
                Assert.NotEqual(first.SourceLastWriteTimeUtcTicks, second.SourceLastWriteTimeUtcTicks);
            }
            finally
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        [Fact]
        public void PhysicalAssetCacheIdentityUsesResolvedFileRatherThanVirtualAlias()
        {
            string path = Path.Combine(Path.GetTempPath(), $"am-map-cache-alias-{Guid.NewGuid():N}.tex");
            try
            {
                File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
                MapResolvedAsset first = MapResolvedAsset.FromPhysical(
                    "assets/maps/first.tex",
                    path,
                    MapAssetOrigin.ProjectFile);
                MapResolvedAsset second = MapResolvedAsset.FromPhysical(
                    "assets/maps/alias.tex",
                    path,
                    MapAssetOrigin.SelectedFile);

                Assert.Equal(MapResolvedAssetCacheKey.From(first), MapResolvedAssetCacheKey.From(second));
            }
            finally
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        [Fact]
        public void WadAssetCacheIdentityUsesWadAndChunkRatherThanVirtualAlias()
        {
            string wad = Path.Combine(Path.GetTempPath(), $"am-map-cache-{Guid.NewGuid():N}.wad.client");
            try
            {
                File.WriteAllBytes(wad, new byte[] { 1, 2, 3 });
                const ulong chunk = 0x123456789abcdef0UL;
                MapResolvedAsset first = MapResolvedAsset.FromWad("assets/maps/first.tex", wad, chunk);
                MapResolvedAsset second = MapResolvedAsset.FromWad("assets/maps/alias.tex", wad, chunk);
                MapResolvedAsset otherChunk = MapResolvedAsset.FromWad("assets/maps/other.tex", wad, chunk + 1);

                Assert.Equal(MapResolvedAssetCacheKey.From(first), MapResolvedAssetCacheKey.From(second));
                Assert.NotEqual(MapResolvedAssetCacheKey.From(first), MapResolvedAssetCacheKey.From(otherChunk));
            }
            finally
            {
                if (File.Exists(wad))
                    File.Delete(wad);
            }
        }

        private static MapTextureImage Image(int width)
        {
            BitmapSource bitmap = BitmapSource.Create(
                width,
                1,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                new byte[checked(width * 4)],
                checked(width * 4));
            bitmap.Freeze();
            return new MapTextureImage(new[] { bitmap });
        }
    }
}
