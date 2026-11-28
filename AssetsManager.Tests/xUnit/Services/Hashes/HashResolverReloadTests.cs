using System.IO;
using System.Threading.Tasks;
using AssetsManager.Services.Hashes;
using AssetsManager.Tests.xUnit.Infrastructure;
using AssetsManager.Utils;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Hashes
{
    public sealed class HashResolverReloadTests
    {
        [Fact]
        public void DisposedBinaryHashCacheReturnsFalseAndZeroInsteadOfThrowing()
        {
            using var bridge = new AssetsManagerTestBridge();
            string hashFile = Path.Combine(bridge.Directories.HashesPath, "hashes.game.txt");
            Directory.CreateDirectory(bridge.Directories.HashesPath);
            File.WriteAllText(hashFile, "0000000000000001 assets/test.json\n");

            var cache = new BinaryHashCache(hashFile, bridge.LogService);
            cache.Load();
            Assert.Equal("assets/test.json", cache.Resolve(1UL));

            cache.Dispose();

            Assert.Null(cache.Resolve(1UL));
            Assert.Equal(0UL, cache.GetHash(0));
        }

        [Fact]
        public async Task ForceReloadAndBinRstReloadRefreshCatalogsCleanly()
        {
            using var bridge = new AssetsManagerTestBridge();
            Directory.CreateDirectory(bridge.Directories.HashesPath);
            string binFile = Path.Combine(bridge.Directories.HashesPath, "hashes.binhashes.txt");
            File.WriteAllText(binFile, "00000001 FirstName\n");

            using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);
            await resolver.ForceReloadHashesAsync();
            Assert.Equal("FirstName", resolver.ResolveBinHash(1u));

            File.WriteAllText(binFile, "00000001 UpdatedName\n");
            File.SetLastWriteTimeUtc(binFile, System.DateTime.UtcNow.AddSeconds(5));

            resolver.ReloadBinRstHashes();
            Assert.Equal("UpdatedName", resolver.ResolveBinHash(1u));
        }
    }
}
