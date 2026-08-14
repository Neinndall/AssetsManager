using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
namespace AssetsManager.Views.Models.Viewer
{
    public enum VfxPrimitiveKind
    {
        CameraQuad,
        CameraUnitQuad,
        ArbitraryQuad,
        Mesh,
        AttachedMesh,
        CameraTrail,
        ArbitraryTrail,
        Ray,
        Beam,
        PlanarProjection,
        Unsupported
    }

    internal sealed record VfxBinDocument(
        IReadOnlyDictionary<uint, VfxSystemDefinition> Systems,
        IReadOnlyDictionary<uint, uint> ResourceMap,
        IReadOnlyList<string> Dependencies,
        IReadOnlyList<VfxEventSequenceDefinition> EventSequences,
        VfxOwnerSceneContext OwnerSceneContext);

    /// <summary>
    /// Domain graph for a League VFX system and its emitter nodes.
    /// </summary>
    public sealed record VfxSystemDefinition(
        uint PathHash,
        string Name,
        string ParticlePath,
        IReadOnlyList<VfxEmitterDefinition> Emitters,
        float VisibilityRadius = 0f,
        Matrix4x4? Transform = null,
        VfxSystemAuthoredFeatures AuthoredFeatures = null);

    /// <summary>One emitter inside a system. Curves are absolute-valued (sampled over normalised particle age 0..1).</summary>
    public sealed record VfxEmitterDefinition(
        string Name,
        VfxCurveF Rate,                 // particles per second
        VfxCurveF ParticleLifetime,     // seconds a particle lives
        float? EmitterLifetime,         // emitter runtime; null = infinite (loops)
        float ParticleLinger,           // retention window used while an emitter shuts down
        float TimeBeforeFirstEmission,
        bool IsSingleParticle,          // burst of exactly one particle
        bool Disabled,
        int BlendMode,                  // raw authored BIN value; VfxBlendModes owns its rendering semantics
        VfxCurve3 BirthScale,           // ABSOLUTE size at birth (birthScale0), world units
        VfxCurve3? ScaleOverLife,       // scale0: normalised MULTIPLIER over age → effective size = BirthScale * this
        VfxCurve4 BirthColor,           // rgba at birth
        VfxCurve4? ColorOverLife,       // color: MULTIPLIER over age → effective colour = BirthColor * this (alpha usually fades)
        VfxCurve3? BirthVelocity,       // initial velocity
        VfxCurve3? Acceleration,        // worldAcceleration (gravity/wind)
        VfxCurve3? BirthRotationalVelocity,
        VfxCurve3 EmitterPosition,      // animated offset of this emitter within the system
        string TexturePath,            // particle sprite (.dds/.tex)
        Vector2 TexDiv,                 // flipbook grid (cols, rows); (1,1) = single frame
        int NumFrames,
        bool RandomStartFrame,
        bool IsMeshPrimitive,           // primitive is a mesh (billboarded only when the mesh can't load)
        string MeshPath = null,        // VfxPrimitiveMesh -> VfxMeshDefinitionData.mSimpleMeshName (.scb/.sco)
        Vector2 UvScrollRate = default, // birthUvScrollRate — mesh particles FLOW by scrolling UVs (waterfalls)
        string MeshSkeletonPath = null, // skinned mesh primitive (.skl)
        string MeshAnimationPath = null, // idle animation (.anm)
        VfxSpawnShape SpawnShape = null,
        VfxCurve3? BirthAcceleration = null,
        VfxCurve3? BirthOrbitalVelocity = null,
        VfxCurve3? BirthDrag = null,
        VfxCurve3? DragOverLife = null,
        VfxCurve3? BirthRotation = null,
        bool IsDirectionOriented = false,
        bool IsArbitraryQuad = false,
        VfxCurveF? BirthFrameRate = null,
        float? FrameRate = null,
        string TextureMultPath = null,
        Vector2 TextureMultTexDiv = default,
        Vector2 TextureMultUvScrollRate = default,
        float StartFrame = 0f,
        bool UseTextureAspect = false,
        VfxDistortionDefinition Distortion = null,
        string ParticleColorTexturePath = null,
        int? ColorLookUpTypeX = null,
        int? ColorLookUpTypeY = null,
        VfxEmitterRenderState RenderState = null,
        byte MiscRenderFlags = 0,
        byte MeshRenderFlags = 0,
        bool UseNavmeshMask = false,
        Vector2? DepthBiasFactors = null,
        bool IsRotationEnabled = true,
        VfxPrimitiveKind PrimitiveKind = VfxPrimitiveKind.CameraQuad,
        VfxCurve3? VelocityOverLife = null,
        VfxCurve3? RotationOverLife = null,
        VfxCurve2? BirthUvOffset = null,
        VfxCurve2? UvScale = null,
        VfxCurveF? UvRotation = null,
        VfxAlphaErosionDefinition AlphaErosion = null,
        VfxChildParticleSetDefinition ChildParticleSet = null,
        VfxFieldCollectionDefinition Fields = null,
        byte ParticleLingerType = 0,
        float EmitterLinger = 0f,
        bool IsEmitterSpace = false,
        bool IsLocalOrientation = false,
        bool ParticleIsLocalOrientation = false,
        bool IsFollowingTerrain = false,
        bool IsGroundLayer = false,
        bool IsUniformScale = false,
        Vector2 EmitterUvScrollRate = default,
        VfxTrailDefinition Trail = null,
        VfxCurve2? BirthUvScrollRateCurve = null,
        VfxCurve2? ParticleUvScrollRate = null,
        VfxCurveF? BirthUvRotateRate = null,
        VfxCurveF? ParticleUvRotateRate = null,
        VfxCurve2? TextureMultBirthUvOffset = null,
        VfxCurve2? TextureMultBirthUvScrollRate = null,
        VfxCurve2? TextureMultParticleUvScroll = null,
        VfxCurve2? TextureMultUvScale = null,
        VfxCurveF? TextureMultUvRotation = null,
        VfxCurveF? TextureMultBirthUvRotateRate = null,
        VfxCurveF? TextureMultParticleUvRotate = null,
        int TextureMultAddressMode = 0,
        bool TextureMultFlipV = true,
        bool RateIsPeriod = false,
        float BirthTimePeriod = 0f,
        bool IsLoop = false,
        Vector2 ColorLookUpOffsets = default,
        Vector2 ColorLookUpScales = default,
        byte ColorRenderFlags = 0,
        bool IsTexturePixelated = false,
        Vector2 UvTransformCenter = default,
        bool TextureMultFlipU = false,
        bool TextureMultRandomStartFrame = true,
        Vector2 TextureMultTransformCenter = default,
        bool TextureMultClampUvScroll = false,
        Vector2 TextureMultEmitterUvScrollRate = default,
        bool TextureMultScrollAlpha = false,
        VfxSoftParticleDefinition SoftParticle = null,
        VfxReflectionDefinition Reflection = null,
        Vector3? RayTargetOffset = null,
        byte Importance = 0,
        VfxCurve3? BirthScale1 = null,
        VfxCurve3? Rotation1 = null,
        byte UvMode = 0,
        VfxCurveF? BindWeight = null,
        VfxFlexShapeDefinition FlexShape = null,
        VfxPaletteDefinition PaletteDefinition = null,
        float DirectionVelocityScale = 0f,
        VfxCurve2? RateByVelocityFunction = null,
        bool HasPostRotateOrientation = false,
        bool ParticlesShareRandomValue = false,
        string FalloffTexturePath = null,
        string AudioSoundOnCreate = null,
        IReadOnlyList<string> FilteringKeywordsExcluded = null,
        Vector4? ModulationFactor = null,
        IReadOnlyList<uint> AttachedSubmeshHashes = null,
        VfxEmitterAuthoredFeatures AuthoredFeatures = null)
    {
        /// <summary>Does this emitter produce anything drawable (has a texture and isn't disabled)?</summary>
        public bool IsVisual => !Disabled && PrimitiveKind != VfxPrimitiveKind.AttachedMesh && (!string.IsNullOrEmpty(TexturePath) ||
            !string.IsNullOrEmpty(TextureMultPath) || !string.IsNullOrEmpty(MeshPath) ||
            Distortion is { NormalMapTexturePath.Length: > 0 });
    }

