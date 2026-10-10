using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Services.Hashes.Guessers.Game;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Hashing;
using LeagueToolkit.Core.Wad;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Hashes;

public sealed class GameLocalNamePatternTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SelectedCustomMethodUsesOnlyPendingWadFamilies(bool animations)
    {
        string directory = Path.Combine(Path.GetTempPath(), "assetsmanager-local-patterns-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string folder = "assets/characters/example/" + (animations ? "animations/" : "particles/");
        string extension = animations ? ".anm" : ".tex";
        string target = folder + "joke_sad_loop" + extension;
        try
        {
            WadBakeEntry Entry(string path) => new(path, () => new MemoryStream(new byte[] { 1 }), WadChunkCompression.None);
            WadBuilder.Bake(new[] { Entry(folder + "joke_loop" + extension), Entry(target) },
                Path.Combine(directory, "pending.wad.client"), new WadBakeSettings());
            WadBuilder.Bake(new[] { Entry(folder + "idle_sad" + extension) },
                Path.Combine(directory, "unrelated.wad.client"), new WadBakeSettings());
            var known = new HashFile(HashGuessDomain.Game, new[] { folder + "joke_loop" + extension, folder + "idle_sad" + extension });
            var guesser = new GameHashGuesser(known);
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(target) });
            guesser.GuessLocalNamePatterns(engine, directory, animations, CancellationToken.None, long.MaxValue, null);
            Assert.Empty(engine.Matches);
            WadBuilder.Bake(new[] { Entry(folder + "joke_loop" + extension), Entry(folder + "idle_sad" + extension), Entry(target) },
                Path.Combine(directory, "pending.wad.client"), new WadBakeSettings());
            guesser.RunCustomAttacks(engine, null, CancellationToken.None,
                new HashSet<string> { animations ? "game-custom-animations" : "game-custom-textures" }, directory);
            Assert.Equal(target, Assert.Single(engine.Matches).Value.Path);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("assets/characters/example/animations/", "joke_loop.anm", "idle_sad.anm", "joke_sad_loop.anm")]
    [InlineData("assets/characters/example/particles/", "surf_rock.tex", "idle_mask.tex", "surf_rock_mask.tex")]
    [InlineData("assets/characters/example/animations/", "jade_example_spell1.anm", "idle.anm", "example_jade_spell1.anm")]
    [InlineData("assets/ux/kiwi/augments/icons/", "bluenote_small.tex", "teacher_large.tex", "bluenote_large.tex")]
    [InlineData("assets/maps/kitpieces/example/textures/", "level7_arena_tx.tex", "level1_arena_gap_tx.tex", "level7_arena_gap_tx.tex")]
    public void LearnsNamesFromLocalFamily(string directory, string seed, string vocabulary, string target)
    {
        var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(directory + target), 42 });
        long count = GameHashGuesser.SubstituteLocalNameWords(engine,
            new[] { directory + seed, directory + vocabulary }, CancellationToken.None);
        Assert.Equal(directory + target, Assert.Single(engine.Matches).Value.Path);
        Assert.Equal(count, engine.CheckedCandidates);
    }

    [Fact]
    public void DoesNotBorrowWordsFromOtherDirectories()
    {
        const string target = "assets/characters/example/particles/surf_rock_mask.tex";
        var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(target) });
        GameHashGuesser.SubstituteLocalNameWords(engine, new[]
        {
            "assets/characters/example/particles/surf_rock.tex",
            "assets/characters/other/particles/idle_mask.tex"
        }, CancellationToken.None);
        Assert.Empty(engine.Matches);
    }

    [Fact]
    public void RespectsBudgetCancellationAndProgress()
    {
        string[] paths = { "assets/characters/example/animations/joke_loop.anm" };
        var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { 42 });
        long reported = -1;
        Assert.Equal(3L, GameHashGuesser.SubstituteLocalNameWords(engine, paths, CancellationToken.None, 3, count => reported = count));
        Assert.Equal(3L, engine.CheckedCandidates);
        Assert.Equal(3L, reported);
        Assert.Throws<OperationCanceledException>(() => GameHashGuesser.SubstituteLocalNameWords(engine, paths, new CancellationToken(true)));
    }
}
