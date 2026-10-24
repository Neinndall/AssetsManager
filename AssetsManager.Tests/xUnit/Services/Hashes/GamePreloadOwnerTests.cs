using System;
using System.Linq;
using System.Text;
using System.Threading;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Services.Hashes.Guessers.Game;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Hashes;

public sealed class GamePreloadOwnerTests
{
    [Theory]
    [InlineData("Mode_Target.Dive")]
    [InlineData("Mode_Target.Mode_Target_Dive")]
    [InlineData("Mode_Target_Dive")]
    public void NamespacedPreloadReferencesResolveUnderKnownCharacterSpellDirectory(string name)
    {
        var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[] { "data/characters/mode_target/mode_target.bin" }));
        string[] targets = { "data/characters/mode_target/spells/mode_target_dive.luabin64",
            "data/characters/mode_target/spells/mode_target_dive.preload" };
        var engine = Engine(targets);
        byte[] data = Encoding.ASCII.GetBytes($"PreLoadBuildingBlocks = {{Name=\"{name}\"}}");
        game.GrepWad(engine, new ArraySegment<byte>(data), "unknown.preload", "test.wad.client", 0, CancellationToken.None);
        Assert.Equal(targets.Order(), engine.Matches.Values.Select(match => match.Path).Order());
    }

    [Fact]
    public void DoesNotInventOwnersFromUnqualifiedNamesOrFilePaths()
    {
        var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[] { "data/characters/mode_target/mode_target.bin" }));
        var engine = Engine("data/characters/other/spells/other_dive.preload",
            "data/characters/mode_target/spells/mode_target_dive.preload");
        byte[] data = Encoding.ASCII.GetBytes("PreLoadBuildingBlocks = {Name=\"Other.Dive\", Name=\"Dive\", Name=\"Mode_Target.Dive.lua\"}");
        game.GrepWad(engine, new ArraySegment<byte>(data), "unknown.preload", "test.wad.client", 0, CancellationToken.None);
        Assert.Empty(engine.Matches);
    }

    private static HashGuessEngine Engine(params string[] paths) => new(HashGuessDomain.Game, paths.Select(path => XxHash64Ext.Hash(path)).ToHashSet());
}