    public sealed record VfxSystemAuthoredFeatures(
        bool HasMaterialOverrides = false,
        bool HasAssetRemapping = false);

    public sealed record VfxEmitterAuthoredFeatures(
        uint PrimitiveClassHash = 0,
        bool HasCustomMaterial = false,
        bool HasStencil = false,
        bool HasEmissionMesh = false,
        bool HasEmissionSurface = false,
        bool UsesEmissionMeshNormal = false,
        bool HasTranslationOverride = false,
        bool HasRotationOverride = false,
        bool HasScaleOverride = false,
        bool HasPostRotateOrientationAxis = false,
        bool HasPeriodControl = false);

    public sealed record VfxEmitterRenderState(
        int RenderPass,
        byte AlphaReference,
        int TextureAddressMode,
        bool ClampUvScroll,
        bool FlipU,
        bool FlipV,
        bool DisableBackfaceCull,
        byte RenderPhase = VfxAuthoredDefaults.RenderPhaseOverride,
        byte StencilMode = VfxAuthoredDefaults.StencilMode,
        byte StencilReference = VfxAuthoredDefaults.StencilReference,
        uint StencilReferenceId = 0,
        bool WriteAlphaOnly = false,
        bool SortEmittersByPosition = false)
    {
        public static readonly VfxEmitterRenderState Default = new(
            0,
            VfxAuthoredDefaults.AlphaReference,
            0,
            false,
            false,
            false,
            false,
            VfxAuthoredDefaults.RenderPhaseOverride,
            VfxAuthoredDefaults.StencilMode,
            VfxAuthoredDefaults.StencilReference,
            0,
            false,
            false);
        public float AlphaCutoff => AlphaReference / 255f;
        public bool HasStencil => StencilMode != 0 || StencilReference != 0 || StencilReferenceId != 0;
    }

