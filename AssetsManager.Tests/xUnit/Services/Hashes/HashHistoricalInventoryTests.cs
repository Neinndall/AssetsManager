using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Monitor;
using AssetsManager.Tests.xUnit.Infrastructure;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Hashes;

public sealed class HashHistoricalInventoryTests
{
    [Theory]
    [InlineData(HashGuessDomain.Game)]
    [InlineData(HashGuessDomain.Lcu)]
    public async Task ScanUsesInstalledVersionAndDoesNotRollOverForAdditionalWadsInSamePatch(HashGuessDomain domain)
    {
        using var bridge = new AssetsManagerTestBridge();
        string root = bridge.CreateDirectory("client");
        string game = Path.Combine(root, "Game");
        Directory.CreateDirectory(game);
        string metadata = Path.Combine(game, "code-metadata.json");
        string wadRoot = domain == HashGuessDomain.Game ? game : root;
        string extension = domain == HashGuessDomain.Game ? ".wad.client" : ".wad";
        bridge.BakeWad(wadRoot, "first" + extension, ("assets/first.bin", "payload"));
        await File.WriteAllTextAsync(metadata, "{\"version\":\"16.23.1.1\"}");
        using var http = new HttpClient();
        using var downloader = new ManifestDownloader(http, bridge.LogService, bridge.Directories, null);
        var versions = new VersionService(bridge.LogService, http, bridge.Directories, null, downloader, null);
        var store = new HashGuessingStore(bridge.Directories);
        using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);
        var service = new HashGuessingService(resolver, store,
            new HashGuessPersistenceService(store, new BinRstHashGuessingStore(bridge.Directories)), bridge.LogService, bridge.Directories, versions);
        await service.ScanUnknownHashesAsync(domain, root, null, CancellationToken.None);
        bridge.BakeWad(wadRoot, "second" + extension, ("assets/second.bin", "payload"));
        await service.ScanUnknownHashesAsync(domain, root, null, CancellationToken.None);
        Assert.Equal(2, await store.LoadUnknownCountAsync(domain, CancellationToken.None, HashGuessScope.CurrentPatch));
        Assert.Equal(0, await store.LoadUnknownCountAsync(domain, CancellationToken.None, HashGuessScope.Historical));

        await File.WriteAllTextAsync(metadata, "{\"version\":\"16.23.1.2\"}");
        bridge.BakeWad(wadRoot, "third" + extension, ("assets/third.bin", "payload"));
        await service.ScanUnknownHashesAsync(domain, root, null, CancellationToken.None);
        Assert.Equal(1, await store.LoadUnknownCountAsync(domain, CancellationToken.None, HashGuessScope.CurrentPatch));
        Assert.Equal(2, await store.LoadUnknownCountAsync(domain, CancellationToken.None, HashGuessScope.Historical));
        await service.ScanUnknownHashesAsync(domain, root, null, CancellationToken.None);
        Assert.Equal(1, await store.LoadUnknownCountAsync(domain, CancellationToken.None, HashGuessScope.CurrentPatch));

