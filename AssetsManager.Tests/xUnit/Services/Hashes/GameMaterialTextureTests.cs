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

public class GameMaterialTextureTests
{
    [Theory]
    [InlineData("skins", "skin9")]
    [InlineData("themes", "summer")]
    public void SharedParticleNamesRequireTheOwningSamplerLink(string kind, string container)
    {
        string target = $"assets/characters/target/{kind}/{container}/particles/waves_sharp.tex";
        string competitor = $"assets/characters/target/{kind}/{container}/particles/d.tex";
        var index = new GameMaterialTextureIndex(new[]
        {
            "assets/shared/materials/waves_sharp.tex",
            "assets/maps/particles/tft/booms/fireplant/d.tex"
        }, CancellationToken.None);
        var tree = new BinTree(new[] { Material("unused", "DiffuseTexture", target) }, Array.Empty<string>());
        var engine = Engine(target, competitor);
        string source = $"data/characters/target/{kind}/{container}.bin";
        Assert.False(GameMaterialTextureIndex.HasUnresolvedContext(engine, tree, source, CancellationToken.None));
        Assert.True(GameMaterialTextureIndex.HasUnresolvedSamplerContext(engine, tree, source, CancellationToken.None));
        index.GuessSharedParticles(engine, tree, source, "Target.wad.client", 77, CancellationToken.None);
        var match = Assert.Single(engine.Matches).Value;
        Assert.Equal(target, match.Path);
        Assert.Equal("Target.wad.client", match.SourceWadPath);
        Assert.Equal(77UL, match.SourceChunkHash);
        Assert.Contains(XxHash64Ext.Hash(competitor), engine.UnknownHashes);
    }

