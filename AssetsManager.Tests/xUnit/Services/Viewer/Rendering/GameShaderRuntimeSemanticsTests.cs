using AssetsManager.Shaders;
using AssetsManager.Services.Viewer.Rendering;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering
{
    public sealed class GameShaderRuntimeSemanticsTests
    {
        [Fact]
        public void BufferTexturesRequireIntegerNearestNeutralSampling()
        {
            Assert.True(GameShaderRuntime.RequiresIntegerNeutral(GameShaderTranslator.TextureDimension.Buffer));
            Assert.False(GameShaderRuntime.RequiresIntegerNeutral(GameShaderTranslator.TextureDimension.Texture2D));
            Assert.False(GameShaderRuntime.RequiresIntegerNeutral(GameShaderTranslator.TextureDimension.Cube));
        }

        [Theory]
        [InlineData(0u, false)]
        [InlineData(16u, false)]
        [InlineData(1u, true)]
        [InlineData(8u, true)]
        [InlineData(15u, true)]
        [InlineData(31u, true)]
        public void ColorWriteMatchesViewportAllOrNothingMask(uint writeMask, bool expected)
        {
            Assert.Equal(expected, GameShaderRuntime.ColorWriteEnabled(writeMask));
        }
    }
}
