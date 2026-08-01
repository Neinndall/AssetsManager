using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.BenchmarkTests.Services.Viewer.Vfx
{
    public sealed class VfxBlendModeTests
    {
        [Theory]
        [InlineData(0, VfxBlendModeKind.Additive)]
        [InlineData(1, VfxBlendModeKind.Additive)]
        [InlineData(2, VfxBlendModeKind.Alpha)]
        [InlineData(3, VfxBlendModeKind.Multiply)]
        [InlineData(4, VfxBlendModeKind.Additive)]
        [InlineData(5, VfxBlendModeKind.Additive)]
        public void ResolvesAuthoredBlendModes(int rawMode, VfxBlendModeKind expected)
        {
            Assert.Equal(expected, VfxBlendModes.Resolve(rawMode));
            Assert.True(VfxBlendModes.IsKnown(rawMode));
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
