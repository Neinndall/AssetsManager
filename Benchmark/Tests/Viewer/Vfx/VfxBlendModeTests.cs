using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.BenchmarkTests.Services.Viewer.Vfx
{
    public sealed class VfxBlendModeTests
    {
        [Theory]
        [InlineData(0, VfxBlendModeKind.Alpha)]
        [InlineData(1, VfxBlendModeKind.Alpha)]
        [InlineData(2, VfxBlendModeKind.Additive)]
        [InlineData(3, VfxBlendModeKind.Multiply)]
        [InlineData(4, VfxBlendModeKind.Additive)]
        [InlineData(5, VfxBlendModeKind.Alpha)]
        public void ResolvesAuthoredBlendModes(int rawMode, VfxBlendModeKind expected)
        {
            Assert.Equal(expected, VfxBlendModes.Resolve(rawMode));
            Assert.True(VfxBlendModes.IsKnown(rawMode));
        }

        [Theory]
        [InlineData(0, false, 1f)]
        [InlineData(1, false, 1f)]
        [InlineData(2, true, 1f)]
        [InlineData(3, false, 1f)]
        [InlineData(4, true, 1f)]
        [InlineData(5, false, 1f)]
        public void ResolvesAdditiveMaterialSemantics(int rawMode, bool expectedAdditive, float expectedEmissiveStrength)
        {
            Assert.Equal(expectedAdditive, VfxBlendModes.IsAdditive(rawMode));
            Assert.Equal(expectedEmissiveStrength, VfxBlendModes.ResolveEmissiveStrength(rawMode));
        }

        [Theory]
        [InlineData(1, 0, false)]
        [InlineData(1, 5, true)]
        [InlineData(2, 5, false)]
        [InlineData(4, 255, false)]
        [InlineData(255, 5, true)]
        public void AppliesAlphaTestOnlyToNonAdditiveModes(int rawMode, int alphaReference, bool expected)
        {
            Assert.Equal(expected, VfxBlendModes.ShouldAlphaTest(rawMode, alphaReference));
        }

        [Theory]
        [InlineData(1, 0, false)]
        [InlineData(1, 5, true)]
        [InlineData(2, 5, false)]
        [InlineData(3, 5, true)]
        public void ResolvesDepthWriteAlongsideAlphaSemantics(int rawMode, int alphaReference, bool expected)
        {
            Assert.Equal(expected, VfxBlendModes.ShouldWriteDepth(rawMode, alphaReference));
        }

        [Theory]
        [InlineData(1, 1, false, true)]
        [InlineData(3, 1, false, true)]
        [InlineData(0, 1, false, false)]
        [InlineData(1, 0, false, false)]
        [InlineData(1, 1, true, false)]
        public void ResolvesMiscRenderFaceInversion(int flags, int rawMode, bool disableCull, bool expected)
        {
            Assert.Equal(expected, VfxBlendModes.ShouldFlipFaces(flags, rawMode, disableCull));
        }

        [Fact]
        public void UnknownModesUseSafeAlphaFallback()
        {
            Assert.False(VfxBlendModes.IsKnown(255));
            Assert.Equal(VfxBlendModeKind.Alpha, VfxBlendModes.Resolve(255));
            Assert.Contains("safe alpha fallback", VfxBlendModes.Describe(255));
        }
    }
}