    /// <summary>Riot's screen-space particle distortion stage (heat haze/refraction).</summary>
    public sealed record VfxDistortionDefinition(float Strength, int Mode, string NormalMapTexturePath);

    public sealed record VfxFlexShapeDefinition(
        float ScaleBirthScaleByBoundObjectSize,
        float ScaleEmitOffsetByBoundObjectSize);

    public sealed record VfxPaletteDefinition(
        int PaletteCount,
        VfxCurve3 PaletteSelector,
        string PaletteTexturePath = null,
        Vector4? PaletteSourceMixColor = null);

    public sealed record VfxSoftParticleDefinition(
        float BeginIn,
        float DeltaIn,
        float BeginOut,
        float DeltaOut);

    public sealed record VfxReflectionDefinition(
        float DirectOpacity,
        float GlancingOpacity,
        float ReflectionFresnel,
        float Fresnel,
        Vector4 FresnelColor,
        Vector4 ReflectionFresnelColor,
        string TexturePath);

    public sealed record VfxAlphaErosionDefinition(
        string TexturePath,
        VfxCurveF Drive,
        float FeatherIn,
        float FeatherOut,
        int AddressMode,
        VfxCurve4? ChannelMixer = null);

    public sealed record VfxTrailDefinition(
        VfxCurve3 BirthTilingSize,
        int SmoothingMode,
        int Mode,
        int MaxAddedPerFrame,
        float Cutoff);

    public sealed record VfxChildSystemReference(string Name, uint SystemHash, uint EffectKey);

    public sealed record VfxChildParticleSetDefinition(
        IReadOnlyList<VfxChildSystemReference> Children,
        bool EmitOnDeath,
        VfxCurveF Probability,
        VfxCurve3 RelativeOffset,
        int InheritanceMode);

    public sealed record VfxAccelerationField(VfxCurve3 Acceleration, bool LocalSpace);
    public sealed record VfxAttractionField(VfxCurveF Acceleration, VfxCurve3 Position, VfxCurveF Radius);
    public sealed record VfxDragField(VfxCurveF Strength, VfxCurve3 Position, VfxCurveF Radius);
    public sealed record VfxOrbitalField(VfxCurve3 Direction, bool LocalSpace);
    public sealed record VfxNoiseField(VfxCurveF Frequency, VfxCurveF VelocityDelta, VfxCurve3 Position, VfxCurveF Radius, Vector3 AxisFraction);
    public sealed record VfxFieldCollectionDefinition(
        IReadOnlyList<VfxAccelerationField> Acceleration,
        IReadOnlyList<VfxAttractionField> Attraction,
        IReadOnlyList<VfxDragField> Drag,
        IReadOnlyList<VfxOrbitalField> Orbital,
        IReadOnlyList<VfxNoiseField> Noise);

    /// <summary>
    /// Authored particle spawn volume. EmitOffset is randomized by its ValueVector3
    /// probability tables, then the authored axis/angle rotations are applied in order.
    /// </summary>
    public enum VfxSpawnShapeKind
    {
        Legacy,
        Point,
        Box,
        Sphere,
        Cylinder
    }

