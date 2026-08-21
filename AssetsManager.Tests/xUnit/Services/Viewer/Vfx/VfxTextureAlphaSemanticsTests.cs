using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Viewer.Vfx.Semantics;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx
{
    public sealed class VfxTextureAlphaSemanticsTests
    {
        [Fact]
        public void AttachedMeshMaskUsesDarkRgbAsOpacity()
        {
            BitmapSource texture = CreateTexture(
                0, 0, 0, 255,
                255, 255, 255, 255);

            Assert.True(VfxTextureAlphaSemantics.ShouldDeriveAlphaFromRgb(
                texture,
                blendMode: 1,
                VfxPrimitiveKind.AttachedMesh));
        }

        [Fact]
        public void OpaqueMeshMaskUsesDarkRgbAsOpacity()
        {
            BitmapSource texture = CreateTexture(
                0, 0, 0, 255,
                255, 255, 255, 255);

            Assert.True(VfxTextureAlphaSemantics.ShouldDeriveAlphaFromRgb(
                texture,
                blendMode: 1,
                VfxPrimitiveKind.Mesh));
        }

        [Fact]
        public void OpaqueGradientTexturePreservesAuthoredAlpha()
        {
            BitmapSource texture = CreateTexture(
                82, 81, 82, 255,
                247, 251, 247, 255);

            Assert.False(VfxTextureAlphaSemantics.ShouldDeriveAlphaFromRgb(
                texture,
                blendMode: 1,
                VfxPrimitiveKind.ArbitraryQuad));
        }

        [Fact]
        public void OpaqueBillboardMaskUsesDarkRgbAsOpacity()
        {
            BitmapSource texture = CreateTexture(
                0, 0, 0, 255,
                255, 255, 255, 255);

            Assert.True(VfxTextureAlphaSemantics.ShouldDeriveAlphaFromRgb(
                texture,
                blendMode: 1,
                VfxPrimitiveKind.ArbitraryQuad));
        }

        [Fact]
        public void AuthoredAlphaChannelIsPreserved()
        {
            BitmapSource texture = CreateTexture(
                0, 0, 0, 0,
                255, 255, 255, 255);

            Assert.False(VfxTextureAlphaSemantics.ShouldDeriveAlphaFromRgb(
                texture,
                blendMode: 1,
                VfxPrimitiveKind.CameraQuad));
        }

        [Fact]
        public void CompressedNearOpaqueAlphaUsesRgbOpacity()
        {
            byte[] pixels = new byte[4 * 100];
            for (int i = 0; i < 100; i++)
            {
                bool compressedSample = i < 2;
                pixels[i * 4] = compressedSample ? (byte)0 : byte.MaxValue;
                pixels[i * 4 + 1] = compressedSample ? (byte)0 : byte.MaxValue;
                pixels[i * 4 + 2] = compressedSample ? (byte)0 : byte.MaxValue;
                pixels[i * 4 + 3] = compressedSample ? (byte)225 : byte.MaxValue;
            }

            BitmapSource texture = CreateTexture(pixels);

            Assert.True(VfxTextureAlphaSemantics.ShouldDeriveAlphaFromRgb(
                texture,
                blendMode: 1,
                VfxPrimitiveKind.CameraQuad));
        }

        [Fact]
        public void MeaningfulPartialAlphaIsPreserved()
        {
            BitmapSource texture = CreateTexture(
                0, 0, 0, 128,
                255, 255, 255, 255);

            Assert.False(VfxTextureAlphaSemantics.ShouldDeriveAlphaFromRgb(
                texture,
                blendMode: 1,
                VfxPrimitiveKind.CameraQuad));
        }

        [Fact]
        public void AdditiveBillboardMaskUsesDarkRgbAsOpacity()
        {
            BitmapSource texture = CreateTexture(
                0, 0, 0, 255,
                255, 255, 255, 255);

            Assert.True(VfxTextureAlphaSemantics.ShouldDeriveAlphaFromRgb(
                texture,
                blendMode: 4,
                VfxPrimitiveKind.CameraQuad));
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
