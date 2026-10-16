using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Services.Viewer.Vfx.Composition;
using AssetsManager.Services.Viewer.Vfx.Semantics;
using AssetsManager.Services.Viewer.Vfx.Session;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx;

public sealed class VfxSystemRigResolverTests
{
    public static IEnumerable<object[]> SpellSpeeds()
    {
        foreach (bool catalog in new[] { false, true })
        foreach (var kind in new[] { VfxSpellMissileMovementKind.FixedSpeed, VfxSpellMissileMovementKind.FixedSpeedSpline })
        foreach (var speeds in new (float? Movement, float? Spell, float Expected)[]
        {
            (null, 2000f, 2000f), (2400f, 2000f, 2400f), (0f, 2000f, 2000f),
            (-1f, 2000f, 2000f), (null, null, 1800f), (null, 0f, 1800f),
            (null, -1f, 1800f), (null, float.NaN, 1800f), (float.PositiveInfinity, 2000f, 2000f)
        })
            yield return new object[] { catalog, kind, speeds.Movement, speeds.Spell, speeds.Expected };
    }

    [Theory, MemberData(nameof(SpellSpeeds))]
    public void AutomaticSpellRigUsesCastRangeAndEffectiveSpeedWithoutChangingAuthoredValues(
        bool catalog, VfxSpellMissileMovementKind kind, float? movementSpeed, float? spellSpeed, float expected)
    {
        var system = System("Projectile");
        var bundle = Bundle(system);
        // A straight Hermite spline has a known length, so speed can be checked independently of its sampler.
        var missile = new VfxSpellMissilePreview(kind, movementSpeed, null, null, null, null, null, null)
        {
            SplineControlPoint1 = Vector3.UnitZ, SplineControlPoint2 = Vector3.UnitZ,
            SplineFromMissile = true, HeightOffsetAtEnd = true
        };
        var spell = Spell() with
        {
            MissileEffectKey = 77, CastRange = 600f, MissileSpeed = spellSpeed,
            Missile = missile, ScriptName = "HeroQ"
        };
        if (catalog) bundle.MissileSpells.Add(system.PathHash, new[] { spell });
        else bundle.SpellPreviews.Add(1, spell);

        var rig = VfxSystemRigResolver.ResolveSettings(system, bundle).Settings;

        Assert.Equal(600f, rig.FlightRange);
        Assert.Equal(expected, rig.FlightSpeed);
        Assert.Equal(expected, rig.Missile.Speed);
        Assert.Equal(kind, rig.Missile.MovementKind);
        Assert.Equal("HeroQ", rig.SpellName);
        Assert.Equal(movementSpeed, spell.Missile.Speed);
        Assert.Equal(spellSpeed, spell.MissileSpeed);
        var track = VfxRigMotion.CompileRigTrack(rig);
        Assert.NotNull(track);
        Assert.Equal(-300f, track.Start.X, 3);
        Assert.Equal(300f, track.End.X, 3);
        Assert.InRange(Math.Abs(track.Duration - 600d / expected), 0, VfxSpellFlightPath.FlightStep + 1e-5);
    }

    [Theory]
    [InlineData(null, 1200f)]
    [InlineData(0f, 1200f)]
    [InlineData(-1f, 1200f)]
    [InlineData(100001f, 1200f)]
    [InlineData(float.NaN, 1200f)]
    [InlineData(float.PositiveInfinity, 1200f)]
    [InlineData(100000f, 100000f)]
    public void AutomaticSpellRigOnlyUsesFinitePositivePreviewRanges(float? range, float expected)
    {
        var system = System("Projectile");
        var bundle = Bundle(system);
        bundle.MissileSpells.Add(system.PathHash, new[] { Spell() with
        {
            CastRange = range,
            Missile = new(VfxSpellMissileMovementKind.FixedSpeed, 2000f, null, null, null, null, null, null)
        } });
        Assert.Equal(expected, VfxSystemRigResolver.ResolveSettings(system, bundle).Settings.FlightRange);
    }

