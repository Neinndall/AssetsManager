using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
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

public sealed class GameBinLinkTemplateTests
{
    private static BinTree Tree(string name, ulong link, uint type = 100, uint field = 10) => new(
        new[] { new BinTreeObject(1, type, new BinTreeProperty[]
        {
            new BinTreeString(20, name), new BinTreeWadChunkLink(field, link)
        }) }, Array.Empty<string>());
    private const string Seed = "assets/ui/cards/forest_spirit_large.tex";
    private const string Target = "assets/ui/cards/moon_guardian_large.tex";
    private static Dictionary<ulong, string> Known => new() { [XxHash64Ext.Hash(Seed)] = Seed };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LearnsFromSiblingsAcrossBinsInEitherOrder(bool pendingFirst)
    {
        var index = new GameBinLinkTemplateIndex();
        var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(Target), 42 });
        void SeedScope() => index.Guess(engine, Tree("ForestSpirit", XxHash64Ext.Hash(Seed)), Known,
            null, null, "seed.wad", 1, CancellationToken.None);
        void PendingScope() => index.Guess(engine, Tree("MoonGuardian", XxHash64Ext.Hash(Target)), Known,
            null, null, "pending.wad", 2, CancellationToken.None);
        if (pendingFirst) { PendingScope(); SeedScope(); } else { SeedScope(); PendingScope(); }
        var match = Assert.Single(engine.Matches).Value;
        Assert.Equal(Target, match.Path);
        Assert.Equal("pending.wad", match.SourceWadPath);
        Assert.Equal(2UL, match.SourceChunkHash);
        Assert.Equal(HashGuessStrategy.BinLinkSibling, match.Strategy);
        Assert.Contains(42UL, engine.UnknownHashes);
        long checks = engine.CheckedCandidates;
        PendingScope(); SeedScope();
        Assert.Equal(checks, engine.CheckedCandidates);
    }

    [Theory]
    [InlineData(101U, 10U)]
    [InlineData(100U, 11U)]
    public void DoesNotTransferTemplatesAcrossClassesOrLinkFields(uint type, uint field)
    {
        var index = new GameBinLinkTemplateIndex();
        var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(Target) });
        index.Guess(engine, Tree("ForestSpirit", XxHash64Ext.Hash(Seed)), Known, null, null, "seed", 1, CancellationToken.None);
        index.Guess(engine, Tree("MoonGuardian", XxHash64Ext.Hash(Target), type, field), Known, null, null, "target", 2, CancellationToken.None);
        Assert.Empty(engine.Matches);
    }

    [Fact]
    public void ExactLinkGateDoesNotResolveAnUnrelatedUnknownCandidate()
    {
        var index = new GameBinLinkTemplateIndex();
        var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(Target), 42 });
        index.Guess(engine, Tree("ForestSpirit", XxHash64Ext.Hash(Seed)), Known, null, null, "seed", 1, CancellationToken.None);
        index.Guess(engine, Tree("MoonGuardian", 42), Known, null, null, "target", 2, CancellationToken.None);
        Assert.Empty(engine.Matches);
        Assert.True(engine.CheckedCandidates > 0);
    }

    [Fact]
    public void WideMaterialNamesRetainAncestorContext()
    {
        const string teacher = "assets/maps/kitpieces/set20/textures/forest_ground_tx.tex";
        const string target = "assets/maps/kitpieces/set20/textures/moon_ground_tx.tex";
        BinTree Material(ulong name, ulong texture) => new(new[]
        {
            new BinTreeObject(1, 100, new BinTreeProperty[]
            {
                new BinTreeHash64(30, name),
                new BinTreeContainer(40, BinPropertyType.Struct, new BinTreeProperty[]
                {
                    new BinTreeStruct(0, 200, new BinTreeProperty[] { new BinTreeWadChunkLink(10, texture) })
                })
            })
        }, Array.Empty<string>());
        var known = new Dictionary<ulong, string> { [XxHash64Ext.Hash(teacher)] = teacher };
        var index = new GameBinLinkTemplateIndex();
        var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(target) });
        string Resolve(ulong h) => h == 1 ? "Maps/Materials/Forest_Ground_Mat" : "Maps/Materials/Moon_Ground_Mat";
        index.Guess(engine, Material(1, XxHash64Ext.Hash(teacher)), known, null, Resolve, "seed", 1, CancellationToken.None);
        index.Guess(engine, Material(2, XxHash64Ext.Hash(target)), known, null, Resolve, "target", 2, CancellationToken.None);
        Assert.Equal(target, Assert.Single(engine.Matches).Value.Path);
    }

    [Fact]
    public void OptimizedPaddedPassPreservesWholeAndPartialNumericVariants()
    {
        var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
        {
            "assets/example/multiple_003_face07.tex", "assets/example/solo_24.tex"
        }));
        var unpadded = guesser.SubstituteNumbers(200).Select(c => c.Path).ToHashSet();
        var previous = unpadded.Concat(guesser.SubstituteNumbers(200, digits: 2).Select(c => c.Path)).ToHashSet();
        var engine = new HashGuessEngine(HashGuessDomain.Game, previous.Select(path => XxHash64Ext.Hash(path)).Append(42UL).ToHashSet());
        guesser.SubstituteNumbers(engine, CancellationToken.None, maximum: 200);
        guesser.SubstituteBasicPaddedNumbers(engine, CancellationToken.None, maximum: 200);
        Assert.True(previous.Select(path => XxHash64Ext.Hash(path)).ToHashSet().SetEquals(engine.Matches.Keys));
        Assert.True(engine.CheckedCandidates < guesser.SubstituteNumbers(200).Count() + guesser.SubstituteNumbers(200, digits: 2).Count());
    }

    [Fact]
    public void LearnsShortAnimationIdentifiers()
    {
        const string seed = "assets/animations/run_loop.anm";
        const string target = "assets/animations/die_loop.anm";
        var index = new GameBinLinkTemplateIndex();
        var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(target) });
        var known = new Dictionary<ulong, string> { [XxHash64Ext.Hash(seed)] = seed };
        index.Guess(engine, Tree("Run", XxHash64Ext.Hash(seed)), known, null, null, "seed", 1, CancellationToken.None);
        index.Guess(engine, Tree("Die", XxHash64Ext.Hash(target)), known, null, null, "target", 2, CancellationToken.None);
        Assert.Equal(target, Assert.Single(engine.Matches).Value.Path);
    }

    [Fact]
    public void AtlasRetainsUnrelatedMatchesAndNewUnresolvedSprites()
    {
        const string directory = "assets/items/icons2d/autoatlas/largeicons";
        const string unrelated = directory + "/a_icon.png";
        const string sprite = directory + "/b_icon.png";
        var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
        {
            "assets/items/icons2d/a_icon.png", "assets/items/icons2d/b_icon.png"
        }));
        var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(unrelated) });
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(new byte[] { 0x49, 0x4d, 0x41, 0x41 });
        writer.Write(1U);
        writer.Write(1UL);
        writer.Write(2U);
        foreach (ulong hash in new[] { XxHash64Ext.Hash(sprite), 42UL })
        {
            writer.Write(hash);
            for (int i = 0; i < 4; i++) writer.Write(0f);
            writer.Write(0U);
        }
        guesser.GrepWad(engine, new ArraySegment<byte>(stream.ToArray()),
            "ASSETS\\ITEMS\\ICONS2D\\AUTOATLAS\\LARGEICONS\\atlas_info.bin", "atlas.wad", 7);
        Assert.Equal(2, engine.Matches.Count);
        Assert.Equal(sprite, engine.Matches[XxHash64Ext.Hash(sprite)].Path);
        Assert.Equal(unrelated, engine.Matches[XxHash64Ext.Hash(unrelated)].Path);
        Assert.Equal("atlas.wad", engine.Matches[XxHash64Ext.Hash(sprite)].SourceWadPath);
        Assert.Equal(7UL, engine.Matches[XxHash64Ext.Hash(sprite)].SourceChunkHash);
        Assert.Equal(new[] { 42UL }, engine.UnknownHashes);
    }

    [Fact]
    public void CharacterFilesProjectBorrowedBodyTexturesIntoObservedJadeSkins()
    {
        const string target = "assets/characters/jade_lux/skins/skin301/lux_skin07_tx_cm.tex";
        var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
        {
            "assets/characters/lux/skins/skin07/lux_skin07_tx_cm.tex",
            "assets/characters/jade_lux/skins/skin301/lux_borrowed.skn"
        }));
        var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(target), 42 });
        guesser.GuessCharactersFiles(engine, CancellationToken.None, new[] { "jade_lux" });
        Assert.Equal(target, Assert.Single(engine.Matches).Value.Path);
        Assert.Contains(42UL, engine.UnknownHashes);
    }

    [Fact]
    public void CancellationPreservesPartialMatchesAndAllowsRetry()
    {
        const string other = "assets/ui/cards/sun_keeper_large.tex";
        using var cancellation = new CancellationTokenSource();
        var engine = new HashGuessEngine(HashGuessDomain.Game,
            new HashSet<ulong> { XxHash64Ext.Hash(Target), XxHash64Ext.Hash(other) }, _ => cancellation.Cancel());
        var index = new GameBinLinkTemplateIndex();
        index.Guess(engine, Tree("MoonGuardian", XxHash64Ext.Hash(Target)), Known, null, null, "pending", 2, CancellationToken.None);
        index.Guess(engine, Tree("SunKeeper", XxHash64Ext.Hash(other)), Known, null, null, "pending", 3, CancellationToken.None);
        Assert.Throws<OperationCanceledException>(() => index.Guess(engine,
            Tree("ForestSpirit", XxHash64Ext.Hash(Seed)), Known, null, null, "seed", 1, cancellation.Token));
        Assert.Single(engine.Matches);
        index.Guess(engine, Tree("ForestSpirit", XxHash64Ext.Hash(Seed)), Known, null, null, "seed", 1, CancellationToken.None);
        Assert.Equal(2, engine.Matches.Count);
    }

    [Fact]
    public void GrepIntegrationKeepsLearningIsolatedBetweenEngines()
    {
        var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[] { Seed }));
        var first = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { 42 });
        var second = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(Target) });
        void Grep(HashGuessEngine engine, BinTree tree)
        {
            using var stream = new MemoryStream();
            tree.Write(stream);
            guesser.GrepWad(engine, new ArraySegment<byte>(stream.ToArray()), "unknown.bin", "example.wad", 1);
        }
        Grep(first, Tree("ForestSpirit", XxHash64Ext.Hash(Seed)));
        Grep(second, Tree("MoonGuardian", XxHash64Ext.Hash(Target)));
        Assert.Empty(second.Matches);
        Grep(second, Tree("ForestSpirit", XxHash64Ext.Hash(Seed)));
        Assert.Equal(Target, Assert.Single(second.Matches).Value.Path);
    }
}
