using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Map
{
    public sealed class MapCharacterLoadingServiceTests
    {
        [Fact]
        public void BareSixteenDigitTextureKeyRemainsAHashOnlyAsset()
        {
            MapAssetReference reference = MapCharacterLoadingService.ReferenceFromAuthoredTexture("1234567890abcdef");

            Assert.NotNull(reference);
            Assert.Null(reference.VirtualPath);
            Assert.Equal(0x1234567890abcdeful, reference.PathHash);
        }

        [Theory]
        [InlineData("assets/characters/test/diffuse.tex")]
        [InlineData("data/characters/test/skins/skin0.bin")]
        public void AuthoredPathRemainsAPathAsset(string path)
        {
            MapAssetReference reference = MapCharacterLoadingService.ReferenceFromAuthoredTexture(path);

            Assert.NotNull(reference);
            Assert.Equal(path, reference.VirtualPath);
            Assert.Equal(0ul, reference.PathHash);
        }
    }
}
