using System;
using System.Collections.Generic;
using System.Linq;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Viewer.Vfx.Parsing;

/// <summary>
/// Reads the authored SpellObject fields consumed by the isolated ability preview.
/// Catalog discovery stays separate from spell semantics, matching LTK's object-index/readSpell split.
/// </summary>
internal static class VfxSpellPreviewReader
{
    private static readonly uint SpellDataResourceClass = Fnv1a.HashLower("SpellDataResource");
    private static readonly uint MissileSpecificationClass = Fnv1a.HashLower("MissileSpecification");
    private static readonly uint FixedSpeedMovementClass = Fnv1a.HashLower("FixedSpeedMovement");
    private static readonly uint FixedTimeMovementClass = Fnv1a.HashLower("FixedTimeMovement");
    private const uint RankValuesClass = 0x0a0eddc9;

    internal static VfxSpellPreview Read(BinTreeObject spellObject)
    {
        if (spellObject == null) return null;
        var issues = new List<VfxSpellIssue>();
        if (!TryExpectedStruct(spellObject.Properties, "mSpell", "mSpell", SpellDataResourceClass, issues, out BinTreeStruct spell))
        {
            return new VfxSpellPreview(
                null, null, null, 0u, null, null, null, null, 0u, null, issues);
        }

        float? spellCastTime = ReadF32(spell.Properties, "spellCastTime", "mSpell", issues);
        float? castTime = ReadF32(spell.Properties, "mCastTime", "mSpell", issues);
        bool? haveHitEffect = ReadBool(spell.Properties, "bHaveHitEffect", "mSpell", issues);
        uint hitEffectKey = ReadHash(spell.Properties, "mHitEffectKey", "mSpell", issues);
        string hitEffectName = ReadString(spell.Properties, "mHitEffectName", "mSpell", issues);
        string animationName = ReadString(spell.Properties, "mAnimationName", "mSpell", issues);
        uint missileEffectKey = ReadHash(spell.Properties, "mMissileEffectKey", "mSpell", issues);
        string missileEffectName = ReadString(spell.Properties, "mMissileEffectName", "mSpell", issues);

        float? displayRange = ReadRankOne(spell.Properties, "castRangeDisplayOverride", "mSpell", issues);
        float? valueRange = ReadNestedRankOne(spell.Properties, "castRangeValues", "mSpell", issues);
        float? legacyRange = ReadRankOne(spell.Properties, "castRange", "mSpell", issues);
        _ = ReadRankOne(spell.Properties, "castRadius", "mSpell", issues);
        _ = ReadRankOne(spell.Properties, "castRadiusSecondary", "mSpell", issues);
        _ = ReadF32(spell.Properties, "castConeAngle", "mSpell", issues);
        _ = ReadF32(spell.Properties, "castConeDistance", "mSpell", issues);

        VfxSpellMissilePreview missile = null;
        if (TryExpectedStruct(
                spell.Properties,
                "mMissileSpec",
                "mSpell.mMissileSpec",
                MissileSpecificationClass,
                issues,
                out BinTreeStruct missileStruct))
        {
            missile = ReadMissile(missileStruct, issues);
        }

        MarkUnsupported(
            spell.Properties,
            "mSpell",
            issues,
            "mResourceResolvers",
            "mMissileEffectPlayerKey",
            "mMissileEffectEnemyKey",
            "mMissileEffectPlayerName",
            "mMissileEffectEnemyName",
            "mParticleStartOffset");

        return new VfxSpellPreview(
            spellCastTime,
            castTime,
            haveHitEffect,
            hitEffectKey,
            hitEffectName,
            Positive(displayRange) ?? Positive(valueRange) ?? Positive(legacyRange),
            animationName,
            missile,
            missileEffectKey,
            missileEffectName,
            issues);
    }

    internal static VfxSpellAvailability AvailabilityOf(VfxSpellPreview preview, bool includeImpact = true)
    {
        if (preview == null) return VfxSpellAvailability.Unavailable;
        bool impact = includeImpact &&
                      preview.HaveHitEffect != false &&
                      (preview.HitEffectKey != 0u || !string.IsNullOrWhiteSpace(preview.HitEffectName));
        bool animation = !string.IsNullOrWhiteSpace(preview.AnimationName);
        bool missile = preview.Missile != null &&
                       !preview.HasInvalidIssues &&
                       CanCompileFlight(preview.Missile, 800f);
        return impact || animation || missile
            ? VfxSpellAvailability.Supported
            : VfxSpellAvailability.Unsupported;
    }

