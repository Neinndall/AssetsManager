using System;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Composition;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Services.Viewer.Vfx.Session;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Semantics;

/// <summary>Chooses an isolated system's preview rig from authored usage and emission geometry.</summary>
internal static class VfxSystemRigResolver
{
    internal static float? BeamReach(VfxSystemDefinition system)
    {
        var shown = system?.Emitters;
        if (shown == null || !shown.Any(emitter => !emitter.Disabled && emitter.Beam != null)) return null;
        float? reach = null;
        foreach (var emitter in shown)
        {
            if (emitter.Disabled || emitter.Beam != null || emitter.EmitterPosition.Times?.Length > 0) continue;
            Vector3 position = emitter.EmitterPosition.Constant;
            if (position.Z < VfxRigMotion.ChampionHeight || new Vector2(position.X, position.Y).Length() > position.Z * 0.01f) continue;
            reach = MathF.Max(reach ?? 0, position.Z);
        }
        return reach;
    }

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
