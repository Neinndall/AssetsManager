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
    [InlineData("Jade", "Background", "wheel_default.png", 0x22761269e893db46UL, true)]
    [InlineData("Jade", "Center_BG", "wheel_center_default.png", 0xc6b783ffc9bdda9eUL, true)]
    [InlineData("Jade", "1N_ButtonHover", "wheel_highlight_01.png", 0x20ee9ead5cfe5b09UL, true)]
    [InlineData("Jade", "2NE_ButtonHover", "wheel_highlight_02.png", 0xf1140103ae2cb906UL, true)]
    [InlineData("Jade", "3E_ButtonHover", "wheel_highlight_03.png", 0xadc1422ecf55b0aaUL, true)]
    [InlineData("Jade", "4SE_ButtonHover", "wheel_highlight_04.png", 0x1fe9e5ce17469b94UL, true)]
    [InlineData("Jade", "5S_ButtonHover", "wheel_highlight_05.png", 0xcb1625c7077d791bUL, true)]
    [InlineData("Jade", "6SW_ButtonHover", "wheel_highlight_06.png", 0x3111e424539e0c60UL, true)]
    [InlineData("Jade", "7W_ButtonHover", "wheel_highlight_07.png", 0x90ad076b02827f27UL, true)]
    [InlineData("Jade", "8NW_ButtonHover", "wheel_highlight_08.png", 0x2c304d856264219fUL, true)]
    [InlineData("Future", "3E_ButtonHover", "wheel_highlight_03.png", 0UL, true)]
    [InlineData("Jade", "1N_ButtonDefault", "wheel_highlight_01.png", 0x20ee9ead5cfe5b09UL, false)]
    [InlineData("Jade", "1N_ButtonHover", "wheel_highlight_02.png", 0xf1140103ae2cb906UL, false)]
    public void GrepResolvesPingWheelLabelsOnlyAgainstTheirOwnLinks(
        string mode, string label, string file, ulong target, bool shouldMatch)
    {
        string modeName = mode.ToLowerInvariant();
        string entry = $"ClientStates/Gameplay/UX/LoL/{mode}/{mode}PingRadial/UIBase/PingWheel/{label}";
        string expected = $"assets/ux/{modeName}/hud/{modeName}_ping_{file}";
        if (target == 0) target = XxHash64Ext.Hash(expected);
        var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, Array.Empty<string>()),
            resolveBinHash: hash => hash == Fnv1a.HashLower(entry) ? entry : null);
        var tree = new BinTree(new[]
        {
            new BinTreeObject(Fnv1a.HashLower(entry), Fnv1a.HashLower("UiElementIconData"), new BinTreeProperty[]
            {
                new BinTreeHash(Fnv1a.HashLower("name"), Fnv1a.HashLower(entry)),
                new BinTreeStruct(Fnv1a.HashLower("TextureData"), Fnv1a.HashLower("LooseUiTextureData"), new BinTreeProperty[]
                {
                    new BinTreeWadChunkLink(Fnv1a.HashLower("TextureName"), target)
                })
            })
        }, Array.Empty<string>());
        using var stream = new MemoryStream();
        tree.Write(stream);
        var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { target });

        game.GrepWad(engine, new ArraySegment<byte>(stream.ToArray()),
            $"clientstates/gameplay/ux/lol/{modeName}/{modeName}pingradial/uibase.bin",
            "UI.wad.client", 123, CancellationToken.None);

        if (shouldMatch)
        {
            Assert.Equal(expected, Assert.Single(engine.Matches.Values).Path);
            Assert.Empty(engine.UnknownHashes);

            engine.EnsureUnknown(ulong.MaxValue);
            using var atlas = new MemoryStream();
            using (var writer = new BinaryWriter(atlas, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                writer.Write("IMAA"u8);
                writer.Write(1U);
                writer.Write(1234UL);
                writer.Write(1U);
                writer.Write(target);
                writer.Write(0f);
                writer.Write(0f);
                writer.Write(1f);
                writer.Write(1f);
                writer.Write(0U);
            }
            game.GrepWad(engine, new ArraySegment<byte>(atlas.ToArray()), ".bin", "UI.wad.client", 456, CancellationToken.None);
            Assert.Equal(ulong.MaxValue, Assert.Single(engine.UnknownHashes));
            Assert.Single(engine.Matches);
        }
        else
        {
            Assert.Empty(engine.Matches);
            Assert.Single(engine.UnknownHashes);
        }
    }

    [Theory]
    [InlineData("Kiwi", "KDA_Ahri", "kda", "ahri", "noise", 0x78f7551de0ee85b7UL, true)]
    [InlineData("Kiwi", "Techno_Viktor", "psyops", "viktor", "psyops", 0x75d555980815bac7UL, true)]
    [InlineData("Kiwi", "Latin_RenataGlasc", "lailusion", "renata", "lailusionexploration", 0xd1b5596d82fbca27UL, true)]
    [InlineData("Kiwi", "Worlds_Neeko", "cosplayer", "neeko", "noise", 0x427b8da1a67fb24bUL, true)]
    [InlineData("Future", "Worlds_TestHero", "cosplayer", "testhero", "noise", 0UL, true)]
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
