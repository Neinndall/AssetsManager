using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Composition;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Animation;
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
    public void MetadataCatalogDoesNotDecodeAnimationAssets()
    {
        var bundle = new VfxLoadingService.Bundle();
        bundle.Clips.Add(Clip(0x10, 0x100, "idle", "missing-but-resolved.anm"));
        bundle.OwnerSceneContext = new VfxOwnerSceneContext(
            string.Empty,
            string.Empty,
            1f,
            0x100,
            Array.Empty<uint>());
        using var catalog = new VfxClipCatalog();

        AnimationClipCatalogItem item = Assert.Single(catalog.BuildMetadata(
            bundle,
            path => $"resolved/{path}"));

        Assert.Null(item.AnimationAsset);
        Assert.Equal(0f, item.Duration);
        Assert.Equal("resolved/missing-but-resolved.anm", item.FilePath);
    }

    [Fact]
    public void MetadataCatalogKeepsAuthoredClipWhenAnimationAssetIsUnavailable()
    {
        var bundle = new VfxLoadingService.Bundle();
        bundle.Clips.Add(Clip(0x10, 0x100, "idle", "missing.anm"));
        bundle.OwnerSceneContext = new VfxOwnerSceneContext(
            string.Empty,
            string.Empty,
            1f,
            0x100,
            Array.Empty<uint>());
        using var catalog = new VfxClipCatalog();

        AnimationClipCatalogItem item = Assert.Single(catalog.BuildMetadata(bundle, _ => null));

        Assert.Null(item.AnimationAsset);
        Assert.Equal("missing.anm", item.FilePath);
    }

    [Fact]
    public void OpeningClipUsesFirstIdleInAuthoredOrderIgnoringCase()
    {
        var bundle = new VfxLoadingService.Bundle();
        bundle.Clips.Add(Clip(0x10, 0x100, "Run", "run.anm"));
        bundle.Clips.Add(Clip(0x20, 0x100, "IDLE_Base", "idle_base.anm"));
        bundle.Clips.Add(Clip(0x30, 0x100, "idle2", "idle2.anm"));
        bundle.OwnerSceneContext = new VfxOwnerSceneContext(
            string.Empty,
            string.Empty,
            1f,
            0x100,
            Array.Empty<uint>());
        using var catalog = new VfxClipCatalog();

        IReadOnlyList<AnimationClipCatalogItem> clips = catalog.BuildMetadata(bundle, path => path);
        AnimationClipCatalogItem opening = VfxClipCatalog.OpeningClip(clips);

        Assert.NotNull(opening);
        Assert.Equal(0x20u, opening.Clip.OwnerPathHash);
        Assert.Equal("IDLE_Base", opening.Name);
    }

    [Fact]
    public void OpeningClipUsesBindPoseWhenThereIsNoIdle()
    {
        var bundle = new VfxLoadingService.Bundle();
        bundle.Clips.Add(Clip(0x10, 0x100, "Presentation", "presentation.anm"));
        bundle.Clips.Add(Clip(0x20, 0x100, "Run", "run.anm"));
        bundle.OwnerSceneContext = new VfxOwnerSceneContext(
            string.Empty,
            string.Empty,
            1f,
            0x100,
            Array.Empty<uint>());
        using var catalog = new VfxClipCatalog();

        IReadOnlyList<AnimationClipCatalogItem> clips = catalog.BuildMetadata(bundle, path => path);

        Assert.Null(VfxClipCatalog.OpeningClip(clips));
    }

    [Fact]
    public void FormMetadataUsesGearBranchWithoutDecodingOrChangingTheBaseCatalog()
    {
        AnimationClipDefinition first = Clip(0x01, 0x100, "base", "base.anm");
        AnimationClipDefinition second = Clip(0x02, 0x100, "alternate", "alternate.anm");
        AnimationClipDefinition gear = Clip(0x10, 0x100, "idle", null) with
        {
            OwnerClassHash = Fnv1a.HashLower("ParametricClipData"),
            ChildClipHashes = new uint[] { 0x01, 0x02 },
            ParametricValues = new float?[] { 0f, 1f },
            UsesEquippedGearParameter = true
        };
        var bundle = new VfxLoadingService.Bundle();
        bundle.Clips.AddRange(new[] { gear, first, second });
        var form = new VfxCharacterFormDefinition(123, 1, "Alternate",
            Array.Empty<uint>(), Array.Empty<uint>());
        using var catalog = new VfxClipCatalog();

        AnimationClipCatalogItem alternate = catalog.BuildMetadata(
            bundle.CreateCharacterPlaybackView(form), path => path, parameter: 0f)
            .Single(item => item.Clip.OwnerPathHash == gear.OwnerPathHash);
        AnimationClipCatalogItem original = catalog.BuildMetadata(bundle, path => path)
            .Single(item => item.Clip.OwnerPathHash == gear.OwnerPathHash);

        Assert.Equal("alternate.anm", alternate.FilePath);
        Assert.Equal(1f, alternate.ParameterValue);
        Assert.Null(alternate.AnimationAsset);
        Assert.Equal("base.anm", original.FilePath);
        Assert.Null(bundle.CharacterGearIndex);
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

    [Fact]
    public void GraphTickRetimesAnimationDurationAndSamplingTogether()
    {
        var source = new RecordingAnimationAsset(duration: 1f, fps: 30f);
        IAnimationAsset retimed = VfxClipCatalog.RetimeForGraph(source, 1f / 60f);
        var pose = new Dictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)>();

        Assert.Equal(0.5f, retimed.Duration, 5);
        Assert.Equal(60f, retimed.Fps, 5);

        retimed.Evaluate(0.25f, pose);

        Assert.Equal(0.5f, source.LastEvaluationTime, 5);
    }

    [Fact]
    public void MissingGraphTickKeepsTheAnimationNativeClock()
    {
        var source = new RecordingAnimationAsset(duration: 1f, fps: 30f);
        IAnimationAsset retimed = VfxClipCatalog.RetimeForGraph(source, 0f);
        var pose = new Dictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)>();

        Assert.Equal(1f, retimed.Duration, 5);
        Assert.Equal(30f, retimed.Fps, 5);

        retimed.Evaluate(0.5f, pose);

        Assert.Equal(0.5f, source.LastEvaluationTime, 5);
    }

    [Fact]
    public void BindPoseItemIsCorrectlyIdentifiedAndConfigured()
    {
        AnimationClipCatalogItem bindPose = AnimationClipCatalogItem.CreateBindPoseItem();

        Assert.True(bindPose.IsBindPose);
        Assert.Equal(AnimationClipCatalogItem.BindPoseName, bindPose.Name);
        Assert.Equal(0f, bindPose.Duration);
        Assert.Null(bindPose.AnimationAsset);
        Assert.Null(bindPose.Clip);
    }

    [Fact]
    public void OpeningClipPrefersIdleOverBindPose()
    {
        AnimationClipCatalogItem bindPose = AnimationClipCatalogItem.CreateBindPoseItem();
        var idleClip = new AnimationClipCatalogItem(
            "Idle1", "Idle1", "idle.anm", 2f, null, null, null,
            Array.Empty<AnimationClipTimedCue>(), 0, false, "Idle");
        var runClip = new AnimationClipCatalogItem(
            "Run", "Run", "run.anm", 1f, null, null, null,
            Array.Empty<AnimationClipTimedCue>(), 0, false, "Run");

        AnimationClipCatalogItem opening = VfxClipCatalog.OpeningClip(new[] { bindPose, idleClip, runClip });

        Assert.NotNull(opening);
        Assert.Equal("Idle1", opening.Name);
        Assert.False(opening.IsBindPose);
    }

    [Fact]
    public void OpeningClipReturnsNullWhenOnlyBindPoseOrNonIdleClipsPresent()
    {
        AnimationClipCatalogItem bindPose = AnimationClipCatalogItem.CreateBindPoseItem();
        var runClip = new AnimationClipCatalogItem(
            "Run", "Run", "run.anm", 1f, null, null, null,
            Array.Empty<AnimationClipTimedCue>(), 0, false, "Run");

        AnimationClipCatalogItem opening = VfxClipCatalog.OpeningClip(new[] { bindPose, runClip });

        Assert.Null(opening);
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

    private sealed class RecordingAnimationAsset(float duration, float fps) : IAnimationAsset
    {
        public float Duration { get; } = duration;
        public float Fps { get; } = fps;
        public bool IsDisposed { get; private set; }
        public float LastEvaluationTime { get; private set; } = float.NaN;

        public void Evaluate(
            float time,
            IDictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)> pose)
            => LastEvaluationTime = time;

        public void Dispose() => IsDisposed = true;
    }
}
