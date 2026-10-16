using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Composition;
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
    private static readonly uint AcceleratingMovementClass = Fnv1a.HashLower("AcceleratingMovement");
    private static readonly uint DecelToLocationMovementClass = Fnv1a.HashLower("DecelToLocationMovement");
    private static readonly uint FixedSpeedSplineMovementClass = Fnv1a.HashLower("FixedSpeedSplineMovement");
    private static readonly uint FixedTimeSplineMovementClass = Fnv1a.HashLower("FixedTimeSplineMovement");
    private static readonly uint PhysicsMovementClass = Fnv1a.HashLower("PhysicsMovement");
    private static readonly uint CircleMovementClass = Fnv1a.HashLower("CircleMovement");
    private static readonly uint SyncCircleMovementClass = Fnv1a.HashLower("SyncCircleMovement");
    private static readonly uint HermiteSplineInfoClass = Fnv1a.HashLower("HermiteSplineInfo");
    private static readonly uint BlendedLinearHeightSolverClass = Fnv1a.HashLower("BlendedLinearHeightSolver");
    private static readonly uint GravityHeightSolverClass = Fnv1a.HashLower("GravityHeightSolver");
    private static readonly uint FollowTerrainHeightSolverClass = Fnv1a.HashLower("FollowTerrainHeightSolver");
    private static readonly uint SinusoidalHeightSolverClass = Fnv1a.HashLower("SinusoidalHeightSolver");
    private static readonly uint CurveTheDifferenceHeightSolverClass = Fnv1a.HashLower("CurveTheDifferenceHeightSolver");
    // League's schema spells the class with the typo `Veritcal`.
    private static readonly uint VerticalFacingMatchVelocityClass = Fnv1a.HashLower("VeritcalFacingMatchVelocity");
    private static readonly uint VerticalFacingFaceTargetClass = Fnv1a.HashLower("VerticalFacingFaceTarget");
    private const uint RankValuesClass = 0x0a0eddc9;

    internal static VfxSpellPreview Read(BinTreeObject spellObject)
    {
        if (spellObject == null) return null;
        var issues = new List<VfxSpellIssue>();
        string scriptName = spellObject.Properties.TryGetValue(Fnv1a.HashLower("mScriptName"), out var script) &&
                            script is BinTreeString scriptText ? scriptText.Value : null;
        if (!TryExpectedStruct(spellObject.Properties, "mSpell", "mSpell", SpellDataResourceClass, issues, out BinTreeStruct spell))
        {
            return new VfxSpellPreview(
                null, null, null, 0u, null, null, null, null, 0u, null, issues)
            {
                HasSpellData = false,
                HasBuffData = TryProperty(spellObject.Properties, "mBuff", out _),
                ScriptName = scriptName
            };
        }

        float? spellCastTime = ReadF32(spell.Properties, "spellCastTime", "mSpell", issues);
        float? castTime = ReadF32(spell.Properties, "mCastTime", "mSpell", issues);
        bool? haveHitEffect = ReadBool(spell.Properties, "bHaveHitEffect", "mSpell", issues);
        uint hitEffectKey = ReadHash(spell.Properties, "mHitEffectKey", "mSpell", issues);
        string hitEffectName = ReadString(spell.Properties, "mHitEffectName", "mSpell", issues);
        string animationName = ReadString(spell.Properties, "mAnimationName", "mSpell", issues);
        uint missileEffectKey = ReadHash(spell.Properties, "mMissileEffectKey", "mSpell", issues);
        string missileEffectName = ReadString(spell.Properties, "mMissileEffectName", "mSpell", issues);
        float? missileSpeed = ReadF32(spell.Properties, "missileSpeed", "mSpell", issues);
        uint missileEffectPlayerKey = ReadOptionalHash(spell.Properties, "mMissileEffectPlayerKey");
        string missileEffectPlayerName = ReadOptionalString(spell.Properties, "mMissileEffectPlayerName");
        uint missileEffectEnemyKey = ReadOptionalHash(spell.Properties, "mMissileEffectEnemyKey");
        string missileEffectEnemyName = ReadOptionalString(spell.Properties, "mMissileEffectEnemyName");

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
            issues)
        {
            ScriptName = scriptName,
            MissileSpeed = missileSpeed,
            MissileEffectPlayerKey = missileEffectPlayerKey,
            MissileEffectPlayerName = missileEffectPlayerName,
            MissileEffectEnemyKey = missileEffectEnemyKey,
            MissileEffectEnemyName = missileEffectEnemyName
        };
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

    /// <summary>Why <see cref="AvailabilityOf"/> finds nothing to play, from what the SpellObject authors.</summary>
    internal static VfxSpellUnplayableReason UnplayableReasonOf(VfxSpellPreview preview)
    {
        if (preview == null || AvailabilityOf(preview) == VfxSpellAvailability.Supported)
            return VfxSpellUnplayableReason.None;
        if (!preview.HasSpellData)
            return preview.HasBuffData ? VfxSpellUnplayableReason.Buff : VfxSpellUnplayableReason.ScriptOnly;
        return preview.Missile != null ? VfxSpellUnplayableReason.UnsupportedMissile : VfxSpellUnplayableReason.NoVisuals;
    }

    internal static bool CanCompileFlight(VfxSpellMissilePreview missile, float distance)
    {
        if (missile == null || !float.IsFinite(distance) || distance < 0f) return false;
        return VfxSpellPreviewComposer.CompileFlight(missile, Vector3.Zero, new Vector3(0f, 0f, distance)) != null;
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
        bool? heightOffsetAtEnd = null;
        float? acceleration = null, minSpeed = null, maxSpeed = null, initialSpeed = null;
        float? drag = null, lifetime = null, angularVelocity = null, linearVelocity = null, radialVelocity = null;
        byte? syncCircleAxis = null;
        Vector3 splineOffset = Vector3.Zero;
        Vector3 splinePoint1 = new(-1f, 0f, 0f);
        Vector3 splinePoint2 = new(1f, 0f, 0f);
        bool splineFromMissile = false;

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
            else if (movement.ClassHash == AcceleratingMovementClass || movement.ClassHash == DecelToLocationMovementClass)
            {
                kind = movement.ClassHash == AcceleratingMovementClass
                    ? VfxSpellMissileMovementKind.Accelerating
                    : VfxSpellMissileMovementKind.DecelToLocation;
                acceleration = ReadF32(movement.Properties, "mAcceleration", movementPath, issues);
                minSpeed = ReadF32(movement.Properties, "mMinSpeed", movementPath, issues);
                maxSpeed = ReadF32(movement.Properties, "mMaxSpeed", movementPath, issues);
                initialSpeed = ReadF32(movement.Properties, "mInitialSpeed", movementPath, issues);
            }
            else if (movement.ClassHash == FixedSpeedSplineMovementClass || movement.ClassHash == FixedTimeSplineMovementClass)
            {
                bool fixedSpeed = movement.ClassHash == FixedSpeedSplineMovementClass;
                kind = fixedSpeed ? VfxSpellMissileMovementKind.FixedSpeedSpline : VfxSpellMissileMovementKind.FixedTimeSpline;
                if (fixedSpeed) speed = ReadF32(movement.Properties, "mSpeed", movementPath, issues);
                else duration = ReadF32(movement.Properties, "mTravelTime", movementPath, issues);
                const string splinePath = movementPath + ".mSplineInfo";
                if (TryExpectedStruct(movement.Properties, "mSplineInfo", splinePath, HermiteSplineInfoClass, issues, out BinTreeStruct spline))
                {
                    splineOffset = ReadVector3OrDefault(spline.Properties, "mStartPositionOffset", splinePath, Vector3.Zero, issues);
                    splinePoint1 = ReadVector3OrDefault(spline.Properties, "mControlPoint1", splinePath, new Vector3(-1f, 0f, 0f), issues);
                    splinePoint2 = ReadVector3OrDefault(spline.Properties, "mControlPoint2", splinePath, new Vector3(1f, 0f, 0f), issues);
                    splineFromMissile = ReadBool(spline.Properties, "mUseMissilePositionAsOrigin", splinePath, issues) ?? false;
                }
            }
            else if (movement.ClassHash == PhysicsMovementClass)
            {
                kind = VfxSpellMissileMovementKind.Physics;
                initialSpeed = ReadF32(movement.Properties, "mInitialSpeed", movementPath, issues);
                drag = ReadF32(movement.Properties, "mDrag", movementPath, issues);
                lifetime = ReadF32(movement.Properties, "mLifetime", movementPath, issues);
            }
            else if (movement.ClassHash == CircleMovementClass)
            {
                kind = VfxSpellMissileMovementKind.Circle;
                angularVelocity = ReadF32(movement.Properties, "mAngularVelocity", movementPath, issues);
                linearVelocity = ReadF32(movement.Properties, "mLinearVelocity", movementPath, issues);
                radialVelocity = ReadF32(movement.Properties, "mRadialVelocity", movementPath, issues);
                lifetime = ReadF32(movement.Properties, "mLifetime", movementPath, issues);
            }
            else if (movement.ClassHash == SyncCircleMovementClass)
            {
                kind = VfxSpellMissileMovementKind.SyncCircle;
                angularVelocity = ReadF32(movement.Properties, "mAngularVelocity", movementPath, issues);
                lifetime = ReadF32(movement.Properties, "mLifetime", movementPath, issues);
                syncCircleAxis = ReadU8(movement.Properties, "mAxisOfRotation", movementPath, issues);
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
            heightOffsetAtEnd = ReadBool(movement.Properties, "mUseHeightOffsetAtEnd", movementPath, issues);
            MarkUnsupported(
                movement.Properties,
                movementPath,
                issues,
                "mStartBoneSkinOverrides",
                "mTracksTarget",
                "mVisualsTrackHiddenTargets",
                "AddBonusAttackRangeToCastRange",
                "mInferDirectionFromFacingIfNeeded",
                "mProjectTargetToCastRange",
                "mUseGroundHeightAtTarget");
        }

        VfxSpellMissileHeightKind heightKind = VfxSpellMissileHeightKind.None;
        float gravity = 0f, sineAmplitude = 0f, sinePeriods = 0f;
        float? heightSolverGravity = null, heightOffset = null, maxSlope = null;
        float? sineAmplitudeRaw = null, sinePeriodsRaw = null, sineVerticalOffset = null, sinePhaseShift = null;
        float? curveInitialTargetHeightOffset = null;
        bool landsOnTargetHeight = false;
        const string heightPath = root + ".heightSolver";
        if (TryAnyStruct(missile.Properties, "heightSolver", heightPath, issues, out BinTreeStruct height))
        {
            if (height.ClassHash == BlendedLinearHeightSolverClass)
            {
                heightKind = VfxSpellMissileHeightKind.BlendedLinear;
                landsOnTargetHeight = true;
            }
            else if (height.ClassHash == GravityHeightSolverClass)
            {
                heightKind = VfxSpellMissileHeightKind.Gravity;
                landsOnTargetHeight = true;
                heightSolverGravity = ReadF32(height.Properties, "mGravity", heightPath, issues);
                gravity = heightSolverGravity ?? 0f;
            }
            else if (height.ClassHash == FollowTerrainHeightSolverClass)
            {
                heightKind = VfxSpellMissileHeightKind.FollowTerrain;
                heightOffset = ReadF32(height.Properties, "mHeightOffset", heightPath, issues);
                maxSlope = ReadF32(height.Properties, "mMaxSlope", heightPath, issues);
            }
            else if (height.ClassHash == SinusoidalHeightSolverClass)
            {
                heightKind = VfxSpellMissileHeightKind.Sinusoidal;
                sineAmplitudeRaw = ReadF32(height.Properties, "mAmplitude", heightPath, issues);
                sinePeriodsRaw = ReadF32(height.Properties, "mNumberOfPeriods", heightPath, issues);
                sineVerticalOffset = ReadF32(height.Properties, "mVerticalOffset", heightPath, issues);
                sinePhaseShift = ReadF32(height.Properties, "PhaseShift", heightPath, issues);
                sineAmplitude = sineAmplitudeRaw ?? 0f;
                sinePeriods = sinePeriodsRaw ?? 0f;
            }
            else if (height.ClassHash == CurveTheDifferenceHeightSolverClass)
            {
                heightKind = VfxSpellMissileHeightKind.CurveTheDifference;
                curveInitialTargetHeightOffset = ReadF32(height.Properties, "mInitialTargetHeightOffset", heightPath, issues);
            }
            else
            {
                heightKind = VfxSpellMissileHeightKind.Unsupported;
                Issue(issues, heightPath, VfxSpellIssueKind.Unsupported);
            }
        }

        VfxSpellMissileFacingKind facingKind = VfxSpellMissileFacingKind.None;
        const string facingPath = root + ".verticalFacing";
        if (TryAnyStruct(missile.Properties, "verticalFacing", facingPath, issues, out BinTreeStruct facing))
        {
            if (facing.ClassHash == VerticalFacingMatchVelocityClass)
            {
                facingKind = VfxSpellMissileFacingKind.MatchVelocity;
            }
            else if (facing.ClassHash == VerticalFacingFaceTargetClass)
            {
                facingKind = VfxSpellMissileFacingKind.FaceTarget;
            }
            else
            {
                facingKind = VfxSpellMissileFacingKind.Unsupported;
                Issue(issues, facingPath, VfxSpellIssueKind.Unsupported);
            }
        }

        MarkUnsupported(
            missile.Properties,
            root,
            issues,
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
            initialTargetHeight)
        {
            HeightOffsetAtEnd = heightOffsetAtEnd,
            Acceleration = acceleration,
            MinSpeed = minSpeed,
            MaxSpeed = maxSpeed,
            InitialSpeed = initialSpeed,
            Drag = drag,
            Lifetime = lifetime,
            AngularVelocity = angularVelocity,
            LinearVelocity = linearVelocity,
            RadialVelocity = radialVelocity,
            SyncCircleAxis = syncCircleAxis,
            SplineStartOffset = splineOffset,
            SplineControlPoint1 = splinePoint1,
            SplineControlPoint2 = splinePoint2,
            SplineFromMissile = splineFromMissile,
            HeightKind = heightKind,
            LandsOnTargetHeight = landsOnTargetHeight,
            Gravity = gravity,
            HeightSolverGravity = heightSolverGravity,
            HeightOffset = heightOffset,
            MaxSlope = maxSlope,
            SineAmplitude = sineAmplitude,
            SinePeriods = sinePeriods,
            SineAmplitudeRaw = sineAmplitudeRaw,
            SinePeriodsRaw = sinePeriodsRaw,
            SineVerticalOffset = sineVerticalOffset,
            SinePhaseShift = sinePhaseShift,
            CurveInitialTargetHeightOffset = curveInitialTargetHeightOffset,
            FacingKind = facingKind
        };
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

    private static Vector3 ReadVector3OrDefault(
        IReadOnlyDictionary<uint, BinTreeProperty> properties,
        string field,
        string parent,
        Vector3 fallback,
        ICollection<VfxSpellIssue> issues)
    {
        if (!TryProperty(properties, field, out BinTreeProperty property)) return fallback;
        if (Unwrap(property) is BinTreeVector3 value &&
            float.IsFinite(value.Value.X) && float.IsFinite(value.Value.Y) && float.IsFinite(value.Value.Z))
            return value.Value;
        Issue(issues, $"{parent}.{field}", VfxSpellIssueKind.Invalid);
        return fallback;
    }

    private static byte? ReadU8(
        IReadOnlyDictionary<uint, BinTreeProperty> properties,
        string field,
        string parent,
        ICollection<VfxSpellIssue> issues)
    {
        if (!TryProperty(properties, field, out BinTreeProperty property)) return null;
        if (Unwrap(property) is BinTreeU8 value) return value.Value;
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

    private static string ReadOptionalString(
        IReadOnlyDictionary<uint, BinTreeProperty> properties,
        string field)
    {
        if (!TryProperty(properties, field, out BinTreeProperty property)) return null;
        return Unwrap(property) is BinTreeString value ? value.Value : null;
    }

    private static uint ReadOptionalHash(
        IReadOnlyDictionary<uint, BinTreeProperty> properties,
        string field)
    {
        if (!TryProperty(properties, field, out BinTreeProperty property)) return 0u;
        return Unwrap(property) is BinTreeHash value ? value.Value : 0u;
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
