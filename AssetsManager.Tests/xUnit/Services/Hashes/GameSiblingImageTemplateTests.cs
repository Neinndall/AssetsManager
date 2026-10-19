using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers.Game;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Hashes;

public class GameSiblingImageTemplateTests
{
    private const uint Owner = 23;
    private const string First = "assets/cards/chibiredfox/chibi_red-fox-2x-pc.tex";
    private const string FirstThumb = "assets/thumbs/chibiredfox/thumb-chibi_red-fox.tex";
    private const string Second = "assets/cards/chibiblueowl/chibi_blue-owl-2x-pc.tex";
    private const string SecondThumb = "assets/thumbs/chibiblueowl/thumb-chibi_blue-owl.tex";
    private const string Source = "assets/cards/chibigreencat/chibi_green-cat-2x-pc.tex";
    private const string Target = "assets/thumbs/chibigreencat/thumb-chibi_green_cat.tex";

    [Fact]
    public void ImageLayoutsNeedTwoDistinctExamplesAndResolveEarlierPendingLinks()
    {
        var index = new GameSiblingImageTemplateIndex();
        var engine = Engine(Target);
        var known = Catalog();
        Guess(index, engine, Pair(Source, Target), known, "pending", 77);
        Guess(index, engine, Pair(First, FirstThumb), known);
        Guess(index, engine, Pair(First, FirstThumb), known);
        Assert.Empty(engine.Matches);
        Guess(index, engine, Pair(Second, SecondThumb), known);
        var match = Assert.Single(engine.Matches).Value;
        Assert.Equal(Target, match.Path);
        Assert.Equal("pending", match.SourceWadPath);
        Assert.Equal(77UL, match.SourceChunkHash);
    }

    [Fact]
    public void ImageLayoutsRemainScopedToOwnerFieldsAndExactTarget()
    {
        var index = new GameSiblingImageTemplateIndex();
        string competitor = Target.Replace("green_cat", "green-cat", StringComparison.Ordinal);
        var engine = Engine(Target, competitor);
        var known = Catalog();
        Guess(index, engine, Pair(First, FirstThumb), known);
        Guess(index, engine, Pair(Second, SecondThumb), known);
        index.Guess(engine, Owner + 1, Pair(Source, Target), known, "wrong class", 1, CancellationToken.None);
        var wrongField = Pair(Source, Target, "otherThumbnail");
        Guess(index, engine, wrongField, known);
        Assert.Empty(engine.Matches);
        Guess(index, engine, Pair(Source, Target), known);
        Assert.Equal(Target, Assert.Single(engine.Matches).Value.Path);
        Assert.Contains(XxHash64Ext.Hash(competitor), engine.UnknownHashes);
    }

    [Fact]
    public void ImageLayoutsUseSiblingLabelsToSeparateCompoundNames()
    {
        const string source = "assets/cards/arenariverbend/mythic_arena_riverbend-2x-pc.tex";
        const string target = "assets/thumbs/arenariverbend/thumb-arena_river-bend.tex";
        var index = new GameSiblingImageTemplateIndex();
        var engine = Engine(target);
        var known = Catalog();
        known[XxHash64Ext.Hash(source)] = source;
        Guess(index, engine, Pair(First, FirstThumb), known);
        Guess(index, engine, Pair(Second, SecondThumb), known);
        var properties = Pair(source, target).Append(new BinTreeString(Fnv1a.HashLower("label"), "DisplayName_RiverBend")).ToArray();
        Guess(index, engine, properties, known);
        Assert.Equal(target, Assert.Single(engine.Matches).Value.Path);
    }

    [Fact]
    public void ImageLayoutsCanDeriveTheContentFolderFromTheSourceFilename()
    {
        string source = Source.Replace("/chibigreencat/", "/legacyfolder/", StringComparison.Ordinal);
        var index = new GameSiblingImageTemplateIndex();
        var engine = Engine(Target);
        var known = Catalog();
        known[XxHash64Ext.Hash(source)] = source;
        Guess(index, engine, Pair(First, FirstThumb), known);
        Guess(index, engine, Pair(Second, SecondThumb), known);
        Guess(index, engine, Pair(source, Target), known);
        Assert.Equal(Target, Assert.Single(engine.Matches).Value.Path);
    }

    [Fact]
    public void CancelledImageLearningCanReplayOutstandingLinks()
    {
        var index = new GameSiblingImageTemplateIndex();
        string source2 = Source.Replace("greencat", "goldbird", StringComparison.Ordinal).Replace("green-cat", "gold-bird", StringComparison.Ordinal);
        string target2 = Target.Replace("greencat", "goldbird", StringComparison.Ordinal).Replace("green_cat", "gold_bird", StringComparison.Ordinal);
        using var cancellation = new CancellationTokenSource();
        var engine = new HashGuessEngine(HashGuessDomain.Game, new[] { XxHash64Ext.Hash(Target), XxHash64Ext.Hash(target2), 42UL }.ToHashSet(),
            _ => cancellation.Cancel());
        var known = Catalog();
        known[XxHash64Ext.Hash(source2)] = source2;
        Guess(index, engine, Pair(Source, Target), known);
        Guess(index, engine, Pair(source2, target2), known);
        Guess(index, engine, Pair(First, FirstThumb), known);
        Assert.Throws<OperationCanceledException>(() => index.Guess(engine, Owner, Pair(Second, SecondThumb), known, "learn", 1, cancellation.Token));
        Assert.Single(engine.Matches);
        Guess(index, engine, Pair(Second, SecondThumb), known);
        Assert.Equal(2, engine.Matches.Count);
    }

    private static Dictionary<ulong, string> Catalog() => new[] { First, FirstThumb, Second, SecondThumb, Source }
        .ToDictionary(path => XxHash64Ext.Hash(path), path => path);
    private static HashGuessEngine Engine(params string[] paths) => new(HashGuessDomain.Game,
        paths.Select(path => XxHash64Ext.Hash(path)).Append(42UL).ToHashSet());
    private static BinTreeProperty[] Pair(string source, string target, string targetField = "thumbnail") => new BinTreeProperty[]
    {
        new BinTreeWadChunkLink(Fnv1a.HashLower("background"), XxHash64Ext.Hash(source)),
        new BinTreeWadChunkLink(Fnv1a.HashLower(targetField), XxHash64Ext.Hash(target))
    };
    private static void Guess(GameSiblingImageTemplateIndex index, HashGuessEngine engine, IReadOnlyCollection<BinTreeProperty> props,
        IReadOnlyDictionary<ulong, string> known, string wad = "source", ulong chunk = 1) => index.Guess(engine, Owner, props, known, wad, chunk, CancellationToken.None);
}
