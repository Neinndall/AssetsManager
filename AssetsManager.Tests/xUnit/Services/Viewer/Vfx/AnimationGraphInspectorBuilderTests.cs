using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Composition;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Animation;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx;

public sealed class AnimationGraphInspectorBuilderTests
{
    [Fact]
    public void BuildClipsSortsResolvedNamesAndPreservesInspectorMetadata()
    {
        var track = new AnimationGraphKeyReference(0x30, "BaseTrack", true);
        var mask = new AnimationGraphKeyReference(0x40, "MissingMask", false);
        var child = new AnimationGraphKeyReference(0x02, "Idle_A", true);
        var resolved = new AnimationClipDefinition(
            0x01,
            Fnv1a.HashLower("ParametricClipData"),
            1f / 30f,
            0f,
            30f,
            new AnimationClipEventDefinition[]
            {
                new AnimationJointSnapEventDefinition(0x91, 2f, 4f, 0x10, 0x11, Vector3.Zero)
            },
            ClipName: "Run",
            AnimationFilePath: "run.anm",
            GraphPathHash: 0x100,
            ChildClipHashes: new uint[] { 0x02 },
            ChildParameters: new[] { 0.5f },
            Track: track,
            Mask: mask,
            ChildReferences: new[] { child },
            InterruptionGroups: new[] { "Movement" },
            Flags: 3u,
            ParametricValues: new float?[] { 0.5f },
            ClassName: "ParametricClipData");
        var unresolved = new AnimationClipDefinition(
            0x03,
            0x12345678,
            0f,
            0f,
            -1f,
            Array.Empty<AnimationClipEventDefinition>(),
            ClipName: "0x00000003",
            GraphPathHash: 0x100);
        var childClip = new AnimationClipDefinition(
            0x02,
            Fnv1a.HashLower("AtomicClipData"),
            1f / 30f,
            0f,
            10f,
            Array.Empty<AnimationClipEventDefinition>(),
            ClipName: "Idle_A",
            AnimationFilePath: "idle_a.anm",
            GraphPathHash: 0x100,
            ClassName: "AtomicClipData");
        var graph = new AnimationGraphDefinition(
            0x100,
            new[] { unresolved, resolved, childClip },
            Array.Empty<AnimationTrackDefinition>(),
            Array.Empty<AnimationMaskDefinition>(),
            Array.Empty<AnimationSyncGroupDefinition>());

        IReadOnlyList<AnimationGraphClipInspectorItem> items =
            AnimationGraphInspectorBuilder.BuildClips(graph, Array.Empty<AnimationClipCatalogItem>());

        Assert.Equal(new[] { "Idle_A", "Run", "0x00000003" }, items.Select(item => item.Name));
        AnimationGraphClipInspectorItem run = items.Single(item => item.Name == "Run");
        Assert.Equal("Parametric", run.Kind);
        Assert.Equal("BaseTrack", run.TrackDisplay);
        Assert.Equal("MissingMask !", run.MaskDisplay);
        Assert.Equal(1, run.EventCount);
        Assert.Equal("Joint Snap", Assert.Single(run.Events).Kind);
        Assert.Equal("2 → 4", Assert.Single(run.Events).FrameRange);
        Assert.Equal("Idle_A  0.5", Assert.Single(run.Children).DisplayText);
        Assert.Equal("0x00000003", items[^1].Name);
        Assert.Equal("0x12345678", items[^1].Kind);
    }

    [Fact]
    public void BuildClipsReportsFileAndTickRatesWhenAnimationIsPlayable()
    {
        var clip = new AnimationClipDefinition(
            0x01,
            Fnv1a.HashLower("AtomicClipData"),
            1f / 30f,
            0f,
            30f,
            Array.Empty<AnimationClipEventDefinition>(),
            ClipName: "Idle",
            AnimationFilePath: "idle.anm",
            GraphPathHash: 0x100,
            ClassName: "AtomicClipData");
        using var asset = new FakeAnimationAsset(1f, 24f);
        var catalogItem = new AnimationClipCatalogItem(
            "Idle",
            "Idle",
            "idle.anm",
            1f,
            asset,
            clip,
            null,
            Array.Empty<AnimationClipTimedCue>(),
            0,
            false,
            "0 VFX · 0 events");
        var graph = new AnimationGraphDefinition(
            0x100,
            new[] { clip },
            Array.Empty<AnimationTrackDefinition>(),
            Array.Empty<AnimationMaskDefinition>(),
            Array.Empty<AnimationSyncGroupDefinition>());

        AnimationGraphClipInspectorItem row = Assert.Single(
            AnimationGraphInspectorBuilder.BuildClips(graph, new[] { catalogItem }));

        Assert.Equal("24/30", row.RateText);
        Assert.True(row.IsPlayable);
    }

    [Fact]
    public void FilterClipsMatchesNamesCaseInsensitively()
    {
        var graph = new AnimationGraphDefinition(
            0x100,
            new[]
            {
                Clip(0x01, "Idle_Base"),
                Clip(0x02, "Run_Fast"),
                Clip(0x03, "Spell1")
            },
            Array.Empty<AnimationTrackDefinition>(),
            Array.Empty<AnimationMaskDefinition>(),
            Array.Empty<AnimationSyncGroupDefinition>());
        IReadOnlyList<AnimationGraphClipInspectorItem> rows =
            AnimationGraphInspectorBuilder.BuildClips(graph, Array.Empty<AnimationClipCatalogItem>());

        Assert.Equal(
            new[] { "Run_Fast" },
            AnimationGraphInspectorBuilder.FilterClips(rows, "rUn").Select(item => item.Name));
    }

    [Fact]
    public void BuildMaskJointsKeepsOnlyPositiveFiniteWeightsAndMapsSlotsToNames()
    {
        var mask = new AnimationMaskDefinition(
            0x40,
            "UpperBody",
            7,
            new[] { 1f, 0f, float.NaN, 0.5f });
        var graph = new AnimationGraphDefinition(
            0x100,
            Array.Empty<AnimationClipDefinition>(),
            Array.Empty<AnimationTrackDefinition>(),
            new[] { mask },
            Array.Empty<AnimationSyncGroupDefinition>());

        AnimationMaskInspectorItem maskRow = Assert.Single(AnimationGraphInspectorBuilder.BuildMasks(graph));
        Assert.Equal("2/4", maskRow.JointCountText);

        IReadOnlyList<AnimationMaskJointInspectorItem> joints =
            AnimationGraphInspectorBuilder.BuildMaskJoints(
                mask,
                new[] { "root", "spine", "invalid", "hand" });

        Assert.Equal(new[] { 0, 3 }, joints.Select(item => item.Slot));
        Assert.Equal(new[] { "root", "hand" }, joints.Select(item => item.JointName));
        Assert.Equal(new[] { "1.00", "0.50" }, joints.Select(item => item.WeightText));
    }

    private static AnimationClipDefinition Clip(uint hash, string name)
        => new(
            hash,
            Fnv1a.HashLower("AtomicClipData"),
            1f / 30f,
            0f,
            30f,
            Array.Empty<AnimationClipEventDefinition>(),
            ClipName: name,
            AnimationFilePath: $"{name}.anm",
            GraphPathHash: 0x100,
            ClassName: "AtomicClipData");

    private sealed class FakeAnimationAsset(float duration, float fps) : IAnimationAsset
    {
        public float Duration { get; } = duration;
        public float Fps { get; } = fps;
        public bool IsDisposed { get; private set; }

        public void Evaluate(
            float time,
            IDictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)> pose)
        {
        }

        public void Dispose() => IsDisposed = true;
    }
}
