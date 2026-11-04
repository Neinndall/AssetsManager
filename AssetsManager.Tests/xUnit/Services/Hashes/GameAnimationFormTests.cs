using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers.Game;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Hashes;

public sealed class GameAnimationFormTests
{
    private static BinTree Tree(params BinTreeProperty[] properties) => new(
        new[] { new BinTreeObject(1, 2, properties) }, Array.Empty<string>());
    private static BinTreeProperty Scope(string type, params BinTreeProperty[] properties) =>
        new BinTreeEmbedded(Fnv1a.HashLower(type), Fnv1a.HashLower(type), properties);
    private static BinTreeProperty Text(string field, string value) => new BinTreeString(Fnv1a.HashLower(field), value);
    private static BinTree Link(ulong hash) => Tree(Scope("AnimationResourceData",
        new BinTreeWadChunkLink(Fnv1a.HashLower("mAnimationFilePath"), hash)));
    private static BinTree Metadata() => Tree(Scope("GearData", Text("mEquipAnimation", "Transition_In_Mode02")),
        Scope("SkinMeshDataProperties", Text("skeleton", "ASSETS/Characters/Example/Skins/Skin9/Form3/Example_Form3.skl")));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UsesTypedSkinMetadataAcrossBinsInEitherOrder(bool graphFirst)
    {
        const string prefix = "assets/characters/example/skins/skin9/animations/";
        string[] expected = { prefix + "form3_recall.anm", prefix + "run_to_idle_mode02.anm" };
        var unknown = new HashSet<ulong> { XxHash64Ext.Hash(expected[0]), XxHash64Ext.Hash(expected[1]), 42 };
        var engine = new HashGuessEngine(HashGuessDomain.Game, unknown);
        var index = new GameAnimationFormIndex();
        void Metadata() => index.Guess(engine, GameAnimationFormTests.Metadata(), "data/characters/example/skins/skin9.bin",
            _ => new[] { "recall.anm", "run_to_idle.anm" }, "metadata.wad", 1, CancellationToken.None);
        void Graph()
        {
            foreach (string path in expected)
                index.Guess(engine, Link(XxHash64Ext.Hash(path)), "data/characters/example/animations/skin9.bin",
                    _ => new[] { "recall.anm", "run_to_idle.anm" }, "graph.wad", 2, CancellationToken.None);
        }
        if (graphFirst) { Graph(); Metadata(); } else { Metadata(); Graph(); }
        Assert.Equal(2, engine.Matches.Count);
        foreach (string path in expected)
        {
            var match = engine.Matches[XxHash64Ext.Hash(path)];
            Assert.Equal(path, match.Path);
            Assert.Equal("graph.wad", match.SourceWadPath);
            Assert.Equal(2UL, match.SourceChunkHash);
        }
        Assert.Contains(42UL, engine.UnknownHashes);
    }

    [Theory]
    [InlineData("data/characters/other/skins/skin9.bin")]
    [InlineData("data/characters/example/skins/skin10.bin")]
    public void DoesNotBorrowMetadataFromOtherCharactersOrSkins(string otherSource)
    {
        ulong target = XxHash64Ext.Hash("assets/characters/example/skins/skin9/animations/form3_recall.anm");
        var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { target });
        var index = new GameAnimationFormIndex();
        index.Guess(engine, Metadata(), otherSource, _ => new[] { "recall.anm" }, "metadata", 1, CancellationToken.None);
        index.Guess(engine, Link(target), "data/characters/example/animations/skin9.bin",
            _ => new[] { "recall.anm" }, "graph", 2, CancellationToken.None);
        Assert.Empty(engine.Matches);
    }

    [Fact]
    public void IgnoresNumberedStringsOutsideSkinAndGearProperties()
    {
        ulong target = XxHash64Ext.Hash("assets/characters/example/skins/skin9/animations/form3_recall.anm");
        var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { target });
        var index = new GameAnimationFormIndex();
        index.Guess(engine, Tree(Text("description", "Form3")), "data/characters/example/skins/skin9.bin",
            _ => new[] { "recall.anm" }, "metadata", 1, CancellationToken.None);
        index.Guess(engine, Link(target), "data/characters/example/animations/skin9.bin",
            _ => new[] { "recall.anm" }, "graph", 2, CancellationToken.None);
        Assert.Empty(engine.Matches);
    }

    [Fact]
    public void SkinRoleTexturesTraverseNestedGearMeshes()
    {
        const string expected = "assets/characters/example/skins/skin9/example_skin9_instrument_tx_cm.tex";
        ulong target = XxHash64Ext.Hash(expected);
        var materialOverride = Scope("SkinMeshDataProperties_MaterialOverride",
            Text("submesh", "instrument"), new BinTreeWadChunkLink(Fnv1a.HashLower("texture"), target));
        var mesh = new BinTreeEmbedded(Fnv1a.HashLower("skinMeshProperties"), Fnv1a.HashLower("SkinMeshDataProperties"),
            new BinTreeProperty[] { new BinTreeContainer(Fnv1a.HashLower("materialOverride"), BinPropertyType.Embedded,
                new[] { materialOverride }) });
        var tree = Tree(Scope("GearData", mesh));
        using var bytes = new MemoryStream();
        tree.Write(bytes);
        var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { target, 42 });
        var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, Array.Empty<string>()));
        guesser.GuessSkinRoleTextures(engine, new ArraySegment<byte>(bytes.ToArray()),
            "data/characters/example/skins/skin9.bin", "example.wad", 1, CancellationToken.None);
        Assert.Equal(expected, Assert.Single(engine.Matches).Value.Path);
        Assert.Contains(42UL, engine.UnknownHashes);
    }
}
