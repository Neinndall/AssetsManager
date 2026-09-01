using AssetsManager.Utils;
using Xunit;

namespace AssetsManager.Tests.xUnit.Utils
{
    public sealed class PathUtilsTests
    {
        [Theory]
        [InlineData(null, "")]
        [InlineData("", "")]
        [InlineData("Characters\\Ahri/Skins\\Skin01.bin", "Characters/Ahri/Skins/Skin01.bin")]
        [InlineData("Characters//Ahri", "Characters//Ahri")]
        public void NormalizeSeparatorsOnlyConvertsBackslashes(string input, string expected)
        {
            Assert.Equal(expected, PathUtils.NormalizeSeparators(input));
        }

        [Fact]
        public void NormalizeSeparatorsReusesForwardSlashPath()
        {
            const string path = "Characters/Ahri/Skins/Skin01.bin";

            Assert.Same(path, PathUtils.NormalizeSeparators(path));
        }

        [Fact]
        public void NormalizePathPreservesCanonicalHashBehavior()
        {
            Assert.Equal(
                "characters/ahri//skins/skin01.bin",
                PathUtils.NormalizePath("  Characters\\Ahri//Skins\\Skin01.BIN  "));
        }

        [Fact]
        public void NormalizeVirtualPathHandlesMixedSeparatorsAndSegments()
        {
            Assert.Equal(
                "Characters/Ahri/Skins/Skin01.bin",
                PathUtils.NormalizeVirtualPath("Characters\\Ahri/Shared/../Skins\\Skin01.bin"));
        }
    }
}