        await File.WriteAllTextAsync(metadata, "{\"version\":\"16.23.1.3\"}");
        await File.WriteAllTextAsync(Path.Combine(wadRoot, "broken" + extension), "invalid wad");
        await Assert.ThrowsAsync<IOException>(() => service.ScanUnknownHashesAsync(domain, root, null, CancellationToken.None));
        Assert.Equal(1, await store.LoadUnknownCountAsync(domain, CancellationToken.None, HashGuessScope.CurrentPatch));
        Assert.Equal(2, await store.LoadUnknownCountAsync(domain, CancellationToken.None, HashGuessScope.Historical));
    }

    [Theory]
    [InlineData(HashGuessDomain.Game)]
    [InlineData(HashGuessDomain.Lcu)]
    public async Task PatchRolloverMigratesLegacyAndKeepsRepeatedScansInCurrentPatch(HashGuessDomain domain)
    {
        using var bridge = new AssetsManagerTestBridge();
        var store = new HashGuessingStore(bridge.Directories);
        await store.SaveUnknownHashesAsync(domain, new ulong[] { 1, 2 }, CancellationToken.None);
        await store.SaveScannedInventoryAsync(domain, new ulong[] { 2, 3 }, "patch-1", h => h == 1, CancellationToken.None);
        Assert.Equal(new ulong[] { 2 }, await store.LoadUnknownHashesAsync(domain, CancellationToken.None, HashGuessScope.Historical));
        Assert.Equal(new ulong[] { 3 }, await store.LoadUnknownHashesAsync(domain, CancellationToken.None, HashGuessScope.CurrentPatch));

        // Reload the store, then discover another hash without changing the installed patch.
        store = new HashGuessingStore(bridge.Directories);
        await store.SaveScannedInventoryAsync(domain, new ulong[] { 2, 3, 4 }, "patch-1", _ => false, CancellationToken.None);
        Assert.True((await store.LoadUnknownHashesAsync(domain, CancellationToken.None, HashGuessScope.CurrentPatch)).SetEquals(new ulong[] { 3, 4 }));
        await store.SaveScannedInventoryAsync(domain, new ulong[] { 4, 5 }, "patch-2", h => h == 3, CancellationToken.None);
        Assert.True((await store.LoadUnknownHashesAsync(domain, CancellationToken.None, HashGuessScope.Historical)).SetEquals(new ulong[] { 2, 4 }));
        Assert.Equal(new ulong[] { 5 }, await store.LoadUnknownHashesAsync(domain, CancellationToken.None, HashGuessScope.CurrentPatch));
        Assert.Equal(3, await store.LoadUnknownCountAsync(domain, CancellationToken.None));
    }

    [Theory]
    [InlineData(HashGuessDomain.Game, HashGuessScope.All)]
    [InlineData(HashGuessDomain.Game, HashGuessScope.CurrentPatch)]
    [InlineData(HashGuessDomain.Game, HashGuessScope.Historical)]
    [InlineData(HashGuessDomain.Lcu, HashGuessScope.All)]
    [InlineData(HashGuessDomain.Lcu, HashGuessScope.CurrentPatch)]
    [InlineData(HashGuessDomain.Lcu, HashGuessScope.Historical)]
    public async Task ScopedGuessingReadsSelectedBucketAndPreservesOtherPendingHashes(HashGuessDomain domain, HashGuessScope scope)
    {
        using var bridge = new AssetsManagerTestBridge();
        string root = bridge.CreateDirectory("client");
        var store = new HashGuessingStore(bridge.Directories);
        await store.SaveScannedInventoryAsync(domain, new ulong[] { 1 }, "patch-1", _ => false, CancellationToken.None);
        await store.SaveScannedInventoryAsync(domain, new ulong[] { 2 }, "patch-2", _ => false, CancellationToken.None);
        using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);
        var service = CreateService(bridge, store, resolver);
        var result = domain == HashGuessDomain.Game
            ? await service.RunGameBasicGuessingAsync(root, null, CancellationToken.None, selectedSubMethods: new HashSet<string>(), scope: scope)
            : await service.RunLcuBasicGuessingAsync(root, null, CancellationToken.None, selectedSubMethods: new HashSet<string>(), scope: scope);
        Assert.Equal(scope == HashGuessScope.All ? 2 : 1, result.UnknownHashesAtStart);
        Assert.Equal(new ulong[] { 1 }, await store.LoadUnknownHashesAsync(domain, CancellationToken.None, HashGuessScope.Historical));
        Assert.Equal(new ulong[] { 2 }, await store.LoadUnknownHashesAsync(domain, CancellationToken.None, HashGuessScope.CurrentPatch));
    }

    [Theory]
    [InlineData(HashGuessDomain.Game)]
    [InlineData(HashGuessDomain.Lcu)]
    public async Task EmptyCurrentBucketDoesNotRescanOrGuessHistoricalHashes(HashGuessDomain domain)
    {
        using var bridge = new AssetsManagerTestBridge();
        var store = new HashGuessingStore(bridge.Directories);
        await store.SaveScannedInventoryAsync(domain, new ulong[] { 1 }, "patch-1", _ => false, CancellationToken.None);
        await store.SaveScannedInventoryAsync(domain, Array.Empty<ulong>(), "patch-2", _ => false, CancellationToken.None);
        string root = bridge.CreateDirectory("client");
        bridge.BakeWad(root, domain == HashGuessDomain.Game ? "sample.wad.client" : "sample.wad", ("assets/not-in-inventory.bin", "payload"));
        using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);
        var result = await CreateService(bridge, store, resolver).RunEmbeddedPathGrepAsync(domain, root, null, CancellationToken.None, scope: HashGuessScope.CurrentPatch);
        Assert.Equal(0, result.UnknownHashesAtStart);
        Assert.Equal(new ulong[] { 1 }, await store.LoadUnknownHashesAsync(domain, CancellationToken.None));
    }

    [Theory]
    [InlineData(HashGuessDomain.Game, HashGuessScope.All, false)]
    [InlineData(HashGuessDomain.Game, HashGuessScope.CurrentPatch, false)]
    [InlineData(HashGuessDomain.Game, HashGuessScope.Historical, false)]
    [InlineData(HashGuessDomain.Lcu, HashGuessScope.All, false)]
    [InlineData(HashGuessDomain.Lcu, HashGuessScope.CurrentPatch, false)]
    [InlineData(HashGuessDomain.Lcu, HashGuessScope.Historical, false)]
    [InlineData(HashGuessDomain.Game, HashGuessScope.CurrentPatch, true)]
    [InlineData(HashGuessDomain.Game, HashGuessScope.Historical, true)]
    [InlineData(HashGuessDomain.Lcu, HashGuessScope.CurrentPatch, true)]
    [InlineData(HashGuessDomain.Lcu, HashGuessScope.Historical, true)]
    public async Task GrepRemovesResolvedHashesFromTheirBucketIncludingOnCancellation(HashGuessDomain domain, HashGuessScope scope, bool cancel)
    {
        using var bridge = new AssetsManagerTestBridge();
        string oldPath = domain == HashGuessDomain.Game ? "data/shared/particles/historical.troybin" : "plugins/rcp-fe-test/global/default/historical.json";
        string newPath = domain == HashGuessDomain.Game ? "data/shared/particles/current.troybin" : "plugins/rcp-fe-test/global/default/current.json";
        ulong oldHash = XxHash64Ext.Hash(oldPath), newHash = XxHash64Ext.Hash(newPath);
        var store = new HashGuessingStore(bridge.Directories);
        await store.SaveScannedInventoryAsync(domain, new[] { oldHash }, "patch-1", _ => false, CancellationToken.None);
        await store.SaveScannedInventoryAsync(domain, new[] { newHash }, "patch-2", _ => false, CancellationToken.None);
        string root = bridge.CreateDirectory("client");
        string sourcePath = domain == HashGuessDomain.Game ? "data/shared/source.preload" : "plugins/source.json";
        string content = domain == HashGuessDomain.Game
            ? "PreLoadBuildingBlocks = {{Name=\"historical.troy\"},{Name=\"current.troy\"}}"
            : $"[\"{oldPath}\",\"{newPath}\"]";
        await store.SaveHashesAsync(new[] { new HashGuessMatch { Domain = domain, Hash = XxHash64Ext.Hash(sourcePath), Path = sourcePath } }, CancellationToken.None);
        bridge.BakeWad(root, domain == HashGuessDomain.Game ? "sample.wad.client" : "sample.wad", (sourcePath, content));
        using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);
        var service = CreateService(bridge, store, resolver);
        using var cts = new CancellationTokenSource();
        var progress = new ImmediateProgress<HashGuessMatch>(_ => { if (cancel) cts.Cancel(); });
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunEmbeddedPathGrepAsync(domain, root, null, cts.Token, progress, scope));
        else
        {
            var result = await service.RunEmbeddedPathGrepAsync(domain, root, null, cts.Token, progress, scope);
            Assert.Equal(scope == HashGuessScope.All ? 2 : 1, result.Matches.Count);
            await service.SaveMatchesAsync(result.Matches, CancellationToken.None);
        }
        var current = await store.LoadUnknownHashesAsync(domain, CancellationToken.None, HashGuessScope.CurrentPatch);
        var historical = await store.LoadUnknownHashesAsync(domain, CancellationToken.None, HashGuessScope.Historical);
        Assert.Equal(scope == HashGuessScope.Historical ? 1 : 0, current.Count);
        Assert.Equal(scope == HashGuessScope.CurrentPatch ? 1 : 0, historical.Count);
    }

    [Fact]
    public async Task InterruptedCommitIsRecoveredBeforeReadingEitherBucket()
    {
        using var bridge = new AssetsManagerTestBridge();
        Directory.CreateDirectory(bridge.Directories.HashLabPath);
        await File.WriteAllTextAsync(Path.Combine(bridge.Directories.HashLabPath, "pending.game.txt"),
            "{\"Current\":[3],\"Historical\":[1,2],\"Patch\":\"patch-2\"}");
        await File.WriteAllTextAsync(Path.Combine(bridge.Directories.HashLabPath, "unknowns.game.txt"), "0000000000000001\n");
        var store = new HashGuessingStore(bridge.Directories);
        Assert.Equal(3, await store.LoadUnknownCountAsync(HashGuessDomain.Game, CancellationToken.None));
        Assert.Equal(new ulong[] { 3 }, await store.LoadUnknownHashesAsync(HashGuessDomain.Game, CancellationToken.None, HashGuessScope.CurrentPatch));
        Assert.Equal("patch-2", await File.ReadAllTextAsync(Path.Combine(bridge.Directories.HashLabPath, "patch.game.txt")));
        Assert.False(File.Exists(Path.Combine(bridge.Directories.HashLabPath, "pending.game.txt")));
    }

    private static HashGuessingService CreateService(AssetsManagerTestBridge bridge, HashGuessingStore store, HashResolverService resolver) =>
        new(resolver, store, new HashGuessPersistenceService(store, new BinRstHashGuessingStore(bridge.Directories)), bridge.LogService, bridge.Directories);

    private sealed class ImmediateProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }
}
