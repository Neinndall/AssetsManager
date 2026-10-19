using System;
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

public class GameThemeMeshTextureTests
{
    [Theory]
    [InlineData("coven", "petexample_coven_coven_tier1", "petexample_coven_coven_weapon_tx_cm.tex")]
    [InlineData("base", "petexample_base_classic_tier1", "petexample_base_weapon_tx_cm.tex")]
    [InlineData("theme1", "petexample_theme1_classic_tier1", "petexample_theme1_weapon_tx_cm.tex")]
    [InlineData("theme1_extra", "petexample_theme1_extra_classic_tier1", "petexample_theme1_extra_weapon_tx_cm.tex")]
    public void GrepFindsThemeRootTexturesFromTierMeshesAndSkinNames(string theme, string skinName, string name)
    {
        string target = $"assets/characters/petexample/themes/{theme}/{name}";
        string legacy = $"assets/characters/petexample/themes/{theme}/tier1/petexample_{theme}_tier1_weapon_tx_cm.tex";
        var mesh = new BinTreeEmbedded(Fnv1a.HashLower("skinMeshProperties"), Fnv1a.HashLower("SkinMeshDataProperties"), new BinTreeProperty[]
        {
            new BinTreeString(Fnv1a.HashLower("simpleSkin"), $"ASSETS/Characters/PetExample/Themes/{theme}/Tier1/PetExample_{theme}_Tier1.skn"),
            new BinTreeUnorderedContainer(Fnv1a.HashLower("materialOverride"), BinPropertyType.Embedded, new[]
            {
                new BinTreeEmbedded(0, Fnv1a.HashLower("SkinMeshDataProperties_MaterialOverride"), new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("submesh"), "Weapon"),
                    new BinTreeWadChunkLink(Fnv1a.HashLower("texture"), XxHash64Ext.Hash(target))
                }),
                new BinTreeEmbedded(0, Fnv1a.HashLower("SkinMeshDataProperties_MaterialOverride"), new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("submesh"), "Weapon"),
                    new BinTreeWadChunkLink(Fnv1a.HashLower("texture"), XxHash64Ext.Hash(legacy))
                })
            })
        });
        var tree = new BinTree(new[] { new BinTreeObject("Characters/PetExample/Skins/Skin2", "SkinCharacterDataProperties", new BinTreeProperty[]
        {
            new BinTreeString(Fnv1a.HashLower("championSkinName"), skinName), mesh
        }) }, Array.Empty<string>());
        using var stream = new MemoryStream();
        tree.Write(stream);
        var engine = new HashGuessEngine(HashGuessDomain.Game, new[] { XxHash64Ext.Hash(target), XxHash64Ext.Hash(legacy), 42UL }.ToHashSet());
        var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, Array.Empty<string>()));
        game.GrepWad(engine, new ArraySegment<byte>(stream.ToArray()), "data/characters/petexample/skins/skin2.bin", "Companions.wad.client", 77, CancellationToken.None);
        Assert.Equal(new[] { legacy, target }.Order(), engine.Matches.Values.Select(match => match.Path).Order());
        Assert.All(engine.Matches.Values, match => Assert.Equal(77UL, match.SourceChunkHash));
    }
}