    [Theory]
    [InlineData(VfxSpellMissileMovementKind.FixedTime)]
    [InlineData(VfxSpellMissileMovementKind.Accelerating)]
    [InlineData(VfxSpellMissileMovementKind.Circle)]
    public void SpellFallbackSpeedDoesNotReplaceOtherAuthoredMovement(VfxSpellMissileMovementKind kind)
    {
        var system = System("Projectile");
        var bundle = Bundle(system);
        var missile = new VfxSpellMissilePreview(kind, null, 2f, null, null, null, null, null)
        { InitialSpeed = 100f, Acceleration = 300f, AngularVelocity = 1f, Lifetime = 2f };
        bundle.MissileSpells.Add(system.PathHash, new[] { Spell() with
        { CastRange = 600f, MissileSpeed = 2000f, Missile = missile } });

        var rig = VfxSystemRigResolver.ResolveSettings(system, bundle).Settings;
        Assert.Equal(600f, rig.FlightRange);
        Assert.Same(missile, rig.Missile);
        if (kind == VfxSpellMissileMovementKind.FixedTime)
            Assert.InRange(Math.Abs(VfxRigMotion.FlightDuration(rig) - 2d), 0, VfxSpellFlightPath.FlightStep + 1e-5);
        if (kind == VfxSpellMissileMovementKind.Circle)
            Assert.Equal(600f, VfxRigMotion.OriginAt(rig, 0).X);
    }

    [Fact]
    public void AuthoredProjectileRoleWinsOverAnUnrelatedSystemNameAndTrailGeometry()
    {
        var system = System("UnrelatedBuff", Trail());
        var bundle = Bundle(system);
        bundle.SpellPreviews.Add(1, Spell() with
        {
            MissileEffectKey = 77,
            Issues = new[] { new VfxSpellIssue("mSpell.mMissileSpec.heightSolver", VfxSpellIssueKind.Unsupported) }
        });
        Assert.Equal(VfxRigPreset.Missile, VfxSystemRigResolver.Resolve(system, bundle).Preset);
    }

    [Fact]
    public void SkinResolverDeterminesWhichSystemTheProjectileUses()
    {
        var original = System("Original");
        var overrideSystem = new VfxSystemDefinition(999, "Override", "Override", Array.Empty<VfxEmitterDefinition>());
        var bundle = Bundle(original);
        bundle.Systems.Add(overrideSystem.PathHash, overrideSystem);
        bundle.ResourceMap[77] = overrideSystem.PathHash;
        bundle.SpellPreviews.Add(1, Spell() with { MissileEffectKey = 77 });
        Assert.Equal(VfxRigPreset.Still, VfxSystemRigResolver.Resolve(original, bundle).Preset);
        Assert.Equal(VfxRigPreset.Missile, VfxSystemRigResolver.Resolve(overrideSystem, bundle).Preset);
    }

    [Fact]
    public void NamedEffectUsesTheSameResolutionAsSpellPlayback()
    {
        var system = System("Effect");
        var bundle = Bundle(system);
        bundle.ResourceMap.Clear();
        bundle.ResourceMap.Add(Fnv1a.HashLower("ReturnEffect"), system.PathHash);
        bundle.SpellPreviews.Add(1, Spell() with { MissileEffectName = "ReturnEffect" });
        Assert.Equal(VfxRigPreset.Missile, VfxSystemRigResolver.Resolve(system, bundle).Preset);
    }

    [Fact]
    public void ResolveSettingsAttachesSpellMissileSpecAndMatchesPlayerOrEnemyKeys()
    {
        var system = System("PlayerMissile");
        var bundle = Bundle(system);
        var missile = new VfxSpellMissilePreview(
            VfxSpellMissileMovementKind.FixedSpeed,
            2400f,
            null,
            null,
            null,
            null,
            null,
            null);
        bundle.SpellPreviews.Add(1, Spell() with
        {
            MissileEffectPlayerKey = 77,
            Missile = missile,
            ScriptName = "EzrealQ"
        });

        var resolved = VfxSystemRigResolver.ResolveSettings(system, bundle);
        Assert.Equal(VfxRigPreset.Missile, resolved.Settings.Preset);
        Assert.Same(missile, resolved.Settings.Missile);
        Assert.Equal("EzrealQ", resolved.Settings.SpellName);
        Assert.Equal(2400f, resolved.Settings.FlightSpeed);
    }

