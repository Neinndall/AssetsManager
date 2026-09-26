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
            Assert.Equal(VfxBlendFactor.SourceAlpha, alpha.SourceAlpha);
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
        [InlineData(0, 0, false)]
        [InlineData(0, 5, true)]
        [InlineData(1, 0, false)]
        [InlineData(1, 5, true)]
        [InlineData(2, 5, true)]
        [InlineData(3, 5, true)]
        [InlineData(4, 0, false)]
        [InlineData(4, 255, true)]
        [InlineData(6, 5, true)]
        [InlineData(7, 5, true)]
        [InlineData(8, 5, true)]
        [InlineData(255, 5, true)]
        public void EvaluatesAlphaTestAgainstAlphaReference(int rawMode, int alphaReference, bool expected)
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
        [InlineData(0, true)]
        [InlineData(1, false)]
        [InlineData(2, true)]
        [InlineData(5, false)]
        public void AuthoredDisableZBufferControlsDepthTest(int flags, bool expected)
            => Assert.Equal(expected, VfxBlendModes.ShouldTestDepth(flags));

        [Theory]
        [InlineData(0, false)] // Add
        [InlineData(1, true)]  // Alpha
        [InlineData(2, false)] // Subtract (order-independent multiplication)
        [InlineData(3, false)] // None / Opaque
        [InlineData(4, false)] // Alpha Add
        [InlineData(5, true)]  // Premultiplied Alpha
        [InlineData(6, false)] // Min
        [InlineData(7, false)] // Max
        [InlineData(8, true)]  // Target Alpha
        public void ShouldSortBackToFront_ReturnsTrueOnlyForOverBlending(int rawMode, bool expected)
            => Assert.Equal(expected, VfxBlendModes.ShouldSortBackToFront(rawMode));

        [Theory]
        [InlineData(0)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(8)]
        public void DistortionAlwaysCompositesWithAlphaBlend(int authoredMode)
        {
            var draw = VfxBlendModes.GetDrawDescriptor(authoredMode, true);
            Assert.Equal(1, draw.RawMode);
            Assert.Equal(VfxBlendFactor.SourceAlpha, draw.SourceRgb);
            Assert.Equal(VfxBlendFactor.OneMinusSourceAlpha, draw.DestinationRgb);
            Assert.False(draw.AllowsDepthWrite);
        }

        [Theory]
        [InlineData(0, 0.7f, 0.85f)]
        [InlineData(1, 0.425f, 0.5125f)]
        [InlineData(2, 0.4f, 0.45f)]
        [InlineData(3, 0.2f, 0.25f)]
        [InlineData(4, 0.55f, 0.6625f)]
        [InlineData(5, 0.575f, 0.7f)]
        [InlineData(6, 0.2f, 0.25f)]
        [InlineData(7, 0.5f, 0.6f)]
        [InlineData(8, 0.38f, 0.85f)]
        public void AuthoredBlendEquationsMatchLtkColorAndCoverage(int mode, float red, float alpha)
        {
            var d = VfxBlendModes.GetDescriptor(mode);
            Assert.Equal(red, Compose(0.2f, 0.5f, d.SourceRgb, d.DestinationRgb, d.RgbEquation), 5);
            Assert.Equal(alpha, Compose(0.25f, 0.6f, d.SourceAlpha, d.DestinationAlpha, d.AlphaEquation), 5);
        }

        private static float Compose(float source, float destination, VfxBlendFactor sf, VfxBlendFactor df, VfxBlendEquationKind equation)
        {
            if (equation == VfxBlendEquationKind.Min) return System.MathF.Min(source, destination);
            if (equation == VfxBlendEquationKind.Max) return System.MathF.Max(source, destination);
            float Factor(VfxBlendFactor f) => f switch
            {
                VfxBlendFactor.Zero => 0f, VfxBlendFactor.One => 1f,
                VfxBlendFactor.SourceAlpha => 0.25f, VfxBlendFactor.OneMinusSourceAlpha => 0.75f,
                VfxBlendFactor.DestinationAlpha => 0.6f, VfxBlendFactor.OneMinusDestinationAlpha => 0.4f,
                VfxBlendFactor.OneMinusSourceColor => 1f - source,
                _ => throw new System.InvalidOperationException()
            };
            return source * Factor(sf) + destination * Factor(df);
        }

        [Fact]
        public void UnknownModesUseLtkAddFallback()
        {
            Assert.False(VfxBlendModes.IsKnown(255));
            VfxBlendModeDescriptor descriptor = VfxBlendModes.GetDescriptor(255);
            Assert.Equal(-1, descriptor.RawMode);
            Assert.Equal(VfxBlendModeKind.Additive, descriptor.Kind);
            Assert.Equal(VfxBlendFactor.One, descriptor.SourceRgb);
            Assert.Equal(VfxBlendFactor.One, descriptor.DestinationRgb);
            Assert.Equal(VfxBlendFactor.One, descriptor.SourceAlpha);
            Assert.Equal(VfxBlendFactor.One, descriptor.DestinationAlpha);
            Assert.Contains("LTK add fallback", VfxBlendModes.Describe(255));
        }
    }
}
