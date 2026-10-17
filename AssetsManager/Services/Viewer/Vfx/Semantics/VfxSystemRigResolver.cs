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
        (VfxRigSettings settings, string reason) = ResolveSettings(system, bundle, resolveSystemPath);
        return (settings.Preset, reason);
    }

    internal static (VfxRigSettings Settings, string Reason) ResolveSettings(
        VfxSystemDefinition system,
        VfxLoadingService.Bundle bundle,
        Func<uint, string> resolveSystemPath = null)
    {
        if (system == null)
            return (VfxRigSettings.ForPreset(VfxRigPreset.Still), "No system definition.");

        if (bundle?.MissileSpells.TryGetValue(system.PathHash, out var catalog) == true)
            foreach (var spell in catalog)
                if (VfxSpellFlightPath.CanSimulateForRig(spell.Missile))
                    return (SpellRig(spell, spell.ScriptName),
                        $"Spell missile effect ({spell.ScriptName}) resolved through the active skin.");

        bool projectile = false;
        bool impact = false;
        VfxSpellPreview matchedSpell = null;
        string matchedSpellName = null;

        foreach ((uint spellHash, VfxSpellPreview spell) in bundle?.SpellPreviews ?? new System.Collections.Generic.Dictionary<uint, VfxSpellPreview>())
        {
            if (spell == null) continue;
            // Cast timing and unsupported flight solvers do not invalidate an effect's authored role.
            if (spell.Issues?.Any(issue => issue.Kind == VfxSpellIssueKind.Invalid &&
                    issue.Path is "mSpell.mMissileEffectKey" or "mSpell.mMissileEffectName") != true)
            {
                bool isSpellMissile =
                    Matches(spell.MissileEffectKey, spell.MissileEffectName) ||
                    Matches(spell.MissileEffectPlayerKey, spell.MissileEffectPlayerName) ||
                    Matches(spell.MissileEffectEnemyKey, spell.MissileEffectEnemyName);
                if (isSpellMissile)
                {
                    projectile = true;
                    if (matchedSpell == null && VfxSpellFlightPath.CanSimulateForRig(spell.Missile))
                    {
                        matchedSpell = spell;
                        matchedSpellName = !string.IsNullOrWhiteSpace(spell.ScriptName)
                            ? spell.ScriptName
                            : $"0x{spellHash:x8}";
                    }
                }
            }
            if (spell.HaveHitEffect != false && !spell.HasInvalidHitEffectFields)
                impact |= Matches(spell.HitEffectKey, spell.HitEffectName);
        }

        Vector3? beamTarget = BeamReach(system) is { } reach ? new Vector3(0f, 0f, reach) : null;

        if (projectile && impact && matchedSpell == null)
            return (VfxRigSettings.ForPreset(VfxRigPreset.Still) with { Target = beamTarget }, "Shared projectile and impact effect; no unique motion.");
        if (projectile)
        {
            VfxRigSettings missileSettings = matchedSpell != null
                ? SpellRig(matchedSpell, matchedSpellName)
                : VfxRigSettings.ForPreset(VfxRigPreset.Missile);
            string reason = matchedSpellName != null
                ? $"Spell missile effect ({matchedSpellName}) resolved through the active skin."
                : "Spell missile effect resolved through the active skin.";
            return (missileSettings, reason);
        }
        if (impact)
            return (VfxRigSettings.ForPreset(VfxRigPreset.Still) with { Target = beamTarget }, "Spell impact effect resolved through the active skin.");

        if (system.Emitters?.Any(emitter => !emitter.Disabled && emitter.Culled == VfxCullReason.None && emitter.DrawsAsTrail) == true)
            return (VfxRigSettings.ForPreset(VfxRigPreset.Missile) with { FlightSpeed = VfxSpellFlightPath.FallbackSpeed },
                "Trail without a resolved spell; straight missile preview.");
        return (VfxRigSettings.ForPreset(VfxRigPreset.Still) with { Target = beamTarget }, "No unambiguous external movement reference.");

        bool Matches(uint key, string name)
            => bundle != null && VfxSpellPreviewComposer.ResolveEffect(key, name, bundle, resolveSystemPath)
                .System?.PathHash == system.PathHash;
    }

    private static VfxRigSettings SpellRig(VfxSpellPreview spell, string name)
    {
        VfxSpellMissilePreview missile = spell.Missile;
        float speed = Positive(missile.Speed) ?? VfxSpellFlightPath.FallbackSpeed;
        if (missile.MovementKind is VfxSpellMissileMovementKind.FixedSpeed or VfxSpellMissileMovementKind.FixedSpeedSpline)
        {
            speed = Positive(missile.Speed) ?? Positive(spell.MissileSpeed) ?? VfxSpellFlightPath.FallbackSpeed;
            // Keep the written movement intact for other consumers and use a resolved copy only for this rig.
            if (missile.Speed != speed) missile = missile with { Speed = speed };
        }
        return VfxRigSettings.ForPreset(VfxRigPreset.Missile) with
        {
            FlightRange = spell.CastRange is > 0f and <= 100000f && float.IsFinite(spell.CastRange.Value)
                ? spell.CastRange.Value : VfxRigMotion.FlightRange,
            FlightSpeed = speed,
            Missile = missile,
            SpellName = name
        };
    }

    private static float? Positive(float? value)
        => value is > 0f && float.IsFinite(value.Value) ? value : null;

}