    [Fact]
    public void ConflictingImpactAndProjectileUsesRemainStationary()
    {
        var system = System("Shared");
        var bundle = Bundle(system);
        bundle.SpellPreviews.Add(1, Spell() with { MissileEffectKey = 77 });
        bundle.SpellPreviews.Add(2, Spell() with { HaveHitEffect = true, HitEffectKey = 77 });
        Assert.Equal(VfxRigPreset.Still, VfxSystemRigResolver.Resolve(system, bundle).Preset);
    }

    [Fact]
    public void ImpactReferenceKeepsATrailPrimitiveStationary()
    {
        var system = System("Impact", Trail());
        var bundle = Bundle(system);
        bundle.SpellPreviews.Add(1, Spell() with { HaveHitEffect = true, HitEffectKey = 77 });
        Assert.Equal(VfxRigPreset.Still, VfxSystemRigResolver.Resolve(system, bundle).Preset);
    }

    [Fact]
    public void NamesAloneCannotIdentifyAProjectile()
    {
        var system = System("Hero_Q_missile_trail");
        Assert.Equal(VfxRigPreset.Still, VfxSystemRigResolver.Resolve(system, Bundle(system)).Preset);
    }

    [Fact]
    public void RibbonWithoutItsOwnMotionGetsAMovingAnchorPreview()
    {
        var system = System("Ribbon", Trail());
        Assert.Equal(VfxRigPreset.Missile, VfxSystemRigResolver.Resolve(system, Bundle(system)).Preset);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("culled")]
    [InlineData("local")]
    [InlineData("velocity")]
    [InlineData("animated-origin")]
    [InlineData("no-ribbon")]
    public void TrailFallbackRespectsVisibilityAndKeepsAuthoredParticleMotion(string variant)
    {
        var emitter = Trail();
        emitter = variant switch
        {
            "disabled" => emitter with { Disabled = true },
            "culled" => emitter with { Culled = VfxCullReason.Importance },
            "local" => emitter with { IsEmitterSpace = true },
            "velocity" => emitter with { BirthVelocity = VfxCurve3.Const(Vector3.UnitY) },
            "animated-origin" => emitter with { EmitterPosition = new VfxCurve3(Vector3.Zero, new[] { 0f, 1f }, new[] { Vector3.Zero, Vector3.UnitY }) },
            _ => emitter with { Trail = null }
        };
        var system = System("Ribbon", emitter);
        var expected = variant is "local" or "velocity" or "animated-origin" ? VfxRigPreset.Missile : VfxRigPreset.Still;
        Assert.Equal(expected, VfxSystemRigResolver.Resolve(system, Bundle(system)).Preset);
    }

    private static VfxSystemDefinition System(string name, params VfxEmitterDefinition[] emitters)
        => new(123, name, name, emitters);

    private static VfxLoadingService.Bundle Bundle(VfxSystemDefinition system)
    {
        var bundle = new VfxLoadingService.Bundle();
        bundle.Systems.Add(system.PathHash, system);
        bundle.ResourceMap.Add(77, system.PathHash);
        return bundle;
    }

    private static VfxSpellPreview Spell()
        => new(null, null, null, 0, null, null, null, null, 0, null, Array.Empty<VfxSpellIssue>());

    private static VfxEmitterDefinition Trail() => new(
        "ribbon", VfxCurveF.Const(10), VfxCurveF.Const(1), null, 0, 0, false, false, 1,
        VfxCurve3.Const(Vector3.One), null, VfxCurve4.Const(Vector4.One), null,
        null, null, null, VfxCurve3.Const(Vector3.Zero), "test.dds", Vector2.One, 1, false, false,
        PrimitiveKind: VfxPrimitiveKind.CameraTrail,
        Trail: new VfxTrailDefinition(VfxCurve3.Const(Vector3.One), 0, 1, 0, 0));
}
