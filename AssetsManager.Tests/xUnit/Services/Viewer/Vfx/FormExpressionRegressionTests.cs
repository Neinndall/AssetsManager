using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetsManager.Services.Viewer.Animation;
using AssetsManager.Services.Viewer.Parsing;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Services.Viewer.Vfx.Parsing;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx;

public sealed class FormExpressionRegressionTests
{
    private static uint H(string name) => Fnv1a.HashLower(name);

    [Theory]
    [InlineData(0, 10u)]
    [InlineData(1, 11u)]
    [InlineData(2, 12u)]
    public void IntegerSwitchSelectsTheExactFormBranch(int gear, uint expected)
    {
        var clips = SwitchGraph();
        Assert.Equal(expected, Assert.Single(AnimationGraphPlayback.ResolvePlaylist(clips[0], clips, gearIndex: gear)).OwnerPathHash);
    }

    [Fact]
    public void MissingIntegerBranchDoesNotPlayAnotherFormsEvents()
    {
        var clips = SwitchGraph();
        Assert.Empty(AnimationGraphPlayback.ResolvePlaylist(clips[0], clips, gearIndex: 3));
        Assert.Empty(AnimationGraphPlayback.ResolvePlaylist(clips[0], clips, gearIndex: 1, available: path => path != "form1.anm"));
    }

    [Fact]
    public void AnimationFractionFollowsTimeAndSeekWhileUnrelatedClipsStayAtRest()
    {
        var driver = new BinTreeStruct(H("driver"), H("AnimationFractionDynamicMaterialFloatDriver"), new BinTreeProperty[]
        { new BinTreeHash(H("mAnimationName"), H("Dance")) });
        var material = new BinTreeStruct(H("dynamicMaterial"), H("DynamicMaterialDef"), new BinTreeProperty[]
        {
            new BinTreeContainer(H("parameters"), BinPropertyType.Embedded, new BinTreeProperty[]
            {
                new BinTreeEmbedded(0, H("DynamicMaterialParameterDef"), new BinTreeProperty[]
                { new BinTreeString(H("name"), "LUT_Macro_Time"), driver })
            })
        });
        var parameter = Assert.Single(SknDynamicMaterialParser.ReadParameters(new Dictionary<uint, BinTreeProperty> { [material.NameHash] = material }));
        var item = new AnimationClipCatalogItem("Dance", "Dance", "dance.anm", 4f, null, null, null,
            Array.Empty<AnimationClipTimedCue>(), 0, false, "");
        var state = GameMaterialState.Preview(null, item);
        foreach (float time in new[] { 0f, 1f, 3f, 3f, .5f })
            Assert.Equal(time / 4f, parameter.Evaluate(state with { Time = time })!.Value.X, 5);
        Assert.Equal(0f, parameter.Evaluate(GameMaterialState.Resting with { Time = 2f })!.Value.X);
    }

    [Theory]
    [InlineData("Genre2")]
    [InlineData("Genre3")]
    public void ModelsWithinTheSameChampionKeepTheirDistinctFormNames(string formName)
    {
        var data = new CharacterFormDocumentData(new uint?[] { 123 }, new Dictionary<uint, CharacterFormDataProjection>
        {
            [123] = new(Array.Empty<uint>(), Array.Empty<uint>(),
                $"ASSETS/Characters/Test/Skins/Skin77/{formName}/Test_Skin77_{formName}.skn", null, null,
                new Dictionary<uint, uint>(), Array.Empty<VfxIdleEffectDefinition>(), false, false)
        });
        var forms = CharacterFormParser.Resolve(new[] { data }, null,
            new VfxOwnerSceneContext("ASSETS/Characters/Test/Skins/Skin77/Genre1/Test_Skin77_Genre1.skn", "", 1f));
        Assert.Equal(formName, Assert.Single(forms).Name);
    }

    [Fact]
    public void IntegerPairValuesAreKeysRatherThanChildIndicesOrNearestParameters()
    {
        var clips = SwitchGraph(new[] { 2, 0, 1 });
        Assert.Equal(12u, Assert.Single(AnimationGraphPlayback.ResolvePlaylist(clips[0], clips, parameter: 2f, gearIndex: 1)).OwnerPathHash);
    }

