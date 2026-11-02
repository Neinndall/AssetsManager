using System;
using System.Collections.Generic;
using System.Linq;
using AssetsManager.Services.Viewer.Animation;
using AssetsManager.Services.Viewer.Vfx.Parsing;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx;

public sealed class AnimationGraphNameTests
{
    [Theory]
    [InlineData("assets/chogath_crit_melee.anm", "crit_melee")]
    [InlineData("assets\\chogath_crit_melee.anm", "crit_melee")]
    [InlineData("assets/chogath_crit_melee.skins_chogath_skin0.anm", "crit_melee")]
    [InlineData("assets/Idle_Base.anm", "Idle_Base")]
    public void AnmStemOrUnderscoreTailNamesOnlyTheMatchingKey(string path, string expected)
    {
        var tree = Tree(Graph("Graph", (H(expected), Atomic(new BinTreeString(0, path)))));
        var graph = Assert.Single(VfxGraphParser.ParseDocument(tree).AnimationGraphs);
        Assert.Equal(expected, Assert.Single(graph.Clips).ClipName);
        Assert.Equal(path, graph.Clips[0].AnimationFilePath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HexFallbackDoesNotBlockLocalNamesOrOpeningIdle(bool prefix)
    {
        var tree = Tree(Graph("Graph", (H("Idle1"), Atomic(new BinTreeString(0, "assets/character_idle1.anm")))));
        string Resolve(uint key) => prefix ? $"0x{key:X8}" : $"{key:X8}";
        var graph = Assert.Single(VfxGraphParser.ParseDocument(tree, Resolve).AnimationGraphs);
        Assert.Equal("idle1", Assert.Single(graph.Clips).ClipName);
        Assert.Same(graph.Clips[0], AnimationGraphPlayback.OpeningClip(graph.Clips));
    }

    [Fact]
    public void EventNamesMustHashToTheirKeysAndKeepTheirTypedPayloadAndTimes()
    {
        var show = new BinTreeStruct(0, 0xbcf56e70, new BinTreeProperty[]
        {
            new BinTreeString(H("mName"), "ShowBody"),
            new BinTreeF32(H("mStartFrame"), 3), new BinTreeF32(H("mEndFrame"), 7),
            new BinTreeContainer(H("mShowSubmeshList"), BinPropertyType.Hash, new BinTreeProperty[] { new BinTreeHash(0, H("Body")) })
        });
        var wrong = new BinTreeStruct(0, H("UnknownEvent"), new BinTreeProperty[] { new BinTreeString(H("mName"), "NotTheKey") });
        var events = Map("mEventDataMap", (H("ShowBody"), show), (H("Other"), wrong));
        var tree = Tree(Graph("Graph", (H("Whirl"), Atomic(new BinTreeString(0, "assets/spin.anm"), events))));
        var clip = Assert.Single(Assert.Single(VfxGraphParser.ParseDocument(tree).AnimationGraphs).Clips);
        Assert.Equal($"0x{H("Whirl"):x8}", clip.ClipName);
        var parsed = Assert.IsType<AnimationSubmeshVisibilityEventDefinition>(clip.Events[0]);
        Assert.Equal("ShowBody", parsed.Name);
        Assert.Equal(H("ShowBody"), parsed.EventHash);
        Assert.Equal(3f, parsed.StartFrame);
        Assert.Equal(7f, parsed.EndFrame);
        Assert.Equal(new[] { H("Body") }, parsed.ShowSubmeshHashes);
        Assert.Equal($"0x{H("Other"):x8}", clip.Events[1].Name);
    }

    [Fact]
    public void NamedTableValuesWinAndGraphReferencesShareTheVerifiedName()
    {
        uint leaf = H("Idle1"), selector = H("Selector");
        var composite = new BinTreeStruct(0, H("EventControlledSelectorClipData"), new BinTreeProperty[]
        { new BinTreeHash(H("DefaultClipName"), leaf) });
        var tree = Tree(Graph("Graph", (selector, composite), (leaf, Atomic(new BinTreeString(0, "assets/character_idle1.anm")))));
        string Resolve(uint key) => key == leaf ? "IDLE1" : null;
        var graph = Assert.Single(VfxGraphParser.ParseDocument(tree, Resolve).AnimationGraphs);
        Assert.Equal("IDLE1", graph.Clips[1].ClipName);
        Assert.Equal("IDLE1", Assert.Single(graph.Clips[0].ChildReferences).Name);
        Assert.True(graph.Clips[0].ChildReferences[0].Declared);
        var local = Assert.Single(VfxGraphParser.ParseDocument(tree).AnimationGraphs);
        Assert.Equal("idle1", Assert.Single(local.Clips[0].ChildReferences).Name);
    }

    [Fact]
    public void EventTableNamesWinOverMatchingMName()
    {
        var events = Map("mEventDataMap", (H("Swing"), new BinTreeStruct(0, H("UnknownEvent"),
            new BinTreeProperty[] { new BinTreeString(H("mName"), "Swing") })));
        var tree = Tree(Graph("Graph", (H("Attack"), Atomic(new BinTreeString(0, "attack.anm"), events))));
        var graph = Assert.Single(VfxGraphParser.ParseDocument(tree, key => key == H("Swing") ? "SWING" : null).AnimationGraphs);
        Assert.Equal("SWING", Assert.Single(Assert.Single(graph.Clips).Events).Name);
    }

    [Fact]
    public void InferredNamesStayWithinTheirGraphAndDoNotMutateTheBin()
    {
        uint key = H("Idle1");
        var tree = Tree(Graph("A", (key, Atomic(new BinTreeString(0, "idle1.anm")))),
            Graph("B", (key, Atomic(new BinTreeString(0, "other.anm")))));
        var graphs = VfxGraphParser.ParseDocument(tree).AnimationGraphs;
        Assert.Equal("idle1", Assert.Single(graphs.Single(graph => graph.PathHash == H("A")).Clips).ClipName);
        Assert.Equal($"0x{key:x8}", Assert.Single(graphs.Single(graph => graph.PathHash == H("B")).Clips).ClipName);
        Assert.Null(AnimationGraphPlayback.OpeningClip(graphs.Single(graph => graph.PathHash == H("B")).Clips));
        Assert.Equal("other.anm", Assert.Single(VfxGraphParser.ParseDocument(tree).AnimationGraphs.Single(graph => graph.PathHash == H("B")).Clips).AnimationFilePath);
    }

    [Fact]
    public void WadPathNamesTheClipThroughBothReadersWithoutReplacingItsAssetIdentity()
    {
        const ulong assetHash = 0x1234567890abcdef;
        var tree = Tree(Graph("Graph", (H("Taunt"), Atomic(new BinTreeWadChunkLink(0, assetHash)))));
        string Path(ulong key) => key == assetHash ? "assets/chogath_taunt.anm" : null;
        var parsed = VfxGraphParser.ParseDocument(tree, wadChunkPathResolver: Path);
        var clip = Assert.Single(Assert.Single(parsed.AnimationGraphs).Clips);
        Assert.Equal("taunt", clip.ClipName);
        Assert.Equal($"{assetHash:x16}.anm", clip.AnimationFilePath);
        Assert.Equal("taunt", Assert.Single(parsed.EventSequences).ClipName);
        var map = AnimationGraphReader.Read(new[] { tree }, H("Graph"), null, null, Path);
        var mapClip = Assert.Single(map.Clips);
        Assert.Equal(clip.ClipName, mapClip.ClipName);
        Assert.Equal(clip.OwnerPathHash, mapClip.OwnerPathHash);
        Assert.Equal(clip.AnimationFilePath, mapClip.AnimationFilePath);
    }

    private static uint H(string name) => Fnv1a.HashLower(name);
    private static BinTree Tree(params BinTreeObject[] objects) => new(objects, Array.Empty<string>());
    private static BinTreeObject Graph(string name, params (uint Key, BinTreeStruct Value)[] clips) =>
        new(name, "AnimationGraphData", new BinTreeProperty[] { Map("mClipDataMap", clips) });
    private static BinTreeMap Map(string field, params (uint Key, BinTreeStruct Value)[] values) =>
        new(H(field), BinPropertyType.Hash, BinPropertyType.Struct, values.Select(pair =>
            new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, pair.Key), pair.Value)).ToArray());
    private static BinTreeStruct Atomic(BinTreeProperty file, params BinTreeProperty[] fields)
    {
        BinTreeProperty namedFile = file switch
        {
            BinTreeString text => new BinTreeString(H("mAnimationFilePath"), text.Value),
            BinTreeWadChunkLink hash => new BinTreeWadChunkLink(H("mAnimationFilePath"), hash.Value),
            _ => throw new ArgumentException(nameof(file))
        };
        return new BinTreeStruct(0, H("AtomicClipData"), fields.Prepend(
            new BinTreeStruct(H("mAnimationResourceData"), H("AnimationResourceData"), new[] { namedFile })).ToArray());
    }
}
