using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Services.Viewer.Vfx.Semantics;
using AssetsManager.Services.Viewer.Vfx.Session;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx;

public sealed class VfxSystemRigResolverTests
{
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
        Assert.Equal(VfxRigPreset.Trail, VfxSystemRigResolver.Resolve(system, Bundle(system)).Preset);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("culled")]
    [InlineData("local")]
    [InlineData("velocity")]
    [InlineData("animated-origin")]
    [InlineData("no-ribbon")]
    public void OtherRibbonUsesDoNotImplyExternalMotion(string variant)
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
        Assert.Equal(VfxRigPreset.Still, VfxSystemRigResolver.Resolve(system, Bundle(system)).Preset);
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