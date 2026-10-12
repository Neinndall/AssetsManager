using System.Collections.Generic;
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
    public async Task GuessingPreservesCurrentAndHistoricalUnknownsFromTheLastScan(HashGuessDomain domain, bool grep)
    {
        using var bridge = new AssetsManagerTestBridge();
        string root = bridge.CreateDirectory("client");
        var store = new HashGuessingStore(bridge.Directories);
        var pending = new HashSet<ulong> { 1, 2 };
        await store.SaveUnknownHashesAsync(domain, pending, pending, "patch-a", CancellationToken.None);
        await store.SaveUnknownHashesAsync(domain, pending, new HashSet<ulong> { 2 }, "patch-b", CancellationToken.None);
        using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);
        var service = new HashGuessingService(resolver, store,
            new HashGuessPersistenceService(store, new BinRstHashGuessingStore(bridge.Directories)), bridge.LogService, bridge.Directories);
        if (grep) await service.RunEmbeddedPathGrepAsync(domain, root, null, CancellationToken.None);
        else if (domain == HashGuessDomain.Game)
            await service.RunGameBasicGuessingAsync(root, null, CancellationToken.None, selectedSubMethods: new HashSet<string>());
        else
            await service.RunLcuBasicGuessingAsync(root, null, CancellationToken.None, selectedSubMethods: new HashSet<string>());

        var inventory = await store.LoadUnknownInventoryAsync(domain, CancellationToken.None);
        Assert.True(inventory.All.SetEquals(pending));
        Assert.Equal(2UL, Assert.Single(inventory.Current));
        Assert.Equal("patch-b", inventory.PatchFingerprint);
        var summary = await store.LoadUnknownSummaryAsync(domain, CancellationToken.None);
        Assert.Equal(1, summary.Current);
        Assert.Equal(1, summary.Recent);
    }

    [Theory]
    [InlineData(HashGuessDomain.Game)]
    [InlineData(HashGuessDomain.Lcu)]
    public async Task ScanningWadsAddsNewPatchUnknownsWithoutDroppingOlderOnes(HashGuessDomain domain)
    {
        using var bridge = new AssetsManagerTestBridge();
        string root = bridge.CreateDirectory("client");
        string extension = domain == HashGuessDomain.Game ? ".wad.client" : ".wad";
        const string oldPath = "assets/previous-patch.bin", newPath = "assets/current-patch.bin";
        ulong oldHash = XxHash64Ext.Hash(oldPath), newHash = XxHash64Ext.Hash(newPath);
        bridge.BakeWad(root, "current" + extension, (newPath, "new payload"));
        var store = new HashGuessingStore(bridge.Directories);
        await store.SaveUnknownHashesAsync(domain, new[] { oldHash }, new HashSet<ulong> { oldHash }, "old-patch", CancellationToken.None);
        using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);
        var service = new HashGuessingService(resolver, store,
            new HashGuessPersistenceService(store, new BinRstHashGuessingStore(bridge.Directories)), bridge.LogService, bridge.Directories);
        await service.ScanUnknownHashesAsync(domain, root, null, CancellationToken.None);
        var inventory = await store.LoadUnknownInventoryAsync(domain, CancellationToken.None);
        Assert.True(inventory.All.SetEquals(new[] { oldHash, newHash }));
        Assert.Equal(newHash, Assert.Single(inventory.Current));
        Assert.StartsWith(domain + ":", inventory.PatchFingerprint);
    }
}
