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

public sealed class HashGuessingPersistenceOrderTests
{
    [Fact]
    public async Task PersistRunResultsAsync_SavesMatchesBeforePurgingUnknowns()
    {
        using var bridge = new AssetsManagerTestBridge();
        var store = new HashGuessingStore(bridge.Directories);
        const string resolvedPath = "assets/characters/ahri/base/ahri.bin";
        ulong resolvedHash = XxHash64Ext.Hash(resolvedPath);
        ulong stillUnknownHash = XxHash64Ext.Hash("assets/characters/ahri/base/still_unknown.bin");

        await store.SaveUnknownHashesAsync(HashGuessDomain.Game, new[] { resolvedHash, stillUnknownHash }, CancellationToken.None);

        using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);
        var service = new HashGuessingService(
            resolver,
            store,
            new HashGuessPersistenceService(store, new BinRstHashGuessingStore(bridge.Directories)),
            bridge.LogService,
            bridge.Directories);

        var matches = new[]
        {
            new HashGuessMatch
            {
                Domain = HashGuessDomain.Game,
                Hash = resolvedHash,
                Path = resolvedPath
            }
        };

        await service.PersistRunResultsAsync(HashGuessDomain.Game, matches, new[] { stillUnknownHash });

        Assert.True(resolver.IsKnownHash(resolvedHash));
        Assert.Equal(resolvedPath, resolver.ResolveHash(resolvedHash));

        var remaining = await store.LoadUnknownHashesAsync(HashGuessDomain.Game, CancellationToken.None);
        Assert.Single(remaining);
        Assert.Contains(stillUnknownHash, remaining);
        Assert.DoesNotContain(resolvedHash, remaining);
    }

    [Fact]
    public async Task PersistRunResultsAsync_DoesNotPurgeUnknownsWhenSavingMatchesFails()
    {
        using var bridge = new AssetsManagerTestBridge();
        var store = new HashGuessingStore(bridge.Directories);
        const string resolvedPath = "assets/characters/ahri/base/locked_save.bin";
        ulong resolvedHash = XxHash64Ext.Hash(resolvedPath);
        ulong stillUnknownHash = XxHash64Ext.Hash("assets/characters/ahri/base/still_unknown.bin");

        await store.SaveUnknownHashesAsync(HashGuessDomain.Game, new[] { resolvedHash, stillUnknownHash }, CancellationToken.None);

        string hashesFile = Path.Combine(bridge.Directories.HashesPath, "hashes.game.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(hashesFile)!);
        await File.WriteAllTextAsync(hashesFile, string.Empty);

        using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);
        var service = new HashGuessingService(
            resolver,
            store,
            new HashGuessPersistenceService(store, new BinRstHashGuessingStore(bridge.Directories)),
            bridge.LogService,
            bridge.Directories);

        var matches = new[]
        {
            new HashGuessMatch
            {
                Domain = HashGuessDomain.Game,
                Hash = resolvedHash,
                Path = resolvedPath
            }
        };

        using (new FileStream(hashesFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() =>
                service.PersistRunResultsAsync(HashGuessDomain.Game, matches, new[] { stillUnknownHash }));
        }

        var remaining = await store.LoadUnknownHashesAsync(HashGuessDomain.Game, CancellationToken.None);
        Assert.Equal(2, remaining.Count);
        Assert.Contains(resolvedHash, remaining);
        Assert.Contains(stillUnknownHash, remaining);
    }
}
