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

public class GameUiObjectivesTests
{
    [Theory]
    [InlineData("Kiwi", "ViewPane/Content/ObjectiveTemplate/Background", "objective_background.png", 0xc185276f0600818bUL, true)]
    [InlineData("Kiwi", "MainHeaderGroup/MainHeaderButton/Background", "header_background.png", 0xc9ff7f4d07f40ff8UL, true)]
    [InlineData("Kiwi", "ViewPane/Background", "panel_background.png", 0x381e049303673d82UL, true)]
    [InlineData("Kiwi", "ViewPane/Content/ObjectiveTemplate/CompletedGroup/CompletedBackground", "objective_background_completed.png", 0x361afe4511618bd2UL, true)]
    [InlineData("Kiwi", "ViewPane/Content/ObjectiveTemplate/CompletedGroup/Completed_Icon", "completed_circle.png", 0xc51a36a6181f602fUL, true)]
    [InlineData("Kiwi", "ViewPane/Content/ObjectiveTemplate/FillMeter_Back", "progress_circle_background.png", 0xb7d8b390081224c4UL, true)]
    [InlineData("Kiwi", "ViewPane/Content/ObjectiveTemplate/FillMeter_IncomingProgress", "progress_circle_glow.png", 0x5cd5bd4f7178185eUL, true)]
    [InlineData("Kiwi", "ViewPane/Content/ObjectiveTemplate/FillMeter_Progress", "progress_circle.png", 0xdf1e9b9313314855UL, true)]
    [InlineData("Kiwi", "ViewPane/Slider/Slider_Slider", "scrollbar.png", 0x541256652fccc411UL, true)]
    [InlineData("Kiwi", "ViewPane/Background_GradientScene/Background_Gradient", "panel_gradient_bottom.png", 0xc14160bda7aedc25UL, true)]
    [InlineData("Kiwi", "MainHeaderGroup/MainHeaderButton/Icon_Collapsed", "caret.png", 0x30a040c0176fec42UL, true)]
    [InlineData("Kiwi", "MainHeaderGroup/MainHeaderButton/BackgroundHover", "header_background_hover.png", 0xe549971b00464babUL, true)]
    [InlineData("Future", "ViewPane/Content/ObjectiveTemplate/FillMeter_Progress", "progress_circle.png", 0UL, true)]
    [InlineData("Kiwi", "UnrelatedGroup/Background", "objective_background.png", 0xc185276f0600818bUL, false)]
    [InlineData("Kiwi", "ViewPane/Content/ObjectiveTemplate/FillMeter_Progress", "progress_circle_glow.png", 0x5cd5bd4f7178185eUL, false)]
    public void ObjectiveLabelsResolveOnlyTheirOwningLinks(string mode, string element, string file, ulong target, bool expectedMatch)
    {
        string entry = $"ClientStates/Gameplay/UX/LoL/{mode}/ModesObjectives/UIBase/ModesObjectives/{element}";
        string expected = $"assets/ux/{mode.ToLowerInvariant()}/objectives/{file}";
        if (target == 0) target = XxHash64Ext.Hash(expected);
        var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, Array.Empty<string>()),
            resolveBinHash: hash => hash == Fnv1a.HashLower(entry) ? entry : null);
        string elementClass = element.EndsWith("FillMeter_Progress", StringComparison.Ordinal) ||
                              element.EndsWith("FillMeter_IncomingProgress", StringComparison.Ordinal)
            ? "UiElementEffectArcFillData" : "UiElementIconData";
        var tree = new BinTree(new[]
        {
            new BinTreeObject(Fnv1a.HashLower(entry), Fnv1a.HashLower(elementClass), new BinTreeProperty[]
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

        guesser.GrepWad(engine, new ArraySegment<byte>(stream.ToArray()),
            $"clientstates/gameplay/ux/lol/{mode.ToLowerInvariant()}/modesobjectives/uibase.bin",
            "UI.wad.client", 123, CancellationToken.None);

        if (expectedMatch)
        {
            var match = Assert.Single(engine.Matches).Value;
            Assert.Equal(expected, match.Path);
            Assert.Equal("UI.wad.client", match.SourceWadPath);
            Assert.Equal(123UL, match.SourceChunkHash);
            Assert.Empty(engine.UnknownHashes);
        }
        else
        {
            Assert.Empty(engine.Matches);
            Assert.Single(engine.UnknownHashes);
        }
    }
}
