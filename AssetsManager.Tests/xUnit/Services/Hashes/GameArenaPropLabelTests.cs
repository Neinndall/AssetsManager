using System;
using System.IO;
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

public sealed class GameArenaPropLabelTests
{
    [Fact]
    public void GrepUsesMatchingArenaSkinLabelAndMeshFolder()
    {
        const string mesh = "ASSETS/Characters/TFT_Example_Props/Skins/Skin05/Example.skn";
        const string target = "assets/characters/tft_example_props/skins/skin05/tft_arenaskin_example_broom_tx.tex";
        var skin = new BinTreeObject("Characters/TFT_Example_Props/Skins/Skin5", "SkinCharacterDataProperties",
            new BinTreeProperty[]
            {
                new BinTreeString(Fnv1a.HashLower("championSkinName"), "Example_Broom"),
                new BinTreeStruct(Fnv1a.HashLower("skinMeshProperties"), Fnv1a.HashLower("SkinMeshDataProperties"),
                    new BinTreeProperty[]
                    {
                        new BinTreeString(Fnv1a.HashLower("simpleSkin"), mesh),
                        new BinTreeWadChunkLink(Fnv1a.HashLower("texture"), XxHash64Ext.Hash(target))
                    })
            });
        using var stream = new MemoryStream();
        new BinTree(new[] { skin }, Array.Empty<string>()).Write(stream);
        var engine = new HashGuessEngine(HashGuessDomain.Game, new[] { XxHash64Ext.Hash(target) }.ToHashSet());
        new GameHashGuesser().GrepWad(engine, new ArraySegment<byte>(stream.ToArray()),
            "data/characters/tft_example_props/skins/skin5.bin", "Map22.wad.client", 1, CancellationToken.None);
        Assert.Equal(target, Assert.Single(engine.Matches.Values).Path);
    }

    [Theory]
    [InlineData("tft_props", "Example_Broom")]
    [InlineData("tft__props", "Example_Broom")]
    [InlineData("tft_example_props", "Other_Broom")]
    [InlineData("tft_example_props", "Example_../Broom")]
    [InlineData("example", "Example_Broom")]
    public void RejectsUnrelatedOrInvalidLabels(string owner, string label)
    {
        Assert.Empty(GameHashGuesser.EnumerateArenaPropLabelTextures($"assets/characters/{owner}/skins/skin05/mesh.skn", label));
    }
}
