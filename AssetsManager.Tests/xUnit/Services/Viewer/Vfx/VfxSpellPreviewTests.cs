using System;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Composition;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx;

public sealed class VfxSpellPreviewTests
{
    [Fact]
    public void SpellAnimationResolvesDirectSkinGraphClip()
    {
        AnimationClipCatalogItem direct = Item(Clip(
            Fnv1a.HashLower("Spell1"),
            0x100,
            "Spell1",
            "spell1.anm"));

        Assert.Same(direct, VfxSpellPreviewComposer.ResolveAnimation("spell1", new[] { direct }));
    }

    [Fact]
    public void SpellAnimationFollowsOnlyAnUnambiguousWrapper()
    {
        AnimationClipDefinition leafDefinition = Clip(0x02, 0x100, "leaf", "leaf.anm");
        AnimationClipDefinition wrapperDefinition = Clip(0x01, 0x100, "Spell1", null) with
        {
            ChildClipHashes = new uint[] { 0x02 }
        };
        AnimationClipCatalogItem wrapper = Item(wrapperDefinition);
        AnimationClipCatalogItem leaf = Item(leafDefinition);

        Assert.Same(leaf, VfxSpellPreviewComposer.ResolveAnimation("Spell1", new[] { wrapper, leaf }));
    }

    [Fact]
    public void SpellAnimationRejectsAWrapperWithMultipleChildrenLikeTheReferencePreview()
    {
        AnimationClipDefinition wrapperDefinition = Clip(0x01, 0x100, "Spell1", null) with
        {
            ChildClipHashes = new uint[] { 0x02, 0x03 }
        };
        AnimationClipCatalogItem wrapper = Item(wrapperDefinition);
        AnimationClipCatalogItem first = Item(Clip(0x02, 0x100, "first", "first.anm"));
        AnimationClipCatalogItem second = Item(Clip(0x03, 0x100, "second", "second.anm"));

        Assert.Null(VfxSpellPreviewComposer.ResolveAnimation("Spell1", new[] { wrapper, first, second }));
    }

    [Fact]
    public void SpellAnimationRejectsWrapperCycles()
    {
        AnimationClipCatalogItem a = Item(Clip(0x01, 0x100, "Spell1", null) with
        {
            ChildClipHashes = new uint[] { 0x02 }
        });
        AnimationClipCatalogItem b = Item(Clip(0x02, 0x100, "wrapper", null) with
        {
            ChildClipHashes = new uint[] { 0x01 }
        });

        Assert.Null(VfxSpellPreviewComposer.ResolveAnimation("Spell1", new[] { a, b }));
    }

    [Fact]
    public void CastTimingRejectsConflictingWrittenTimes()
    {
        VfxSpellPreview preview = Preview(spellCastTime: 0.25f, castTime: 0.5f);

        Assert.False(VfxSpellPreviewComposer.TryCastRelease(preview, out _));
    }

    [Fact]
    public void CastTimingUsesTheWrittenSpellCastTimeWhenCompatible()
    {
        VfxSpellPreview preview = Preview(spellCastTime: 0.25f, castTime: 0.250001f);

        Assert.True(VfxSpellPreviewComposer.TryCastRelease(preview, out double release));
        Assert.Equal(0.25d, release, 5);
    }

    [Fact]
    public void FixedSpeedFlightUsesTheWrittenDelayAndHorizontalDistance()
    {
        var missile = new VfxSpellMissilePreview(
            VfxSpellMissileMovementKind.FixedSpeed,
            200f,
            null,
            0.1f,
            "hand",
            null,
            null,
            null);

        var flight = VfxSpellPreviewComposer.CompileFlight(
            missile,
            new Vector3(0f, 100f, 0f),
            new Vector3(1000f, 100f, 0f));

        Assert.True(flight.HasValue);
        Assert.Equal(0.1d, flight.Value.Delay, 5);
        Assert.Equal(5d, flight.Value.Duration, 5);
    }

    [Fact]
    public void FlightRejectsDifferentAnchorHeights()
    {
        var missile = new VfxSpellMissilePreview(
            VfxSpellMissileMovementKind.FixedTime,
            null,
            1f,
            null,
            null,
            null,
            null,
            null);

        Assert.Null(VfxSpellPreviewComposer.CompileFlight(
            missile,
            new Vector3(0f, 100f, 0f),
            new Vector3(1000f, 101f, 0f)));
    }

    private static VfxSpellPreview Preview(float? spellCastTime = null, float? castTime = null)
        => new(
            spellCastTime,
            castTime,
            null,
            0u,
            null,
            null,
            null,
            null,
            0u,
            null,
            Array.Empty<VfxSpellIssue>());

    private static AnimationClipCatalogItem Item(AnimationClipDefinition clip)
        => new(
            clip.ClipName,
            clip.ClipName,
            clip.AnimationFilePath,
            1f,
            null,
            clip,
            null,
            Array.Empty<AnimationClipTimedCue>(),
            0,
            false,
            string.Empty);

    private static AnimationClipDefinition Clip(
        uint ownerPathHash,
        uint graphPathHash,
        string clipName,
        string animationFilePath)
        => new(
            ownerPathHash,
            0u,
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
