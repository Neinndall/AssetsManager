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
    public void AcceleratingFlightRampsToItsMaxSpeedThenHolds()
    {
        // 1000 -> 2000 at 1000/s: 1 s covers 1500 units, the remaining 500 take 0.25 s at 2000.
        var missile = new VfxSpellMissilePreview(VfxSpellMissileMovementKind.Accelerating, null, null, null, null, null, null, null)
        {
            Acceleration = 1000f,
            InitialSpeed = 1000f,
            MaxSpeed = 2000f
        };

        var path = VfxSpellFlightPath.Compile(missile, Vector3.Zero, new Vector3(0f, 0f, 2000f));

        Assert.NotNull(path);
        Assert.Equal(1.25d, path.Duration, 4);
        Assert.Equal(1500f, path.Sample(1d).Position.Z, 1);
    }

    [Fact]
    public void DeceleratingFlightThatStopsShortIsNotPlayable()
    {
        var missile = new VfxSpellMissilePreview(VfxSpellMissileMovementKind.Accelerating, null, null, null, null, null, null, null)
        {
            Acceleration = -1000f,
            InitialSpeed = 500f
        };

        Assert.Null(VfxSpellFlightPath.Compile(missile, Vector3.Zero, new Vector3(0f, 0f, 1000f)));
    }

    [Fact]
    public void SplineFlightBendsTowardTheCastersRightAndEndsOnTarget()
    {
        var missile = new VfxSpellMissilePreview(VfxSpellMissileMovementKind.FixedTimeSpline, null, 1f, null, null, null, null, null)
        {
            SplineStartOffset = new Vector3(100f, 0f, 0f),
            SplineControlPoint1 = new Vector3(1f, 0f, 0f),
            SplineControlPoint2 = new Vector3(-1f, 0f, 0f)
        };
        Vector3 target = new(0f, 0f, 1000f);

        var path = VfxSpellFlightPath.Compile(missile, Vector3.Zero, target);

        Assert.NotNull(path);
        Assert.Equal(new Vector3(100f, 0f, 0f), path.Start);
        Assert.True(path.Sample(0.5d).Position.X > 100f);
        Assert.True(Vector3.Distance(path.Sample(1d).Position, target) < 0.01f);
    }

    [Fact]
    public void FixedSpeedSplineTakesItsCurveLengthOverSpeed()
    {
        var missile = new VfxSpellMissilePreview(VfxSpellMissileMovementKind.FixedSpeedSpline, 1000f, null, null, null, null, null, null);

        var path = VfxSpellFlightPath.Compile(missile, Vector3.Zero, new Vector3(0f, 0f, 1000f));

        Assert.NotNull(path);
        Assert.Equal(1d, path.Duration, 3);
        Assert.Equal(500f, path.Sample(0.5d).Position.Z, 0);
    }

    [Fact]
    public void GravityFlightArcsAndLandsAtItsTrackHeight()
    {
        // 1 s flight under gravity 8000 peaks at g*T^2/8 = 1000 halfway.
        var missile = new VfxSpellMissilePreview(VfxSpellMissileMovementKind.FixedTime, null, 1f, null, null, null, null, null)
        {
            Gravity = 8000f
        };

        var path = VfxSpellFlightPath.Compile(missile, new Vector3(0f, 100f, 0f), new Vector3(0f, 100f, 1000f));

        Assert.Equal(100f, path.Sample(0d).Position.Y, 3);
        Assert.Equal(1100f, path.Sample(0.5d).Position.Y, 3);
        Assert.Equal(100f, path.Sample(1d).Position.Y, 3);
    }

    [Fact]
    public void HeightSolverFlightLandsAtTheTargetHeightAugment()
    {
        var missile = new VfxSpellMissilePreview(VfxSpellMissileMovementKind.FixedTime, null, 0.5f, null, "head", null, 30f, null)
        {
            LandsOnTargetHeight = true,
            Gravity = 7000f
        };

        var path = VfxSpellFlightPath.Compile(missile, new Vector3(0f, 250f, 0f), new Vector3(0f, 250f, 1000f));

        Assert.Equal(new Vector3(0f, 30f, 1000f), path.End);
        Assert.Equal(30f, path.Sample(0.5d).Position.Y, 3);
        Assert.True(path.Sample(0.25d).Position.Y > 250f);
    }

    [Fact]
    public void SinusoidalFlightWavesByItsAmplitude()
    {
        var missile = new VfxSpellMissilePreview(VfxSpellMissileMovementKind.FixedTime, null, 1f, null, null, null, null, null)
        {
            SineAmplitude = -60f,
            SinePeriods = 0.25f
        };

        var path = VfxSpellFlightPath.Compile(missile, Vector3.Zero, new Vector3(0f, 0f, 1000f));

        Assert.Equal(-60f, path.Sample(1d).Position.Y, 3);
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

    [Fact]
    public void SpellTargetFollowsChampionFacingOnTheGroundPlane()
    {
        Vector3 source = new(25f, 80f, -40f);

        Vector3 target = VfxSpellPreviewComposer.ResolveTarget(
            source,
            500f,
            new Vector3(0f, 5f, -2f));

        Assert.Equal(new Vector3(0f, 80f, -500f), target);
    }

    [Fact]
    public void SpellTargetFallsBackToPositiveZForADegenerateFacing()
    {
        Vector3 source = new(25f, 80f, -40f);

        Vector3 target = VfxSpellPreviewComposer.ResolveTarget(
            source,
            500f,
            Vector3.Zero);

        Assert.Equal(new Vector3(0f, 80f, 500f), target);
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
