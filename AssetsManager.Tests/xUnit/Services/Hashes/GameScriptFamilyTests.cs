using System;
using System.Linq;
using System.Threading;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Services.Hashes.Guessers.Game;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Hashes;

public sealed class GameScriptFamilyTests
{
    [Fact]
    public void BorrowsBareAndNamespacedSpellNamesWithinCharacterFamily()
    {
        var game = Guesser();
        string[] targets = { "data/characters/mode_target/spells/mode_target_attack.luabin64",
            "data/characters/mode_target/spells/mode_target_spellcast.preload" };
        var engine = Engine(targets);
        game.GuessScriptFamilyAliases(engine, CancellationToken.None);
        Assert.Equal(targets.Order(), engine.Matches.Values.Select(match => match.Path).Order());
    }

    [Fact]
    public void DoesNotMixUnrelatedFamiliesOrGuessUnsupportedTargetPrefixes()
    {
        var game = Guesser("data/characters/other/spells/secret.luabin64",
            "data/characters/new_target/spells/new_target_seed.luabin64");
        var engine = Engine("data/characters/mode_target/spells/mode_target_secret.luabin64",
            "data/characters/new_target/spells/new_target_attack.luabin64");
        game.GuessScriptFamilyAliases(engine, CancellationToken.None);
        Assert.Empty(engine.Matches);
    }

    [Fact]
    public void MultipleExtensionsOfOneExampleDoNotSupplyThreeDistinctSeeds()
    {
        var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
        {
            "data/characters/target/spells/attack.luabin64",
            "data/characters/mode_target/spells/mode_target_one.luabin64",
            "data/characters/mode_target/spells/mode_target_one.preload",
            "data/characters/mode_target/spells/mode_target_two.luabin64"
        }));
        var engine = Engine("data/characters/mode_target/spells/mode_target_attack.luabin64");
        Assert.Equal(0, game.GuessScriptFamilyAliases(engine, CancellationToken.None));
        Assert.Empty(engine.Matches);
    }

    [Fact]
    public void HonorsBudgetAndCancellation()
    {
        var game = Guesser();
        var engine = Engine("data/characters/mode_target/spells/mode_target_spellcast.preload");
        Assert.Equal(0, game.GuessScriptFamilyAliases(engine, CancellationToken.None, 0));
        Assert.Equal(1, game.GuessScriptFamilyAliases(engine, CancellationToken.None, 1));
        Assert.Empty(engine.Matches);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => game.GuessScriptFamilyAliases(engine, cancellation.Token));
    }

    private static GameHashGuesser Guesser(params string[] additional) => new(new HashFile(HashGuessDomain.Game, new[]
    {
        "data/characters/target/spells/attack.luabin64",
        "data/characters/target/spells/target_spellcast.luabin64",
        "data/characters/mode_target/spells/mode_target_seed1.luabin64",
        "data/characters/mode_target/spells/mode_target_seed2.luabin64",
        "data/characters/mode_target/spells/mode_target_seed3.luabin64"
    }.Concat(additional)));
    private static HashGuessEngine Engine(params string[] paths) => new(HashGuessDomain.Game, paths.Select(path => XxHash64Ext.Hash(path)).ToHashSet());
}