    public sealed record VfxSpawnShape(
        VfxSpawnShapeKind Kind,
        VfxCurve3 EmitOffset,
        IReadOnlyList<Vector3> RotationAxes,
        IReadOnlyList<VfxCurveF> RotationAngles,
        Vector3 Size = default,
        float Radius = 0f,
        float Height = 0f,
        byte Flags = 0)
    {
        public Vector3 SampleOffset(Random rng, float t, out Matrix4x4 rotation)
        {
            rotation = Matrix4x4.Identity;
            var offset = (Kind switch
            {
                VfxSpawnShapeKind.Box => new Vector3(
                    SignedUnit(rng) * Size.X * 0.5f,
                    SignedUnit(rng) * Size.Y * 0.5f,
                    SignedUnit(rng) * Size.Z * 0.5f),
                VfxSpawnShapeKind.Sphere => SampleSphere(rng, Radius),
                VfxSpawnShapeKind.Cylinder => SampleCylinder(rng, Radius, Height),
                _ => Vector3.Zero
            }) + EmitOffset.SampleBirth(t, rng);
            int count = Math.Min(RotationAxes.Count, RotationAngles.Count);
            for (int i = 0; i < count; i++)
            {
                var axis = RotationAxes[i];
                if (axis.LengthSquared() <= 1e-8f) continue;
                float radians = RotationAngles[i].SampleBirth(t, rng) * (MathF.PI / 180f);
                Matrix4x4 step = Matrix4x4.CreateFromAxisAngle(Vector3.Normalize(axis), radians);
                offset = Vector3.Transform(offset, step);
                rotation *= step;
            }
            return offset;
        }

        private static float SignedUnit(Random rng) => (float)(rng.NextDouble() * 2d - 1d);

        private static Vector3 SampleSphere(Random rng, float radius)
        {
            float z = SignedUnit(rng);
            float angle = (float)(rng.NextDouble() * Math.Tau);
            float radial = MathF.Sqrt(MathF.Max(0f, 1f - z * z));
            float distance = radius * MathF.Cbrt((float)rng.NextDouble());
            return new Vector3(radial * MathF.Cos(angle), z, radial * MathF.Sin(angle)) * distance;
        }

        private static Vector3 SampleCylinder(Random rng, float radius, float height)
        {
            float angle = (float)(rng.NextDouble() * Math.Tau);
            float distance = radius * MathF.Sqrt((float)rng.NextDouble());
            return new Vector3(
                MathF.Cos(angle) * distance,
                SignedUnit(rng) * height * 0.5f,
                MathF.Sin(angle) * distance);
        }
    }

    /// <summary>One per-component probability table: a particle rolls r in 0..1 at birth and takes the piecewise-linear value at r.</summary>
    public readonly record struct VfxProbTable(float[] Times, float[] Values)
    {
        public bool IsEmpty => Times is not { Length: > 0 } || Values is not { Length: > 0 };
        public float Sample(float r) => VfxCurve.Interp(Times, Values, r, static (a, b, f) => a + (b - a) * f);
    }

    /// <summary>A scalar value that is either constant or an animation curve over normalised age (0..1).</summary>
    public readonly record struct VfxCurveF(float Constant, float[] Times, float[] Values, VfxProbTable[] Prob = null)
    {
        public float Sample(float t)
        {
            if (Times is null || Values is null || Times.Length == 0) return Constant;
            return VfxCurve.Interp(Times, Values, t, static (a, b, f) => a + (b - a) * f);
        }
        /// <summary>Birth-time value: exact per-particle randomisation via the probability table when present.</summary>
        public float SampleBirth(Random rng)
            => SampleBirth(0f, rng);

        public float SampleBirth(float t, Random rng)
        {
            float value = Sample(t);
            return Prob is { Length: > 0 } && !Prob[0].IsEmpty
                ? value * Prob[0].Sample((float)rng.NextDouble())
                : value;
        }
        public static readonly VfxCurveF Zero = new(0f, null, null);
        public static VfxCurveF Const(float v) => new(v, null, null);
    }

