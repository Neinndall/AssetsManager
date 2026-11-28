using System.IO;
using System.Threading.Tasks;
using AssetsManager.Services.Explorer;
using AssetsManager.Tests.xUnit.Infrastructure;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Explorer
{
    public sealed class WadContentProviderDirectoryTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task FindNodesByVirtualPathsReturnsEmptyForNullOrBlankDirectory(string missingDir)
        {
            using var bridge = new AssetsManagerTestBridge();
            var provider = new WadContentProvider(bridge.LogService, null, bridge.Directories, null);

            var results = await provider.FindNodesByVirtualPathsAsync(new[] { "assets/icon.png" }, missingDir);

            Assert.Empty(results);
        }

        [Fact]
        public async Task FindNodesByVirtualPathsReturnsEmptyWhenDirectoryDoesNotExist()
        {
            using var bridge = new AssetsManagerTestBridge();
            var provider = new WadContentProvider(bridge.LogService, null, bridge.Directories, null);
            string nonExistent = Path.Combine(bridge.RootPath, "does-not-exist");

            var results = await provider.FindNodesByVirtualPathsAsync(new[] { "assets/icon.png" }, nonExistent);

            Assert.Empty(results);
        }
    }
}