    private static IReadOnlyList<AnimationClipDefinition> SwitchGraph(int[] values = null)
    {
        values ??= new[] { 0, 1, 2 };
        var pairs = Enumerable.Range(0, 3).Select(index => (BinTreeProperty)new BinTreeEmbedded(0, H("SwitchIntPairData"),
            values[index] == 0 ? new BinTreeProperty[] { new BinTreeHash(H("ClipName"), (uint)(10 + index)) } : new BinTreeProperty[]
            { new BinTreeHash(H("ClipName"), (uint)(10 + index)), new BinTreeI32(H("value"), values[index]) })).ToArray();
        var selector = new BinTreeStruct(0, H("SwitchIntClipData"), new BinTreeProperty[]
        {
            new BinTreeStruct(H("driver"), 0x608a3ee7, Array.Empty<BinTreeProperty>()),
            new BinTreeContainer(H("SwitchIntPairDataList"), BinPropertyType.Embedded, pairs)
        });
        var entries = new List<KeyValuePair<BinTreeProperty, BinTreeProperty>> { new(new BinTreeHash(0, 1), selector) };
        for (int index = 0; index < 3; index++)
            entries.Add(new(new BinTreeHash(0, (uint)(10 + index)), new BinTreeStruct(0, H("AtomicClipData"), new BinTreeProperty[]
            {
                new BinTreeEmbedded(H("mAnimationResourceData"), H("AnimationResourceData"), new BinTreeProperty[]
                { new BinTreeString(H("mAnimationFilePath"), $"form{index}.anm") })
            })));
        var graph = new BinTreeObject("Animations/Test", "AnimationGraphData", new BinTreeProperty[]
        { new BinTreeMap(H("mClipDataMap"), BinPropertyType.Hash, BinPropertyType.Struct, entries) });
        using var stream = new MemoryStream();
        new BinTree(new[] { graph }, Array.Empty<string>()).Write(stream);
        return Assert.Single(VfxGraphParser.ParseDocument(stream.ToArray()).AnimationGraphs).Clips;
    }

    [Theory]
    [InlineData(0f, 0f, true)]
    [InlineData(2f, .5f, true)]
    [InlineData(4f, 0f, true)]
    [InlineData(5f, .25f, true)]
    [InlineData(5f, 1f, false)]
    public void PreparedAnimationStateUsesOnlySelectedChildrenAndRespectsLoopOrHold(float time, float expected, bool loop)
    {
        var clips = SwitchGraph();
        var item = new AnimationClipCatalogItem("Dance", "Dance", "form1.anm", 4, null, clips[0], null,
            Array.Empty<AnimationClipTimedCue>(), 0, false, "")
        { MaterialAnimations = new[] { new GameMaterialAnimation(11, 0, 4) } };
        var state = GameMaterialState.Preview(null, item, loop: loop) with { Time = time };
        Assert.True(state.IsPlaying(11));
        Assert.False(state.IsPlaying(10));
        Assert.False(state.IsPlaying(12));
        Assert.Equal(expected, state.AnimationFraction(11));
        Assert.Equal(expected, state.AnimationFraction(H("Dance")));
    }

    [Fact]
    public void SequencedExpressionsReadTheSelectedStepsLocalClock()
    {
        var item = new AnimationClipCatalogItem("Sequence", "Sequence", "", 6, null, null, null,
            Array.Empty<AnimationClipTimedCue>(), 0, false, "")
        { MaterialAnimations = new[] { new GameMaterialAnimation(10, 0, 2), new GameMaterialAnimation(11, 2, 4) } };
        var state = GameMaterialState.Preview(null, item);
        Assert.Equal(.5f, (state with { Time = 1 }).AnimationFraction(10));
        Assert.False((state with { Time = 1 }).IsPlaying(11));
        Assert.False((state with { Time = 4 }).IsPlaying(10));
        Assert.Equal(.5f, (state with { Time = 4 }).AnimationFraction(11));
    }
}