    public readonly record struct VfxCurve2(Vector2 Constant, float[] Times, Vector2[] Values, VfxProbTable[] Prob = null)
    {
        public Vector2 Sample(float t)
        {
            if (Times is null || Values is null || Times.Length == 0) return Constant;
            return VfxCurve.Interp(Times, Values, t, static (a, b, f) => Vector2.Lerp(a, b, f));
        }

        public Vector2 SampleBirth(Random rng)
            => SampleBirth(0f, rng);

        public Vector2 SampleBirth(float t, Random rng)
        {
            var value = Sample(t);
            if (Prob is not { Length: > 0 }) return value;
            return new Vector2(
                Prob.Length > 0 && !Prob[0].IsEmpty ? value.X * Prob[0].Sample((float)rng.NextDouble()) : value.X,
                Prob.Length > 1 && !Prob[1].IsEmpty ? value.Y * Prob[1].Sample((float)rng.NextDouble()) : value.Y);
        }

        public static VfxCurve2 Const(Vector2 value) => new(value, null, null);
    }

    /// <summary>A Vector3 value that is either constant or an animation curve over normalised age.</summary>
    public readonly record struct VfxCurve3(Vector3 Constant, float[] Times, Vector3[] Values, VfxProbTable[] Prob = null)
    {
        public Vector3 Sample(float t)
        {
            if (Times is null || Values is null || Times.Length == 0) return Constant;
            return VfxCurve.Interp(Times, Values, t, static (a, b, f) => Vector3.Lerp(a, b, f));
        }
        /// <summary>Birth-time value with per-component probability tables (independent rolls, Riot-style).</summary>
        public Vector3 SampleBirth(Random rng)
            => SampleBirth(0f, rng);

        public Vector3 SampleBirth(float t, Random rng)
        {
            var v = Sample(t);
            if (Prob is not { Length: > 0 }) return v;
            return new Vector3(
                Prob.Length > 0 && !Prob[0].IsEmpty ? v.X * Prob[0].Sample((float)rng.NextDouble()) : v.X,
                Prob.Length > 1 && !Prob[1].IsEmpty ? v.Y * Prob[1].Sample((float)rng.NextDouble()) : v.Y,
                Prob.Length > 2 && !Prob[2].IsEmpty ? v.Z * Prob[2].Sample((float)rng.NextDouble()) : v.Z);
        }
        public bool HasProb => Prob is { Length: > 0 } && Prob.Any(static p => !p.IsEmpty);
        public static VfxCurve3 Const(Vector3 v) => new(v, null, null);
    }

    /// <summary>A Vector4/colour value that is either constant or an animation curve over normalised age.</summary>
    public readonly record struct VfxCurve4(Vector4 Constant, float[] Times, Vector4[] Values, VfxProbTable[] Prob = null)
    {
        public Vector4 Sample(float t)
        {
            if (Times is null || Values is null || Times.Length == 0) return Constant;
            return VfxCurve.Interp(Times, Values, t, static (a, b, f) => Vector4.Lerp(a, b, f));
        }
        public Vector4 SampleBirth(Random rng)
            => SampleBirth(0f, rng);

        public Vector4 SampleBirth(float t, Random rng)
        {
            var v = Sample(t);
            if (Prob is not { Length: > 0 }) return v;
            return new Vector4(
                Prob.Length > 0 && !Prob[0].IsEmpty ? v.X * Prob[0].Sample((float)rng.NextDouble()) : v.X,
                Prob.Length > 1 && !Prob[1].IsEmpty ? v.Y * Prob[1].Sample((float)rng.NextDouble()) : v.Y,
                Prob.Length > 2 && !Prob[2].IsEmpty ? v.Z * Prob[2].Sample((float)rng.NextDouble()) : v.Z,
                Prob.Length > 3 && !Prob[3].IsEmpty ? v.W * Prob[3].Sample((float)rng.NextDouble()) : v.W);
        }
        public static VfxCurve4 Const(Vector4 v) => new(v, null, null);
    }

    internal static class VfxCurve
    {
        /// <summary>Piecewise-linear sample of (times,values) at t, clamped at both ends.</summary>
        public static T Interp<T>(float[] times, T[] values, float t, Func<T, T, float, T> lerp)
        {
            int n = Math.Min(times.Length, values.Length);
            if (n == 0) return default!;
            if (n == 1 || t <= times[0]) return values[0];
            if (t >= times[n - 1]) return values[n - 1];
            for (int i = 1; i < n; i++)
            {
                if (t <= times[i])
                {
                    float span = times[i] - times[i - 1];
                    float f = span > 1e-6f ? (t - times[i - 1]) / span : 0f;
                    return lerp(values[i - 1], values[i], f);
                }
            }
            return values[n - 1];
        }
    }
}
