using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx
{
    public sealed class VfxBlendModeTests
    {
        [Theory]
        [InlineData(0, VfxBlendModeKind.Additive)]
        [InlineData(1, VfxBlendModeKind.Alpha)]
        [InlineData(2, VfxBlendModeKind.Multiply)]
        [InlineData(3, VfxBlendModeKind.Opaque)]
        [InlineData(4, VfxBlendModeKind.Additive)]
        [InlineData(5, VfxBlendModeKind.Alpha)]
        [InlineData(6, VfxBlendModeKind.Additive)]
        [InlineData(7, VfxBlendModeKind.Additive)]
        [InlineData(8, VfxBlendModeKind.Alpha)]
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
            VfxBlendModeDescriptor add = VfxBlendModes.GetDescriptor(0);
            Assert.Equal(VfxBlendFactor.One, add.SourceRgb);
            Assert.Equal(VfxBlendFactor.One, add.DestinationRgb);
            Assert.Equal(VfxBlendFactor.One, add.SourceAlpha);
            Assert.Equal(VfxBlendFactor.One, add.DestinationAlpha);

            VfxBlendModeDescriptor alpha = VfxBlendModes.GetDescriptor(1);
            Assert.Equal(VfxBlendFactor.SourceAlpha, alpha.SourceRgb);
            Assert.Equal(VfxBlendFactor.OneMinusSourceAlpha, alpha.DestinationRgb);
            Assert.Equal(VfxBlendFactor.One, alpha.SourceAlpha);
            Assert.Equal(VfxBlendFactor.OneMinusSourceAlpha, alpha.DestinationAlpha);

            VfxBlendModeDescriptor subtract = VfxBlendModes.GetDescriptor(2);
            Assert.Equal(VfxBlendFactor.Zero, subtract.SourceRgb);
            Assert.Equal(VfxBlendFactor.OneMinusSourceColor, subtract.DestinationRgb);
            Assert.True(subtract.NeutralizeTransparentRgb);

            VfxBlendModeDescriptor none = VfxBlendModes.GetDescriptor(3);
            Assert.True(none.AllowsDepthWrite);

            VfxBlendModeDescriptor alphaAdd = VfxBlendModes.GetDescriptor(4);
            Assert.Equal(VfxBlendFactor.SourceAlpha, alphaAdd.SourceRgb);
            Assert.Equal(VfxBlendFactor.One, alphaAdd.DestinationRgb);
            Assert.False(alphaAdd.AllowsDepthWrite);
        }

        [Theory]
        [InlineData(0, true, 1f)]
        [InlineData(1, false, 1f)]
        [InlineData(2, false, 1f)]
        [InlineData(3, false, 1f)]
        [InlineData(4, true, 1f)]
        [InlineData(5, false, 1f)]
        [InlineData(6, true, 1f)]
        [InlineData(7, true, 1f)]
        [InlineData(8, false, 1f)]
        public void ResolvesAdditiveMaterialSemantics(int rawMode, bool expectedAdditive, float expectedEmissiveStrength)
        {
            Assert.Equal(expectedAdditive, VfxBlendModes.IsAdditive(rawMode));
            Assert.Equal(expectedEmissiveStrength, VfxBlendModes.ResolveEmissiveStrength(rawMode));
        }

        [Theory]
        [InlineData(1, 0, false)]
        [InlineData(1, 5, true)]
        [InlineData(0, 5, false)]
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
        [InlineData(3, 0, true)]
        [InlineData(3, 5, true)]
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
