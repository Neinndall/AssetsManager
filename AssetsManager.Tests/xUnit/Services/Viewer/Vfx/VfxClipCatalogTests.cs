using System;
using System.Linq;
using AssetsManager.Services.Viewer.Vfx.Composition;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx;

public class VfxClipCatalogTests
{
    [Fact]
    public void SelectGraphClipsUsesOnlyTheSkinAnimationGraph()
    {
        AnimationClipDefinition wantedA = Clip(0x10, 0x100, "idle");
        AnimationClipDefinition otherGraph = Clip(0x20, 0x200, "dance_in");
        AnimationClipDefinition wantedB = Clip(0x30, 0x100, "run");

        var selected = VfxClipCatalog.SelectGraphClips(
            new[] { wantedA, otherGraph, wantedB },
            0x100);

        Assert.Equal(new uint[] { 0x10, 0x30 }, selected.Select(clip => clip.OwnerPathHash));
    }

    [Fact]
    public void UnresolvedClipKeepsItsGraphHashInsteadOfAnimationFilename()
    {
        AnimationClipDefinition unresolved = Clip(
            ownerPathHash: 0x0fa88aba,
            graphPathHash: 0x100,
            clipName: null,
            animationFilePath: "assets/characters/janna/animations/dance_in.anm");

        Assert.Equal("0x0fa88aba", VfxClipCatalog.DisplayNameFor(unresolved));
    }

    [Fact]
    public void ResolvedGraphKeyRemainsTheClipDisplayName()
    {
        AnimationClipDefinition resolved = Clip(
            ownerPathHash: 0x12345678,
            graphPathHash: 0x100,
            clipName: "dance_in",
            animationFilePath: "assets/characters/janna/animations/dance_in.anm");

        Assert.Equal("dance_in", VfxClipCatalog.DisplayNameFor(resolved));
    }

    [Fact]
    public void ParametricPlaylistUsesTheNearestAuthoredValue()
    {
        AnimationClipDefinition first = Clip(0x01, 0x100, "first", "first.anm");
        AnimationClipDefinition second = Clip(0x02, 0x100, "second", "second.anm");
        AnimationClipDefinition parametric = Clip(0x10, 0x100, "move", animationFilePath: null) with
        {
            OwnerClassHash = Fnv1a.HashLower("ParametricClipData"),
            ChildClipHashes = new uint[] { 0x01, 0x02 },
            ChildParameters = new[] { 0f, 2f },
            ParametricValues = new float?[] { 0f, 2f }
        };
        AnimationClipDefinition[] clips = { parametric, first, second };

        Assert.Equal(
            new[] { "second" },
            VfxClipCatalog.ResolvePlaylist(parametric, clips, 1.6f)
                .Select(clip => clip.ClipName));
        Assert.Equal(
            new[] { "first" },
            VfxClipCatalog.ResolvePlaylist(parametric, clips, 1f)
                .Select(clip => clip.ClipName));
    }

    [Fact]
    public void ParametricValuesIgnoreMissingAndDuplicatePairs()
    {
        AnimationClipDefinition parametric = Clip(0x10, 0x100, "move", animationFilePath: null) with
        {
            OwnerClassHash = Fnv1a.HashLower("ParametricClipData"),
            ParametricValues = new float?[] { 2f, null, 0f, 2f }
        };

        Assert.Equal(new[] { 0f, 2f }, VfxClipCatalog.ParameterValues(parametric));
    }

    [Fact]
    public void SequencerPlaysEveryReachableChildInOrder()
    {
        AnimationClipDefinition first = Clip(0x01, 0x100, "first", "first.anm");
        AnimationClipDefinition second = Clip(0x02, 0x100, "second", "second.anm");
        AnimationClipDefinition sequencer = Clip(0x10, 0x100, "sequence", animationFilePath: null) with
        {
            OwnerClassHash = Fnv1a.HashLower("SequencerClipData"),
            ChildClipHashes = new uint[] { 0x01, 0x02 }
        };

        Assert.Equal(
            new[] { "first", "second" },
            VfxClipCatalog.ResolvePlaylist(sequencer, new[] { sequencer, first, second })
                .Select(clip => clip.ClipName));
    }

    [Fact]
    public void CompositeSkipsCyclesAndUsesTheFirstPlayableChild()
    {
        AnimationClipDefinition leaf = Clip(0x02, 0x100, "leaf", "leaf.anm");
        AnimationClipDefinition composite = Clip(0x10, 0x100, "composite", animationFilePath: null) with
        {
            OwnerClassHash = Fnv1a.HashLower("SelectorClipData"),
            ChildClipHashes = new uint[] { 0x10, 0x02 }
        };

        AnimationClipDefinition resolved = Assert.Single(
            VfxClipCatalog.ResolvePlaylist(composite, new[] { composite, leaf }));
        Assert.Equal("leaf", resolved.ClipName);
    }

    private static AnimationClipDefinition Clip(
        uint ownerPathHash,
        uint graphPathHash,
        string clipName,
        string animationFilePath = "test.anm")
        => new(
            ownerPathHash,
            0,
            1f / 30f,
            0f,
            -1f,
            Array.Empty<AnimationClipEventDefinition>(),
            clipName,
            animationFilePath,
            graphPathHash,
            Array.Empty<uint>(),
            Array.Empty<float>());
}
