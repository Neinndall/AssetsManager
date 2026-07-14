using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using AssetsManager.Services.Explorer;
using AssetsManager.Utils;
using LeagueToolkit.Core.Wad;
using Xunit;

namespace AssetsManager.BenchmarkTests.Services.Explorer
{
    public sealed class WadContentProviderTests
    {
        [Fact]
        public async Task BatchLookupResolvesRequestedPathsAcrossWads()
        {
            string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            const string firstPath = "assets/test/first.json";
            const string secondPath = "assets/test/second.json";

            try
            {
                WadBuilder.Bake(
                    new[] { new WadBakeEntry(firstPath, () => new MemoryStream(Encoding.UTF8.GetBytes("first")), WadChunkCompression.None) },
                    Path.Combine(directory, "first.wad"),
                    new WadBakeSettings());
                WadBuilder.Bake(
                    new[] { new WadBakeEntry(secondPath, () => new MemoryStream(Encoding.UTF8.GetBytes("second")), WadChunkCompression.None) },
                    Path.Combine(directory, "second.wad"),
                    new WadBakeSettings());

                var provider = new WadContentProvider(null, null, null);
                var nodes = await provider.FindNodesByVirtualPathsAsync(
                    new[] { firstPath, secondPath, "assets/test/missing.json" },
                    directory);

                Assert.Equal(2, nodes.Count);
                Assert.Equal(Path.Combine(directory, "first.wad"), nodes[firstPath].SourceWadPath);
                Assert.Equal(Path.Combine(directory, "second.wad"), nodes[secondPath].SourceWadPath);
                Assert.DoesNotContain("assets/test/missing.json", nodes.Keys);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Fact]
        public async Task LookupResolvesEventHubFromDefaultAssetsWad()
        {
            string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string wadPath = Path.Combine(directory, "default-assets2.wad");

            try
            {
                WadBuilder.Bake(
                    new[] { new WadBakeEntry(RiotCatalogDefinitions.EventHubJsonPath, () => new MemoryStream(Encoding.UTF8.GetBytes("[]")), WadChunkCompression.None) },
                    wadPath,
                    new WadBakeSettings());

                var provider = new WadContentProvider(null, null, null);
                var node = await provider.FindNodeByVirtualPathAsync(RiotCatalogDefinitions.EventHubJsonPath, directory);

                Assert.NotNull(node);
                Assert.Equal(wadPath, node.SourceWadPath);
                Assert.Equal(RiotCatalogDefinitions.EventHubJsonPath, node.VirtualPath);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