    internal static bool CanCompileFlight(VfxSpellMissilePreview missile, float distance)
    {
        if (missile == null || !float.IsFinite(distance) || distance < 0f) return false;
        float delay = missile.StartDelay ?? 0f;
        if (!float.IsFinite(delay) || delay < 0f) return false;
        float duration = missile.MovementKind switch
        {
            VfxSpellMissileMovementKind.FixedSpeed
                when missile.Speed is > 0f && float.IsFinite(missile.Speed.Value)
                => distance / missile.Speed.Value,
            VfxSpellMissileMovementKind.FixedTime
                when missile.Duration is > 0f && float.IsFinite(missile.Duration.Value)
                => missile.Duration.Value,
            _ => float.NaN
        };
        return float.IsFinite(duration) && duration >= 0f && duration + delay <= 30f;
    }

    private static VfxSpellMissilePreview ReadMissile(
        BinTreeStruct missile,
        ICollection<VfxSpellIssue> issues)
    {
        const string root = "mSpell.mMissileSpec";
        const string movementPath = "mSpell.mMissileSpec.movementComponent";
        VfxSpellMissileMovementKind kind = VfxSpellMissileMovementKind.Missing;
        float? speed = null;
        float? duration = null;
        float? startDelay = null;
        string startBone = null;
        string targetBone = null;
        float? targetHeight = null;
        float? initialTargetHeight = null;

        if (TryAnyStruct(missile.Properties, "movementComponent", movementPath, issues, out BinTreeStruct movement))
        {
            if (movement.ClassHash == FixedSpeedMovementClass)
            {
                kind = VfxSpellMissileMovementKind.FixedSpeed;
                speed = ReadF32(movement.Properties, "mSpeed", movementPath, issues);
            }
            else if (movement.ClassHash == FixedTimeMovementClass)
            {
                kind = VfxSpellMissileMovementKind.FixedTime;
                duration = ReadF32(movement.Properties, "mTravelTime", movementPath, issues);
            }
            else
            {
                kind = VfxSpellMissileMovementKind.Unsupported;
            }

            startDelay = ReadF32(movement.Properties, "mStartDelay", movementPath, issues);
            startBone = ReadString(movement.Properties, "mStartBoneName", movementPath, issues);
            targetBone = ReadString(movement.Properties, "mTargetBoneName", movementPath, issues);
            targetHeight = ReadF32(movement.Properties, "mTargetHeightAugment", movementPath, issues);
            initialTargetHeight = ReadF32(movement.Properties, "mOffsetInitialTargetHeight", movementPath, issues);
            MarkUnsupported(
                movement.Properties,
                movementPath,
                issues,
                "mStartBoneSkinOverrides",
                "mTracksTarget",
                "mUseHeightOffsetAtEnd",
                "mVisualsTrackHiddenTargets",
                "AddBonusAttackRangeToCastRange",
                "mInferDirectionFromFacingIfNeeded",
                "mProjectTargetToCastRange",
                "mUseGroundHeightAtTarget");
        }

        MarkUnsupported(
            missile.Properties,
            root,
            issues,
            "heightSolver",
            "verticalFacing",
            "behaviors",
            "missileGroupSpawners",
            "visibilityComponent",
            "MissileForce");

        return new VfxSpellMissilePreview(
            kind,
            speed,
            duration,
            startDelay,
            startBone,
            targetBone,
            targetHeight,
            initialTargetHeight);
    }

    private static bool TryExpectedStruct(
        IReadOnlyDictionary<uint, BinTreeProperty> properties,
        string field,
        string path,
        uint expectedClass,
        ICollection<VfxSpellIssue> issues,
        out BinTreeStruct value)
    {
        value = null;
        if (!TryProperty(properties, field, out BinTreeProperty property)) return false;
        property = Unwrap(property);
        if (property is BinTreeStruct held && held.ClassHash == 0u) return false;
        if (property is BinTreeStruct typed && typed.ClassHash == expectedClass)
        {
            value = typed;
            return true;
        }
        Issue(issues, path, VfxSpellIssueKind.Invalid);
        return false;
    }

    private static bool TryAnyStruct(
        IReadOnlyDictionary<uint, BinTreeProperty> properties,
        string field,
        string path,
        ICollection<VfxSpellIssue> issues,
        out BinTreeStruct value)
    {
        value = null;
        if (!TryProperty(properties, field, out BinTreeProperty property)) return false;
        property = Unwrap(property);
        if (property is BinTreeStruct held && held.ClassHash == 0u) return false;
        if (property is BinTreeStruct typed)
        {
            value = typed;
            return true;
        }
        Issue(issues, path, VfxSpellIssueKind.Invalid);
        return false;
    }

