using System;
using System.Linq;
using AssetsManager.Services.Viewer.Vfx.Composition;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Services.Viewer.Vfx.Session;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Semantics;

/// <summary>Chooses an isolated system's preview rig from authored usage and emission geometry.</summary>
internal static class VfxSystemRigResolver
{
    internal static (VfxRigPreset Preset, string Reason) Resolve(
        VfxSystemDefinition system,
        VfxLoadingService.Bundle bundle,
        Func<uint, string> resolveSystemPath = null)
    {
        if (system == null)
            return (VfxRigPreset.Still, "No system definition.");

        bool projectile = false;
        bool impact = false;
        foreach (VfxSpellPreview spell in bundle?.SpellPreviews.Values ?? Enumerable.Empty<VfxSpellPreview>())
        {
            if (spell == null) continue;
            // Cast timing and unsupported flight solvers do not invalidate an effect's authored role.
            if (spell.Issues?.Any(issue => issue.Kind == VfxSpellIssueKind.Invalid &&
                    issue.Path is "mSpell.mMissileEffectKey" or "mSpell.mMissileEffectName") != true)
                projectile |= Matches(spell.MissileEffectKey, spell.MissileEffectName);
            if (spell.HaveHitEffect != false && !spell.HasInvalidHitEffectFields)
                impact |= Matches(spell.HitEffectKey, spell.HitEffectName);
        }

        if (projectile && impact)
            return (VfxRigPreset.Still, "Shared projectile and impact effect; no unique motion.");
        if (projectile)
            return (VfxRigPreset.Missile, "Spell missile effect resolved through the active skin.");
        if (impact)
            return (VfxRigPreset.Still, "Spell impact effect resolved through the active skin.");

        // A stationary particle ribbon needs its external anchor to move to leave a trail.
        // Orbit is a preview of that movement, not an authored in-game trajectory.
        if (system.Emitters?.Any(NeedsMovingAnchor) == true)
            return (VfxRigPreset.Trail, "World-space ribbon without its own motion; moving-anchor preview.");
        return (VfxRigPreset.Still, "No unambiguous external movement reference.");

        bool Matches(uint key, string name)
            => bundle != null && VfxSpellPreviewComposer.ResolveEffect(key, name, bundle, resolveSystemPath)
                .System?.PathHash == system.PathHash;
    }

    private static bool NeedsMovingAnchor(VfxEmitterDefinition emitter)
        => !emitter.Disabled && emitter.Culled == VfxCullReason.None && !emitter.IsEmitterSpace &&
           !emitter.IsGroundLayer && !emitter.IsSingleParticle && emitter.DrawsAsTrail &&
           emitter.PrimitiveKind is VfxPrimitiveKind.CameraTrail or VfxPrimitiveKind.ArbitraryTrail &&
           emitter.Fields == null &&
           IsZero(emitter.BirthVelocity) && IsZero(emitter.Acceleration) &&
           IsZero(emitter.BirthAcceleration) && IsZero(emitter.VelocityOverLife) &&
           IsZero(emitter.AccelerationOverLife) && IsZero(emitter.BirthOrbitalVelocity) &&
           (emitter.EmitterPosition.Values == null ||
            emitter.EmitterPosition.Values.All(value => value == emitter.EmitterPosition.Constant));

    private static bool IsZero(VfxCurve3? curve)
        => !curve.HasValue || (curve.Value.Constant == System.Numerics.Vector3.Zero &&
           (curve.Value.Values == null || curve.Value.Values.All(value => value == System.Numerics.Vector3.Zero)));
}