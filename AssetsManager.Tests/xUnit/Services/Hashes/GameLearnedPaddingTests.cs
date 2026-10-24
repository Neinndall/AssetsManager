using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Services.Hashes.Guessers.Game;
using AssetsManager.Tests.xUnit.Infrastructure;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Hashes;

public sealed class GameLearnedPaddingTests
{
    [Theory]
    [InlineData("001", "007")]
    [InlineData("012", "007")]
    [InlineData("0001", "0007")]
    [InlineData("00001", "00007")]
    [InlineData("000001", "000007")]
    public void PreservesLearnedWidthAndDirectory(string seed, string target)
    {
        string prefix = "assets/characters/example/themes/chef/animations/finisher_c";
        var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[] { prefix + seed + ".anm" }));
        string expected = prefix + target + ".anm";
        var engine = Engine(expected, prefix + "7.anm", prefix + "07.anm");
        guesser.GuessLearnedPaddedNumbers(engine, CancellationToken.None, maximum: 8);
        Assert.Equal(expected, Assert.Single(engine.Matches.Values).Path);
        Assert.Equal(2, engine.RemainingUnknownCount);
    }

    [Fact]
    public void DoesNotInventPaddingOrChangeDirectoryCounters()
    {
        var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game,
            new[] { "assets/skin001/finisher_c01.anm", "assets/skin001/finisher_123.anm" }));
        var engine = Engine("assets/skin007/finisher_c01.anm", "assets/skin001/finisher_007.anm");
        Assert.Equal(0, guesser.GuessLearnedPaddedNumbers(engine, CancellationToken.None));
        Assert.Empty(engine.Matches);
    }

    [Fact]
    public void HonorsBudgetAndCancellation()
    {
        var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[] { "assets/finisher_c001.anm" }));
        var engine = Engine("assets/finisher_c007.anm");
        Assert.Equal(0, guesser.GuessLearnedPaddedNumbers(engine, CancellationToken.None, candidateBudget: 0));
        Assert.Equal(3, guesser.GuessLearnedPaddedNumbers(engine, CancellationToken.None, candidateBudget: 3));
        Assert.Empty(engine.Matches);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => guesser.GuessLearnedPaddedNumbers(engine, cancellation.Token));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BasicRunsLearnedPaddingOnlyWhenNumbersAreSelected(bool selected)
    {
        using var bridge = new AssetsManagerTestBridge();
        bridge.Directories.CreateHashesDirectories();
        const string seed = "assets/example/finisher_c001.anm";
        const string target = "assets/example/finisher_c007.anm";
        await File.WriteAllTextAsync(Path.Combine(bridge.Directories.HashesPath, "hashes.game.txt"),
            $"{XxHash64Ext.Hash(seed):x16} {seed}\n");
        var store = new HashGuessingStore(bridge.Directories);
        var pending = new HashSet<ulong> { XxHash64Ext.Hash(target) };
        await store.SaveUnknownHashesAsync(HashGuessDomain.Game, pending, pending, "test", CancellationToken.None);
        using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);
        var service = new HashGuessingService(resolver, store,
            new HashGuessPersistenceService(store, new BinRstHashGuessingStore(bridge.Directories)), bridge.LogService, bridge.Directories);
        var result = await service.RunGameBasicGuessingAsync(bridge.RootPath, null, CancellationToken.None,
            selectedSubMethods: selected ? new HashSet<string> { "game-basic-numbers" } : new HashSet<string>());
        Assert.Equal(selected ? new[] { target } : Array.Empty<string>(), result.Matches.Select(match => match.Path));
    }

    private static HashGuessEngine Engine(params string[] paths) =>
        new(HashGuessDomain.Game, paths.Select(p => XxHash64Ext.Hash(p)).ToHashSet());
}