    [Fact]
    public void SharedParticleSearchResumesInterruptedScopesAndPreservesVocabularyBoundaries()
    {
        const string target = "assets/characters/target/skins/skin9/particles/z.tex";
        const string source = "data/characters/target/skins/skin9.bin";
        var index = new GameMaterialTextureIndex(new[]
        {
            "assets/maps/particles/booms/a.tex", "assets/shared/materials/z.tex",
            "assets/shared/materials/ignored.patch.tex", "assets/characters/foreign/skins/base/unrelated.tex"
        }, CancellationToken.None);
        var tree = new BinTree(new[] { Material("unused", "Top_Texture", target) }, Array.Empty<string>());
        var engine = Engine(target);
        Assert.Equal(1L, index.GuessSharedParticles(engine, tree, source, "one", 77, CancellationToken.None, 1));
        Assert.Empty(engine.Matches);
        Assert.Equal(2L, index.GuessSharedParticles(engine, tree, source, "one", 77, CancellationToken.None));
        Assert.Equal(target, Assert.Single(engine.Matches).Value.Path);
        var other = Engine(target);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => index.GuessSharedParticles(other, tree, source, "two", 88, cancellation.Token));
        Assert.Equal(0L, index.GuessSharedParticles(other, tree, "data/maps/map11.bin", "two", 88, CancellationToken.None));
        index.GuessSharedParticles(other, tree, source, "two", 88, CancellationToken.None);
        Assert.Equal("two", Assert.Single(other.Matches).Value.SourceWadPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GrepPreservesUnicodeAndLongMaterialPaths(bool longName)
    {
        string character = longName ? "target" : "例示";
        string stem = longName ? new string('雪', 600) : "下层";
        string name = $"Characters/{character}/Skins/Skin9/Materials/{character}_Skin9_{stem}";
        string target = $"assets/characters/{character}/skins/skin9/{character}_skin9_{stem}_mult.tex";
        var tree = new BinTree(new[]
        {
            Material(name, "Diffuse_Texture", target, new BinTreeHash64(Fnv1a.HashLower("name"), 123))
        }, Array.Empty<string>());
        using var stream = new MemoryStream();
        tree.Write(stream);
        var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, Array.Empty<string>()),
            resolveBinHash64: hash => hash == 123 ? name : hash.ToString("x16"));
        var engine = Engine(target);
        guesser.GrepWad(engine, new ArraySegment<byte>(stream.ToArray()), $"data/characters/{character}/skins/skin9.bin", "Target.wad.client", 77, CancellationToken.None);
        Assert.Equal(target, Assert.Single(engine.Matches).Value.Path);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("wide")]
    [InlineData("entry")]
    [InlineData("missing")]
    public void GrepUsesTextWideAndEntryMaterialIdentities(string identity)
    {
        const string name = "Characters/Target/Skins/Skin9/Materials/Target_Skin9_Lower";
        const string target = "assets/characters/target/skins/skin9/target_skin9_lower_mult.tex";
        var material = Material(name, "Diffuse_Texture", target,
            identity == "text" ? new BinTreeString(Fnv1a.HashLower("name"), name) : new BinTreeHash64(Fnv1a.HashLower("name"), 123));
        var tree = new BinTree(new[] { material }, Array.Empty<string>());
        using var stream = new MemoryStream();
        tree.Write(stream);
        var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, Array.Empty<string>()),
            resolveBinHash: hash => identity == "entry" && hash == material.PathHash ? name : hash.ToString("x8"),
            resolveBinHash64: hash => identity == "wide" && hash == 123 ? name : hash.ToString("x16"));
        var engine = Engine(target);

        guesser.GrepWad(engine, new ArraySegment<byte>(stream.ToArray()), "data/characters/target/skins/skin9.bin", "Target.wad.client", 77, CancellationToken.None);

        if (identity == "missing") Assert.Empty(engine.Matches);
        else
        {
            var match = Assert.Single(engine.Matches).Value;
            Assert.Equal(target, match.Path);
            Assert.Equal(77UL, match.SourceChunkHash);
        }
    }

    [Theory]
    [InlineData("skins", "skin9")]
    [InlineData("themes", "moon")]
    public void SamplerVocabularyRequiresTheExactTargetAndPreservesEvidence(string kind, string container)
    {
        string folder = $"assets/characters/target/{kind}/{container}/";
        string target = folder + $"target_{container}_body_flowmap_tx_cm.tex";
        string competitor = folder + "body_flowmap_tx_cm.tex";
        var tree = new BinTree(new[] { Material("unused", "FlowMap", target) }, Array.Empty<string>());
        var index = Index();
        var engine = Engine(target, competitor);

        index.Guess(engine, tree, $"data/characters/target/{kind}/{container}.bin", "Target.wad.client", 77, CancellationToken.None);

        var match = Assert.Single(engine.Matches).Value;
        Assert.Equal(target, match.Path);
        Assert.Equal("Target.wad.client", match.SourceWadPath);
        Assert.Equal(77UL, match.SourceChunkHash);
        Assert.Contains(XxHash64Ext.Hash(competitor), engine.UnknownHashes);
    }

    [Fact]
    public void BudgetedScopesCanResumeAndTheCorpusCanServeAnotherEngine()
    {
        const string target = "assets/characters/target/skins/skin9/target_skin9_body_flowmap_tx_cm.tex";
        const string source = "data/characters/target/skins/skin9.bin";
        var tree = new BinTree(new[] { Material("unused", "FlowMap", target) }, Array.Empty<string>());
        var index = Index();
        var engine = Engine(target);
        Assert.Equal(1L, index.Guess(engine, tree, source, "one", 77, CancellationToken.None, candidateBudget: 1));
        Assert.Empty(engine.Matches);
        index.Guess(engine, tree, source, "one", 77, CancellationToken.None);
        Assert.Equal(target, Assert.Single(engine.Matches).Value.Path);

        var other = Engine(target);
        index.Guess(other, tree, source, "two", 88, CancellationToken.None);
        Assert.Equal("two", Assert.Single(other.Matches).Value.SourceWadPath);
    }

    [Fact]
    public void CompletedScopesAreDeduplicatedAndUnrelatedFilesAreIgnored()
    {
        const string missing = "assets/characters/target/skins/skin9/unattested.tex";
        var tree = new BinTree(new[] { Material("unused", "FlowMap", missing) }, Array.Empty<string>());
        var index = Index();
        var engine = Engine(missing);
        Assert.Equal(0L, index.Guess(engine, tree, "data/maps/map11.bin", "one", 77, CancellationToken.None));
        Assert.True(index.Guess(engine, tree, "data/characters/target/skins/skin9.bin", "one", 77, CancellationToken.None) > 0);
        Assert.Equal(0L, index.Guess(engine, tree, "data/characters/target/skins/skin9.bin", "one", 77, CancellationToken.None));
        Assert.Empty(engine.Matches);
    }

    [Fact]
    public void SamplerWorkHonorsCancellation()
    {
        const string target = "assets/characters/target/skins/skin9/unattested.tex";
        var tree = new BinTree(new[] { Material("unused", "FlowMap", target) }, Array.Empty<string>());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => Index().Guess(Engine(target), tree,
            "data/characters/target/skins/skin9.bin", "one", 77, cancellation.Token));
    }

    [Fact]
    public void FailedBinParsingDoesNotEnterMaterialVocabulary()
    {
        var index = Index();
        var engine = Engine("assets/unattested.tex");
        Assert.False(GameMaterialTextureIndex.HasUnresolvedContext(engine, null, "data/characters/target/skins/skin9.bin", CancellationToken.None));
        Assert.Equal(0L, index.Guess(engine, null, "data/characters/target/skins/skin9.bin", "one", 77, CancellationToken.None));
        var guesser = new GameHashGuesser();
        guesser.GrepWad(engine, new ArraySegment<byte>("PROP"u8.ToArray()),
            "data/characters/target/skins/skin9.bin", "Target.wad.client", 77, CancellationToken.None);
        Assert.Empty(engine.Matches);
    }
    [Fact]
    public void DynamicOptionsShareOneBasenamePassAndKeepOtherLinksPending()
    {
        const string directory = "assets/characters/target/skins/skin9/";
        var index = new GameMaterialTextureIndex(new[]
        {
            "assets/characters/donor/skins/base/a.tex",
            "assets/characters/donor/skins/base/b.tex",
            "assets/characters/donor/skins/base/c.tex"
        }, CancellationToken.None);
        var tree = DynamicMaterial(directory + "a.tex", directory + "c.tex", directory + "c.tex");
        var engine = Engine(directory + "a.tex", directory + "b.tex", directory + "c.tex");
        const string source = "data/characters/target/skins/skin9.bin";
        Assert.True(GameMaterialTextureIndex.HasUnresolvedDynamicMaterialContext(engine, tree, source, CancellationToken.None));

        Assert.Equal(3L, index.GuessDynamicMaterialOptions(engine, tree, source, "Target.wad.client", 77, CancellationToken.None));

        Assert.Equal(3L, engine.CheckedCandidates);
        Assert.Equal(2, engine.Matches.Count);
        Assert.Equal(XxHash64Ext.Hash(directory + "b.tex"), Assert.Single(engine.UnknownHashes));
        Assert.All(engine.Matches.Values, match =>
        {
            Assert.Equal("Target.wad.client", match.SourceWadPath);
            Assert.Equal(77UL, match.SourceChunkHash);
        });
        Assert.False(GameMaterialTextureIndex.HasUnresolvedDynamicMaterialContext(engine, tree, source, CancellationToken.None));
        Assert.Equal(0L, index.GuessDynamicMaterialOptions(engine, tree, source, "Target.wad.client", 77, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DynamicOptionsResumeInterruptedTargetsAndAcceptLaterLinks(bool cancelAfterMatch)
    {
        const string directory = "assets/characters/target/skins/skin9/";
        const string source = "data/characters/target/skins/skin9.bin";
        var index = new GameMaterialTextureIndex(new[]
        {
            "assets/characters/donor/skins/base/a.tex",
            "assets/characters/donor/skins/base/b.tex",
            "assets/characters/donor/skins/base/z.tex"
        }, CancellationToken.None);
        var tree = DynamicMaterial(directory + "a.tex", directory + "z.tex");
        using var cancellation = new CancellationTokenSource();
        var engine = new HashGuessEngine(HashGuessDomain.Game,
            new HashSet<ulong>(new[] { "a.tex", "b.tex", "z.tex" }.Select(name => XxHash64Ext.Hash(directory + name))),
            match => { if (cancelAfterMatch && match.Path == directory + "a.tex") cancellation.Cancel(); });

        if (cancelAfterMatch)
            Assert.Throws<OperationCanceledException>(() => index.GuessDynamicMaterialOptions(engine, tree, source,
                "one", 77, cancellation.Token));
        else
            Assert.Equal(1L, index.GuessDynamicMaterialOptions(engine, tree, source, "one", 77, CancellationToken.None, 1));

        Assert.Single(engine.Matches);
        Assert.Equal(3L, index.GuessDynamicMaterialOptions(engine, tree, source, "two", 88, CancellationToken.None));
        Assert.Equal(2, engine.Matches.Count);
        Assert.Equal(XxHash64Ext.Hash(directory + "b.tex"), Assert.Single(engine.UnknownHashes));
        Assert.Equal(2L, index.GuessDynamicMaterialOptions(engine, DynamicMaterial(directory + "b.tex"), source,
            "three", 99, CancellationToken.None));
        Assert.Empty(engine.UnknownHashes);
    }

    private static BinTree DynamicMaterial(params string[] paths) => new(new[]
    {
        new BinTreeObject("Synthetic/Dynamic", "StaticMaterialDef", new BinTreeProperty[]
        {
            new BinTreeEmbedded(Fnv1a.HashLower("dynamicMaterial"), Fnv1a.HashLower("DynamicMaterialDef"), new BinTreeProperty[]
            {
                new BinTreeUnorderedContainer(Fnv1a.HashLower("textures"), BinPropertyType.Embedded, new[]
                {
                    new BinTreeEmbedded(0, Fnv1a.HashLower("DynamicMaterialTextureSwapDef"), new BinTreeProperty[]
                    {
                        new BinTreeUnorderedContainer(Fnv1a.HashLower("options"), BinPropertyType.Embedded,
                            paths.Select(path => new BinTreeEmbedded(0, Fnv1a.HashLower("DynamicMaterialTextureSwapOption"),
                                new BinTreeProperty[] { new BinTreeWadChunkLink(Fnv1a.HashLower("TextureName"), XxHash64Ext.Hash(path)) })))
                    })
                })
            })
        })
    }, Array.Empty<string>());

    private static GameMaterialTextureIndex Index() => new(new[]
    {
        "assets/characters/donor/skins/skin7/donor_skin7_body_flowmap_tx_cm.tex"
    }, CancellationToken.None);

    private static HashGuessEngine Engine(params string[] paths) => new(HashGuessDomain.Game, paths.Select(path => XxHash64Ext.Hash(path)).ToHashSet());

    private static BinTreeObject Material(string name, string role, string target, BinTreeProperty identity = null)
    {
        var properties = new List<BinTreeProperty>
        {
            new BinTreeUnorderedContainer(Fnv1a.HashLower("samplerValues"), BinPropertyType.Embedded,
                new[] { new BinTreeEmbedded(0, Fnv1a.HashLower("StaticMaterialShaderSamplerDef"), new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("TextureName"), role),
                    new BinTreeWadChunkLink(Fnv1a.HashLower("texturePath"), XxHash64Ext.Hash(target))
                }) })
        };
        if (identity != null) properties.Add(identity);
        return new BinTreeObject(name, "StaticMaterialDef", properties);
    }
}
