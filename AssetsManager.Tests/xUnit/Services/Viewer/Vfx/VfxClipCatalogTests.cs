using System;
using System.Linq;
using AssetsManager.Services.Viewer.Vfx.Composition;
using AssetsManager.Views.Models.Viewer;
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
