using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Viewer.Vfx;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.BenchmarkTests.Services.Viewer.Vfx
{
    public sealed class VfxTextureAlphaSemanticsTests
    {
        [Fact]
        public void AlphaBlendWithOpaqueDecodedChannelUsesRgbOpacity()
        {
            BitmapSource texture = CreateTexture(
                0, 0, 0, 255,
                255, 255, 255, 255);

            Assert.True(VfxTextureAlphaSemantics.ShouldDeriveAlphaFromRgb(texture, blendMode: 1));
        }

        [Fact]
        public void AuthoredAlphaChannelIsPreserved()
        {
            BitmapSource texture = CreateTexture(
                0, 0, 0, 0,
                255, 255, 255, 255);

            Assert.False(VfxTextureAlphaSemantics.ShouldDeriveAlphaFromRgb(texture, blendMode: 1));
        }

        [Fact]
        public void AdditiveTextureDoesNotReplaceAuthoredAlpha()
        {
            BitmapSource texture = CreateTexture(
                0, 0, 0, 255,
                255, 255, 255, 255);

            Assert.False(VfxTextureAlphaSemantics.ShouldDeriveAlphaFromRgb(texture, blendMode: 4));
        }

        [Theory]
        [InlineData(0, false, 0)]
        [InlineData(0, true, 1)]
        [InlineData(4, true, 5)]
        public void ParticleColorTextureEnablesMultiplySemantics(int flags, bool hasTexture, int expected)
        {
            Assert.Equal(expected, VfxBlendModes.ResolveColorRenderFlags(flags, hasTexture));
        }

        private static BitmapSource CreateTexture(params byte[] bgra)
        {
            BitmapSource texture = BitmapSource.Create(
                bgra.Length / 4,
                1,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                bgra,
                bgra.Length);
            texture.Freeze();
            return texture;
        }
    }
}
