using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Hashes;
using AssetsManager.Tests.xUnit.Infrastructure;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Hashes;

public sealed class HashUnknownInventoryTests
{
    [Theory]
    [InlineData(HashGuessDomain.Game, false)]
    [InlineData(HashGuessDomain.Game, true)]
    [InlineData(HashGuessDomain.Lcu, false)]
    [InlineData(HashGuessDomain.Lcu, true)]
    public async Task GuessingPreservesPendingUnknownsWithoutReadingLegacyInventory(HashGuessDomain domain, bool grep)
    {
        using var bridge = new AssetsManagerTestBridge();
        string root = bridge.CreateDirectory("client");
        var store = new HashGuessingStore(bridge.Directories);
        var pending = new HashSet<ulong> { 1, 2 };
        await store.SaveUnknownHashesAsync(domain, pending, CancellationToken.None);
        string legacyPath = Path.Combine(bridge.Directories.HashLabPath, $"inventory.{domain.ToString().ToLowerInvariant()}.json");
        await File.WriteAllTextAsync(legacyPath, "obsolete invalid JSON");
        using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);
        var service = new HashGuessingService(resolver, store,
            new HashGuessPersistenceService(store, new BinRstHashGuessingStore(bridge.Directories)), bridge.LogService, bridge.Directories);
        if (grep) await service.RunEmbeddedPathGrepAsync(domain, root, null, CancellationToken.None);
        else if (domain == HashGuessDomain.Game)
            await service.RunGameBasicGuessingAsync(root, null, CancellationToken.None, selectedSubMethods: new HashSet<string>());
        else
            await service.RunLcuBasicGuessingAsync(root, null, CancellationToken.None, selectedSubMethods: new HashSet<string>());

        var pendingHashes = await store.LoadUnknownHashesAsync(domain, CancellationToken.None);
        Assert.True(pendingHashes.SetEquals(pending));
        Assert.Equal(2, await store.LoadUnknownCountAsync(domain, CancellationToken.None));
        Assert.Equal("obsolete invalid JSON", await File.ReadAllTextAsync(legacyPath));
    }

    [Theory]
    [InlineData(HashGuessDomain.Game)]
    [InlineData(HashGuessDomain.Lcu)]
    public async Task ScanningWadsAddsNewPatchUnknownsWithoutDroppingOlderOnes(HashGuessDomain domain)
    {
        using var bridge = new AssetsManagerTestBridge();
        string root = bridge.CreateDirectory("client");
        string extension = domain == HashGuessDomain.Game ? ".wad.client" : ".wad";
        const string oldPath = "assets/previous-patch.bin", newPath = "assets/current-patch.bin", resolvedPath = "assets/resolved.bin";
        ulong oldHash = XxHash64Ext.Hash(oldPath), newHash = XxHash64Ext.Hash(newPath);
        bridge.BakeWad(root, "current" + extension, (newPath, "new payload"));
        var store = new HashGuessingStore(bridge.Directories);
        await store.SaveUnknownHashesAsync(domain, new[] { oldHash, oldHash, XxHash64Ext.Hash(resolvedPath) }, CancellationToken.None);
        await store.SaveHashesAsync(new[] { new HashGuessMatch { Domain = domain, Hash = XxHash64Ext.Hash(resolvedPath), Path = resolvedPath } }, CancellationToken.None);
        using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);
        var service = new HashGuessingService(resolver, store,
            new HashGuessPersistenceService(store, new BinRstHashGuessingStore(bridge.Directories)), bridge.LogService, bridge.Directories);
        Assert.Equal(2, await service.ScanUnknownHashesAsync(domain, root, null, CancellationToken.None));
        Assert.Equal(2, await service.ScanUnknownHashesAsync(domain, root, null, CancellationToken.None));
        var pendingHashes = await store.LoadUnknownHashesAsync(domain, CancellationToken.None);
        Assert.True(pendingHashes.SetEquals(new[] { oldHash, newHash }));
        Assert.Equal(2, await store.LoadUnknownCountAsync(domain, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(bridge.Directories.HashLabPath, $"inventory.{domain.ToString().ToLowerInvariant()}.json")));
    }
}