    private static float? ReadF32(
        IReadOnlyDictionary<uint, BinTreeProperty> properties,
        string field,
        string parent,
        ICollection<VfxSpellIssue> issues)
    {
        if (!TryProperty(properties, field, out BinTreeProperty property)) return null;
        if (Unwrap(property) is BinTreeF32 value && float.IsFinite(value.Value)) return value.Value;
        Issue(issues, $"{parent}.{field}", VfxSpellIssueKind.Invalid);
        return null;
    }

    private static bool? ReadBool(
        IReadOnlyDictionary<uint, BinTreeProperty> properties,
        string field,
        string parent,
        ICollection<VfxSpellIssue> issues)
    {
        if (!TryProperty(properties, field, out BinTreeProperty property)) return null;
        if (Unwrap(property) is BinTreeBool value) return value.Value;
        Issue(issues, $"{parent}.{field}", VfxSpellIssueKind.Invalid);
        return null;
    }

    private static string ReadString(
        IReadOnlyDictionary<uint, BinTreeProperty> properties,
        string field,
        string parent,
        ICollection<VfxSpellIssue> issues)
    {
        if (!TryProperty(properties, field, out BinTreeProperty property)) return null;
        if (Unwrap(property) is BinTreeString value) return value.Value;
        Issue(issues, $"{parent}.{field}", VfxSpellIssueKind.Invalid);
        return null;
    }

    private static uint ReadHash(
        IReadOnlyDictionary<uint, BinTreeProperty> properties,
        string field,
        string parent,
        ICollection<VfxSpellIssue> issues)
    {
        if (!TryProperty(properties, field, out BinTreeProperty property)) return 0u;
        if (Unwrap(property) is BinTreeHash value) return value.Value;
        Issue(issues, $"{parent}.{field}", VfxSpellIssueKind.Invalid);
        return 0u;
    }

    private static float? ReadRankOne(
        IReadOnlyDictionary<uint, BinTreeProperty> properties,
        string field,
        string parent,
        ICollection<VfxSpellIssue> issues)
    {
        if (!TryProperty(properties, field, out BinTreeProperty property)) return null;
        property = Unwrap(property);
        if (property is not BinTreeContainer container || container.Elements.Count != 7)
        {
            Issue(issues, $"{parent}.{field}", VfxSpellIssueKind.Invalid);
            return null;
        }

        var values = new float[7];
        for (int index = 0; index < values.Length; index++)
        {
            if (Unwrap(container.Elements[index]) is not BinTreeF32 value || !float.IsFinite(value.Value))
            {
                Issue(issues, $"{parent}.{field}", VfxSpellIssueKind.Invalid);
                return null;
            }
            values[index] = value.Value;
        }
        return values[1];
    }

    private static float? ReadNestedRankOne(
        IReadOnlyDictionary<uint, BinTreeProperty> properties,
        string field,
        string parent,
        ICollection<VfxSpellIssue> issues)
    {
        if (!TryProperty(properties, field, out BinTreeProperty property)) return null;
        property = Unwrap(property);
        if (property is not BinTreeStruct values || values.ClassHash != RankValuesClass)
        {
            Issue(issues, $"{parent}.{field}", VfxSpellIssueKind.Invalid);
            return null;
        }
        return ReadRankOne(values.Properties, "values", $"{parent}.{field}", issues);
    }

    private static void MarkUnsupported(
        IReadOnlyDictionary<uint, BinTreeProperty> properties,
        string parent,
        ICollection<VfxSpellIssue> issues,
        params string[] fields)
    {
        foreach (string field in fields)
        {
            if (TryProperty(properties, field, out _))
                Issue(issues, $"{parent}.{field}", VfxSpellIssueKind.Unsupported);
        }
    }

    private static bool TryProperty(
        IReadOnlyDictionary<uint, BinTreeProperty> properties,
        string field,
        out BinTreeProperty property)
        => properties.TryGetValue(Fnv1a.HashLower(field), out property);

    private static BinTreeProperty Unwrap(BinTreeProperty property)
        => property is BinTreeOptional optional ? optional.Value : property;

    private static float? Positive(float? value)
        => value is > 0f and <= 100000f && float.IsFinite(value.Value) ? value : null;

    private static void Issue(
        ICollection<VfxSpellIssue> issues,
        string path,
        VfxSpellIssueKind kind)
        => issues.Add(new VfxSpellIssue(path, kind));
}
