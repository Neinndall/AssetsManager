using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Services.Viewer.Vfx.Parsing;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Viewer.Vfx.Composition;

/// <summary>
/// Compiles one SpellObject into the animation/projectile/impact inputs consumed by the shared
/// VFX runtime. This is orchestration only: systems still render through VfxRenderSession.
/// </summary>
internal static class VfxSpellPreviewComposer
{
    internal const int ProjectileSeed = 7331;
    internal const int ImpactSeed = 3137;
    internal const double ImpactDuration = 0.5d;
    private const double TimingTolerance = 0.00001d;

    internal static VfxSpellPreviewPlan Build(
        VfxSpellBrowserItem spell,
        VfxLoadingService.Bundle bundle,
        IReadOnlyList<AnimationClipCatalogItem> clips,
        Func<AnimationClipCatalogItem, double, string, Vector3?> sourceAt,
        Func<uint, string> resolveSystemPath)
    {
        if (spell == null || bundle == null)
            return Empty(VfxSpellAvailability.Unavailable, "Spell preview is unavailable.");
        if (spell.DeclarationCount != 1)
            return Empty(VfxSpellAvailability.Ambiguous, "Spell has multiple declarations.");

        VfxSpellPreview preview = spell.Preview;
        VfxSpellAvailability availability = VfxSpellPreviewReader.AvailabilityOf(preview, includeImpact: true);
        if (availability != VfxSpellAvailability.Supported)
            return Empty(availability, "Spell has no supported isolated preview.");

        if (!TryCastRelease(preview, out double release))
            return Empty(VfxSpellAvailability.Unsupported, "Spell has conflicting cast timing.");

        AnimationClipCatalogItem animation = ResolveAnimation(preview.AnimationName, clips);
        string startBone = preview.Missile?.StartBoneName ?? string.Empty;
        double missileDelay = preview.Missile?.StartDelay ?? 0d;
        if (!double.IsFinite(missileDelay) || missileDelay < 0d)
            return Empty(VfxSpellAvailability.Unsupported, "Spell has an invalid missile start delay.");

        double anchorTime = release + missileDelay;
        Vector3? sampledSource = string.IsNullOrEmpty(startBone)
            ? Vector3.Zero
            : sourceAt?.Invoke(animation, anchorTime, startBone);
        if (!sampledSource.HasValue)
            return Empty(VfxSpellAvailability.Unavailable, $"Missing launch bone: {startBone}.");
        Vector3 source = sampledSource.Value;
        float range = preview.CastRange is > 0f and <= 100000f && float.IsFinite(preview.CastRange.Value)
            ? preview.CastRange.Value
            : 500f;
        Vector3 target = new(range, source.Y, 0f);

        (uint projectileKey, VfxSystemDefinition projectileSystem) = ResolveEffect(
            preview.MissileEffectKey,
            preview.MissileEffectName,
            bundle,
            resolveSystemPath);
        (double Delay, double Duration)? flight = preview.Missile == null || preview.HasInvalidIssues
            ? null
            : CompileFlight(preview.Missile, source, target);
        bool hasProjectile = flight.HasValue && projectileSystem != null;

        (uint impactKey, VfxSystemDefinition impactSystem) = preview.HaveHitEffect == false || preview.HasInvalidHitEffectFields
            ? (0u, null)
            : ResolveEffect(preview.HitEffectKey, preview.HitEffectName, bundle, resolveSystemPath);

        double launch = release + (hasProjectile ? flight.Value.Delay : 0d);
        double flightDuration = hasProjectile
            ? (flight.Value.Duration > 0d ? flight.Value.Duration : 0.5d)
            : 0d;
        double arrival = launch + flightDuration;
        var steps = new List<VfxSpellPlaybackStep>(2);

        if (hasProjectile)
        {
            steps.Add(new VfxSpellPlaybackStep(
                "projectile",
                projectileSystem,
                projectileKey,
                launch,
                arrival,
                source,
                target,
                VfxSpellPlaybackMotion.Path,
                ProjectileSeed));
        }

        if (impactSystem != null)
        {
            steps.Add(new VfxSpellPlaybackStep(
                "impact",
                impactSystem,
                impactKey,
                arrival,
                arrival + ImpactDuration,
                target,
                target,
                VfxSpellPlaybackMotion.Static,
                ImpactSeed));
        }

        string status = BuildStatus(animation, hasProjectile, impactSystem != null);
        return new VfxSpellPreviewPlan(
            availability,
            animation,
            steps,
            release,
            arrival,
            startBone,
            source,
            target,
            status);
    }

