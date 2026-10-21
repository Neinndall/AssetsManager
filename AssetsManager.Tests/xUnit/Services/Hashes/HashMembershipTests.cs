using System;
using System.IO;
using System.Linq;
using AssetsManager.Services.Hashes;
using AssetsManager.Tests.xUnit.Infrastructure;
using AssetsManager.Utils;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Hashes;

public sealed class HashMembershipTests
{
    [Fact]
    public void ContainsPreservesResolutionForBoundariesGapsAndUnicodePaths()
    {
        using var bridge = new AssetsManagerTestBridge();
        string path = Path.Combine(bridge.RootPath, "catalog.txt");
        ulong[] keys = Enumerable.Range(1, 4096).Select(i => (ulong)i * 2)
            .Prepend(0UL).Append(ulong.MaxValue).ToArray();
        File.WriteAllLines(path, keys.Reverse().Select(hash => $"{hash:x16} assets/é/路径/{hash}.tex"));
        using var cache = new BinaryHashCache(path, bridge.LogService);
        Assert.False(cache.Contains(0));
        Assert.Null(cache.Resolve(0));
        cache.Load();
        foreach (ulong hash in keys)
        {
            Assert.True(cache.Contains(hash));
            Assert.Equal($"assets/é/路径/{hash}.tex", cache.Resolve(hash));
        }
        foreach (ulong hash in Enumerable.Range(0, 4097).Select(i => (ulong)i * 2 + 1).Append(ulong.MaxValue - 1))
        {
            Assert.False(cache.Contains(hash));
            Assert.Null(cache.Resolve(hash));
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void ResolverPreservesBothDomainsAndUnknowns(bool game, bool lcu)
    {
        using var bridge = new AssetsManagerTestBridge();
        bridge.Directories.CreateHashesDirectories();
        if (game) File.WriteAllText(Path.Combine(bridge.Directories.HashesPath, "hashes.game.txt"),
            "0000000000000000 game/zero.bin\n0000000000000002 game/two.bin\nffffffffffffffff game/max.bin\n");
        if (lcu) File.WriteAllText(Path.Combine(bridge.Directories.HashesPath, "hashes.lcu.txt"),
            "0000000000000001 lcu/one.json\n0000000000000002 lcu/two.json\n");
        using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);
        resolver.LoadHashes();
        Assert.Equal(game, resolver.IsKnownHash(0));
        Assert.Equal(lcu, resolver.IsKnownHash(1));
        Assert.Equal(game || lcu, resolver.IsKnownHash(2));
        Assert.Equal(game, resolver.IsKnownHash(ulong.MaxValue));
        Assert.False(resolver.IsKnownHash(3));
        Assert.Equal(game ? "game/two.bin" : lcu ? "lcu/two.json" : "0000000000000002", resolver.ResolveHash(2));
    }
}
