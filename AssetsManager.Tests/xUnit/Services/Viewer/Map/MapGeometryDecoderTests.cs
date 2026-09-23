using AssetsManager.Services.Viewer.Parsing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Map
{
    public sealed class MapGeometryDecoderTests
    {
        [Theory]
        [InlineData("Maps/Test/Material", "Maps/Test/Material")]
        [InlineData("Maps/Test/Material\0", "Maps/Test/Material")]
        [InlineData("Maps/Test/Material\0\0\0", "Maps/Test/Material")]
        public void MaterialPathsDropOnlyTrailingMapgeoPadding(string authored, string expected)
        {
            Assert.Equal(expected, MapGeometryDecoder.CanonicalMaterialPath(authored));
        }
    }
}
