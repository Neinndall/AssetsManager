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
            VfxBlendModeDescriptor descriptor = VfxBlendModes.GetDescriptor(rawMode);
            Assert.Equal(rawMode, descriptor.RawMode);
            Assert.Equal(expected, descriptor.Kind);
            Assert.Equal(expected, VfxBlendModes.Resolve(rawMode));
            Assert.True(VfxBlendModes.IsKnown(rawMode));
        }

        [Fact]
        public void DescriptorsOwnSeparateRgbAndAlphaBlendState()
        {
            VfxBlendModeDescriptor alpha = VfxBlendModes.GetDescriptor(0);
            Assert.Equal(VfxBlendFactor.SourceAlpha, alpha.SourceRgb);
            Assert.Equal(VfxBlendFactor.OneMinusSourceAlpha, alpha.DestinationRgb);
            Assert.Equal(VfxBlendFactor.One, alpha.SourceAlpha);
            Assert.Equal(VfxBlendFactor.OneMinusSourceAlpha, alpha.DestinationAlpha);

            VfxBlendModeDescriptor additive = VfxBlendModes.GetDescriptor(4);
            Assert.Equal(VfxBlendFactor.SourceAlpha, additive.SourceRgb);
            Assert.Equal(VfxBlendFactor.One, additive.DestinationRgb);
            Assert.False(additive.AllowsDepthWrite);

            VfxBlendModeDescriptor multiply = VfxBlendModes.GetDescriptor(3);
            Assert.Equal(VfxBlendFactor.DestinationColor, multiply.SourceRgb);
            Assert.Equal(VfxBlendFactor.Zero, multiply.DestinationRgb);
            Assert.True(multiply.NeutralizeTransparentRgb);
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
        [InlineData(1, 5, false)]
        [InlineData(2, 5, false)]
        [InlineData(3, 5, false)]
        public void TransparentParticlesNeverInferDepthWriteFromAlphaReference(int rawMode, int alphaReference, bool expected)
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
            VfxBlendModeDescriptor descriptor = VfxBlendModes.GetDescriptor(255);
            Assert.Equal(-1, descriptor.RawMode);
            Assert.Equal(VfxBlendModeKind.Alpha, descriptor.Kind);
            Assert.Contains("safe alpha fallback", VfxBlendModes.Describe(255));
        }
    }
}
