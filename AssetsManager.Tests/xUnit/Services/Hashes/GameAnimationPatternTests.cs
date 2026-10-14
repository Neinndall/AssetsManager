using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Services.Hashes.Guessers.Game;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Hashes;

public sealed class GameAnimationPatternTests
{
    [Fact]
    public void ExplicitJadeCharactersAreNotGuessedAgainAsGeneratedAliases()
    {
        var targets = new[] { "data/characters/example/skins/root.bin", "data/characters/jade_example/skins/root.bin",
            "assets/characters/jade_example/hud/jade_example_circle.tex" }.Select(path => XxHash64Ext.Hash(path)).Append(42UL).ToHashSet();
        var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, Array.Empty<string>()));
        var inferred = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong>(targets));
        var explicitAlias = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong>(targets));
        int inferredCount = guesser.GuessCharactersFiles(inferred, CancellationToken.None, new[] { "example" });
        int explicitCount = guesser.GuessCharactersFiles(explicitAlias, CancellationToken.None, new[] { "example", "jade_example" });
        Assert.Equal(3, inferred.Matches.Count);
        Assert.Equal(inferred.Matches.Keys.OrderBy(h => h), explicitAlias.Matches.Keys.OrderBy(h => h));
        Assert.Equal(inferredCount, explicitCount);
        Assert.Equal(inferred.CheckedCandidates, explicitAlias.CheckedCandidates);
        Assert.Contains(42UL, explicitAlias.UnknownHashes);
    }

    [Theory]
    [InlineData("Win_00", "skin3_win_00")]
    [InlineData("Win_18", "skin3_win_18")]
    public void GrepInfersNumberedClipLabelsWithoutAnyCataloguedAnimationFile(string label, string filename)
    {
        string target = $"assets/characters/example/skins/skin03/animations/{filename}.anm";
        ulong hash = XxHash64Ext.Hash(target);
        var map = new BinTreeMap(Fnv1a.HashLower("mClipDataMap"), BinPropertyType.Hash, BinPropertyType.Struct,
            new[] { Clip(label, hash), Clip("Win_01", 42) });
        var tree = new BinTree(new[] { new BinTreeObject(1, Fnv1a.HashLower("AnimationGraphData"), new[] { map }) }, Array.Empty<string>());
        using var stream = new MemoryStream();
        tree.Write(stream);
        var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, Array.Empty<string>()),
            resolveBinHash: value => value == Fnv1a.HashLower("Win_01") ? "Win_01" : value.ToString("x8"));
        var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { hash, 42 });
        guesser.GrepWad(engine, new ArraySegment<byte>(stream.ToArray()),
            "data/characters/example/animations/skin3.bin", "example.wad.client", 123);
        var match = Assert.Single(engine.Matches).Value;
        Assert.Equal(target, match.Path);
        Assert.Equal("example.wad.client", match.SourceWadPath);
        Assert.Equal(123UL, match.SourceChunkHash);
        Assert.Contains(42UL, engine.UnknownHashes);
    }

    [Theory]
    [InlineData("intro_01", "intro01")]
    [InlineData("intro01", "intro_01")]
    public void AnimationLabelsAllowBothNumericSeparatorSpellings(string label, string filename)
    {
        string target = $"assets/characters/example/skins/skin3/animations/{filename}.anm";
        var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, Array.Empty<string>()));
        var remaining = new HashSet<ulong> { XxHash64Ext.Hash(target), 42 };
        Assert.Equal(new[] { target }, guesser.MatchAnimationVariants(label, "example", "skin3", remaining));
        Assert.Equal(new[] { 42UL }, remaining);
    }

    private static KeyValuePair<BinTreeProperty, BinTreeProperty> Clip(string label, ulong target) => new(
        new BinTreeHash(0, Fnv1a.HashLower(label)),
        new BinTreeStruct(0, Fnv1a.HashLower("AtomicClipData"), new BinTreeProperty[]
        {
            new BinTreeStruct(Fnv1a.HashLower("mAnimationResourceData"), Fnv1a.HashLower("AnimationResourceData"),
                new BinTreeProperty[] { new BinTreeWadChunkLink(Fnv1a.HashLower("mAnimationFilePath"), target) })
        }));
}
