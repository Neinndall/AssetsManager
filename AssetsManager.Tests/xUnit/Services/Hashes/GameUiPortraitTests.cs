using System;
using System.Collections.Generic;
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

public sealed class GameUiPortraitTests
{
    [Theory]
    [InlineData("Kiwi", "KDA_Ahri", "kda", "ahri", "noise", 0x78f7551de0ee85b7UL, true)]
    [InlineData("Kiwi", "Techno_Viktor", "psyops", "viktor", "psyops", 0x75d555980815bac7UL, true)]
    [InlineData("Kiwi", "Latin_RenataGlasc", "lailusion", "renata", "lailusionexploration", 0xd1b5596d82fbca27UL, true)]
    [InlineData("Future", "Festival_TestHeroLong", "arcanum", "testhero", "arcanumexploration", 0UL, true)]
    [InlineData("Future", "Festival_TestHeroLong", "arcanum", "testhero", "arcanumexploration", 0UL, false)]
    public void GrepResolvesNarrativePortraitsFromUiLabelsAndCorpus(
        string mode, string label, string theme, string character, string vocabulary, ulong expectedHash, bool matchingLink)
    {
        string entry = $"ClientStates/Gameplay/UX/LoL/{mode}/{mode}NarrativeBarks/UIBase/NarrativeBark/ChampionSplash_{label}";
        string expected = $"assets/ux/narrativebarks/{mode.ToLowerInvariant()}/portrait_{theme}_{character}.png";
        ulong target = expectedHash == 0 ? XxHash64Ext.Hash(expected) : expectedHash;
        ulong linkedTarget = matchingLink ? target : XxHash64Ext.Hash("assets/ux/unrelated.png");
        var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game,
            new[] { $"assets/characters/{character}/skins/base/particles/{vocabulary}_trail.tex" }),
            resolveBinHash: hash => hash == Fnv1a.HashLower(entry) ? entry : null);
        var tree = new BinTree(new[]
        {
            new BinTreeObject(Fnv1a.HashLower(entry), Fnv1a.HashLower("UiElementIconData"), new BinTreeProperty[]
            {
                new BinTreeHash(Fnv1a.HashLower("name"), Fnv1a.HashLower(entry)),
                new BinTreeStruct(Fnv1a.HashLower("TextureData"), Fnv1a.HashLower("LooseUiTextureData"), new BinTreeProperty[]
                {
                    new BinTreeWadChunkLink(Fnv1a.HashLower("TextureName"), linkedTarget)
                })
            })
        }, Array.Empty<string>());
        using var stream = new MemoryStream();
        tree.Write(stream);
        var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { target, linkedTarget });

        game.GrepWad(engine, new ArraySegment<byte>(stream.ToArray()),
            $"clientstates/gameplay/ux/lol/{mode.ToLowerInvariant()}/{mode.ToLowerInvariant()}narrativebarks/uibase.bin",
            "UI.wad.client", 123, CancellationToken.None);

        if (!matchingLink)
        {
            Assert.Empty(engine.Matches);
            Assert.Equal(2, engine.RemainingUnknownCount);
            return;
        }

        var match = Assert.Single(engine.Matches.Values);
        Assert.Equal(expected, match.Path);
        Assert.Equal("UI.wad.client", match.SourceWadPath);
        Assert.Equal(123UL, match.SourceChunkHash);
        Assert.Empty(engine.UnknownHashes);
    }
}