    internal static AnimationClipCatalogItem ResolveAnimation(
        string animationName,
        IReadOnlyList<AnimationClipCatalogItem> clips)
    {
        if (string.IsNullOrWhiteSpace(animationName) || clips == null) return null;
        uint hash = Fnv1a.HashLower(animationName);
        AnimationClipCatalogItem clip = clips.FirstOrDefault(item =>
            item?.Clip?.OwnerPathHash == hash ||
            string.Equals(item?.Name, animationName, StringComparison.OrdinalIgnoreCase));
        var visited = new HashSet<uint>();
        while (clip?.Clip != null && visited.Add(clip.Clip.OwnerPathHash))
        {
            if (!string.IsNullOrWhiteSpace(clip.Clip.AnimationFilePath)) return clip;
            IReadOnlyList<uint> children = clip.Clip.ChildClipHashes ?? Array.Empty<uint>();
            if (children.Count != 1) return null;
            uint child = children[0];
            uint graph = clip.Clip.GraphPathHash;
            clip = clips.FirstOrDefault(item =>
                item?.Clip?.GraphPathHash == graph && item.Clip.OwnerPathHash == child);
        }
        return null;
    }

    internal static bool TryCastRelease(VfxSpellPreview preview, out double release)
    {
        release = 0d;
        if (preview == null) return false;
        if (preview.SpellCastTime.HasValue && preview.CastTime.HasValue &&
            Math.Abs(preview.SpellCastTime.Value - preview.CastTime.Value) > TimingTolerance)
        {
            return false;
        }

        release = preview.SpellCastTime ?? preview.CastTime ?? 0f;
        return double.IsFinite(release) && release >= 0d && release <= 30d;
    }

    internal static (double Delay, double Duration)? CompileFlight(
        VfxSpellMissilePreview missile,
        Vector3 from,
        Vector3 to)
    {
        if (missile == null || !Finite(from) || !Finite(to) || from.Y != to.Y ||
            OutsidePreviewBounds(from) || OutsidePreviewBounds(to))
        {
            return null;
        }

        double delay = missile.StartDelay ?? 0d;
        if (!double.IsFinite(delay) || delay < 0d) return null;
        double length = Vector3.Distance(from, to);
        double duration = missile.MovementKind switch
        {
            VfxSpellMissileMovementKind.FixedSpeed
                when missile.Speed is > 0f && float.IsFinite(missile.Speed.Value)
                => length / missile.Speed.Value,
            VfxSpellMissileMovementKind.FixedTime
                when missile.Duration is > 0f && float.IsFinite(missile.Duration.Value)
                => missile.Duration.Value,
            _ => double.NaN
        };

        if (!double.IsFinite(duration) || duration < 0d || duration + delay > 30d) return null;
        return (delay, duration);
    }

    private static (uint Key, VfxSystemDefinition System) ResolveEffect(
        uint key,
        string name,
        VfxLoadingService.Bundle bundle,
        Func<uint, string> resolveSystemPath)
    {
        if (key != 0u && bundle.ResourceMap.TryGetValue(key, out uint keyedSystemHash))
        {
            return keyedSystemHash != 0u && bundle.Systems.TryGetValue(keyedSystemHash, out VfxSystemDefinition keyed)
                ? (key, keyed)
                : (0u, null);
        }

        if (string.IsNullOrWhiteSpace(name)) return (0u, null);
        string normalized = name.Trim().ToLowerInvariant().Replace('\\', '/');
        uint nameHash = Fnv1a.HashLower(name);
        var candidates = new List<(uint Key, VfxSystemDefinition System)>();
        foreach ((uint resourceKey, uint systemHash) in bundle.ResourceMap)
        {
            if (systemHash == 0u || !bundle.Systems.TryGetValue(systemHash, out VfxSystemDefinition system)) continue;
            string path = resolveSystemPath?.Invoke(systemHash)?.Replace('\\', '/').ToLowerInvariant();
            string tail = path?.Split('/').LastOrDefault();
            if (resourceKey == nameHash || path == normalized || tail == normalized)
                candidates.Add((resourceKey, system));
        }

        return candidates.Count == 1 ? candidates[0] : (0u, null);
    }

    private static string BuildStatus(
        AnimationClipCatalogItem animation,
        bool projectile,
        bool impact)
    {
        var parts = new List<string>(3);
        if (animation != null) parts.Add("animation");
        if (projectile) parts.Add("projectile");
        if (impact) parts.Add("impact");
        return parts.Count == 0 ? "No skin-resolved visual steps." : string.Join(" + ", parts);
    }

    private static VfxSpellPreviewPlan Empty(VfxSpellAvailability availability, string status)
        => new(
            availability,
            null,
            Array.Empty<VfxSpellPlaybackStep>(),
            0d,
            0d,
            string.Empty,
            Vector3.Zero,
            Vector3.Zero,
            status);

    private static bool Finite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool OutsidePreviewBounds(Vector3 value)
        => Math.Abs(value.X) > 100000f || Math.Abs(value.Y) > 100000f || Math.Abs(value.Z) > 100000f;
}
