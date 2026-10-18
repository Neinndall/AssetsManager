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

public class GamePortraitVariantTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PortraitVariantsPreserveCaseNormalizationAndLongNames(bool longName)
    {
        string character = longName ? new string('雪', 600) : "target";
        var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
        {
            $"data/characters/{character}/skins/skin80.bin",
            "assets/characters/donor/hud/donor_circle_71_form1.tex"
        }));
        string target = $"assets/characters/{character}/hud/{character}_circle_80_form1.tex";
        var engine = Engine(target);
        game.GuessPortraitVariants(engine, CancellationToken.None, new[] { character.ToUpperInvariant() });
        Assert.Equal(target, Assert.Single(engine.Matches).Value.Path);
    }

    [Fact]
    public void BasicCharacterSearchLearnsPortraitFormsFromOtherCharacters()
    {
        var game = Guesser();
        var targets = Enumerable.Range(1, 3)
            .Select(form => $"assets/characters/target/hud/target_circle_80_form{form}.tex").ToArray();
        var engine = Engine(targets);
        game.GuessCharactersFiles(engine, CancellationToken.None, new[] { "target" });
        Assert.Equal(targets.Order(), engine.Matches.Values.Select(match => match.Path).Order());
    }

    [Fact]
    public void PortraitVariantsRequireKnownSkinsAndIgnorePatchDecorations()
    {
        var game = Guesser();
        var engine = Engine("assets/characters/target/hud/target_circle_81_form1.tex",
            "assets/characters/target/hud/target_circle_80_form4.patch.tex");
        game.GuessPortraitVariants(engine, CancellationToken.None, new[] { "target" });
        Assert.Empty(engine.Matches);
        Assert.Equal(3L, engine.CheckedCandidates);
    }

    [Fact]
    public void PortraitVariantsHonorBudgetsAndCancellation()
    {
        var game = Guesser();
        var engine = Engine("assets/characters/target/hud/target_circle_80_form3.tex");
        Assert.Equal(1, game.GuessPortraitVariants(engine, CancellationToken.None, new[] { "target" }, 1));
        Assert.Empty(engine.Matches);
        game.GuessPortraitVariants(engine, CancellationToken.None, new[] { "target" });
        Assert.Single(engine.Matches);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => game.GuessPortraitVariants(
            Engine("assets/characters/target/hud/target_circle_80_form3.tex"), cancellation.Token, new[] { "target" }));
    }

    private static GameHashGuesser Guesser() => new(new HashFile(HashGuessDomain.Game, new[]
    {
        "data/characters/target/skins/skin80.bin",
        "assets/characters/donor/hud/donor_circle_71_form1.tex",
        "assets/characters/donor/hud/donor_circle_71_form2.tex",
        "assets/characters/donor/hud/donor_circle_71_form3.tex",
        "assets/characters/donor/hud/donor_circle_71_form4.patch.tex"
    }));

    private static HashGuessEngine Engine(params string[] paths) => new(HashGuessDomain.Game,
        paths.Select(path => XxHash64Ext.Hash(path)).Append(42UL).ToHashSet());
}
