using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
namespace AssetsManager.Views.Models.Viewer
{
    public enum VfxPrimitiveKind
    {
        CameraQuad = 0,
        CameraUnitQuad = 1,
        ArbitraryQuad = 2,
        Mesh = 3,
        AttachedMesh = 4,
        CameraTrail = 5,
        ArbitraryTrail = 6,
        Ray = 7,
        Beam = 8,
        PlanarProjection = 9,
        CameraSegmentBeam = 10,
        Unsupported = 11
    }

    public enum VfxDragMotion
    {
        Stepped,
        Analytic
    }

    public enum VfxCustomMaterialBlendFactor
    {
        Zero = 0,
        One = 1,
        SourceColor = 2,
        OneMinusSourceColor = 3,
        DestinationColor = 4,
        OneMinusDestinationColor = 5,
        SourceAlpha = 6,
        OneMinusSourceAlpha = 7
    }

    internal sealed record VfxBinDocument(
        IReadOnlyDictionary<uint, VfxSystemDefinition> Systems,
        IReadOnlyDictionary<uint, uint> ResourceMap,
        IReadOnlyDictionary<uint, uint> SkinResourceMap,
        IReadOnlyList<string> Dependencies,
        IReadOnlyList<AnimationClipDefinition> EventSequences,
        VfxOwnerSceneContext OwnerSceneContext,
        IReadOnlyList<VfxIdleEffectDefinition> IdleEffects = null,
        IReadOnlyList<AnimationGraphDefinition> AnimationGraphs = null);

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
        VfxSystemAuthoredFeatures AuthoredFeatures = null,
        VfxDragMotion DragMotion = VfxDragMotion.Stepped,
        float BuildUpTime = 0f,
        IReadOnlyDictionary<uint, uint> ResourceMap = null);

    /// <summary>One emitter inside a system. Curves are absolute-valued (sampled over normalised particle age 0..1).</summary>
    public sealed record VfxEmitterDefinition(
        string Name,
        VfxCurveF Rate,                 // particles per second
        VfxCurveF ParticleLifetime,     // seconds a particle lives
        float? EmitterLifetime,         // emitter runtime; null = infinite (loops)
        float ParticleLinger,           // retention window used while an emitter shuts down
        float TimeBeforeFirstEmission,
        bool IsSingleParticle,          // one burst; rate determines its particle count
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
        bool IsMeshPrimitive,           // primitive is a mesh; effective draw kind is finalized after asset resolution
        string MeshPath = null,         // resolved authored mesh path (.skn or simple .scb/.tmesh/.gmesh)
        Vector2 UvScrollRate = default, // birthUvScrollRate — mesh particles FLOW by scrolling UVs (waterfalls)
        string MeshSkeletonPath = null, // skinned mesh primitive (.skl)
        string MeshAnimationPath = null, // idle animation (.anm)
        bool MeshIsSkinned = false,
        string MeshFallbackPath = null, // authored mSimpleMeshName used when a skinned mesh asset is unavailable
        bool MeshAlignPitchToCamera = false,
        bool MeshAlignYawToCamera = false,
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
        VfxDistortionDefinition Distortion = null,
        string ParticleColorTexturePath = null,
        int? ColorLookUpTypeX = null,
        int? ColorLookUpTypeY = null,
        VfxEmitterRenderState RenderState = null,
        byte MiscRenderFlags = 0,
        byte MeshRenderFlags = 0,
        bool UseNavmeshMask = false,
        Vector2? DepthBiasFactors = null,
        bool IsRotationEnabled = false,
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
        bool IsLocalOrientation = true,
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
        bool TextureMultFlipV = false,
        bool RateIsPeriod = false,
        float BirthTimePeriod = 0f,
        bool IsLoop = false,
        Vector2 ColorLookUpOffsets = default,
        Vector2 ColorLookUpScales = default,
        byte ColorRenderFlags = 0,
        bool IsTexturePixelated = false,
        Vector2 UvTransformCenter = default,
        bool TextureMultFlipU = false,
        Vector2 TextureMultTransformCenter = default,
        bool TextureMultClampUvScroll = false,
        Vector2 TextureMultEmitterUvScrollRate = default,
        VfxSoftParticleDefinition SoftParticle = null,
        VfxReflectionDefinition Reflection = null,
        Vector3? RayTargetOffset = null,
        byte Importance = 0,
        byte ColorblindVisibility = 0,
        VfxCullReason Culled = VfxCullReason.None,
        VfxCurve3? BirthScale1 = null,
        VfxCurve3? Rotation1 = null,
        byte UvMode = 0,
        VfxCurveF? BindWeight = null,
        VfxFlexShapeDefinition FlexShape = null,
        VfxPaletteDefinition PaletteDefinition = null,
        float DirectionVelocityScale = 0f,
        float DirectionVelocityMinScale = 1f,
        bool ParticlesShareRandomValue = false,
        string AudioSoundOnCreate = null,
        IReadOnlyList<string> FilteringKeywordsExcluded = null,
        Vector4? ModulationFactor = null,
        // Keep the two authored submesh lists separate. League applies mSubmeshesToDraw
        // as a narrowing mask and mSubmeshesToDrawAlways after visibility filtering.
        IReadOnlyList<uint> SubmeshesToDraw = null,
        IReadOnlyList<uint> SubmeshesToDrawAlways = null,
        IReadOnlyList<uint> AttachedSubmeshHashes = null,
        VfxEmitterAuthoredFeatures AuthoredFeatures = null,
        Vector3? TranslationOverride = null,
        Vector3? RotationOverride = null,
        Vector3? ScaleOverride = null,
        VfxCurve3? AccelerationOverLife = null,
        VfxCurve3? BirthRotationalAcceleration = null,
        VfxCurveF? LegacyBirthScale = null,
        Vector2? LegacyScaleBias = null,
        VfxCurveF? LegacyScale = null,
        VfxCurveF? LegacyRotation = null,
        byte LegacyOrientation = 0,
        bool LegacyScaleUpFromOrigin = false,
        bool LegacyLockedToEmitter = false,
        bool LegacyHasFixedOrbit = false,
        byte LegacyFixedOrbitType = 1,
        Vector2 LegacyParticleBind = default,
        float DepthPushPull = 0f,
        VfxBeamDefinition Beam = null,
        VfxLingerDefinition Linger = null,
        bool IsSimpleEmitter = false,
        IReadOnlyList<string> MeshAnimationVariants = null,
        VfxEmissionSurfaceDefinition EmissionSurface = null,
        uint CustomMaterialPathHash = 0,
        ModelMaterialDefinition CustomMaterial = null,
        VfxCustomMaterialBlendFactor CustomMaterialSourceBlendFactor = VfxCustomMaterialBlendFactor.One,
        VfxCustomMaterialBlendFactor CustomMaterialDestinationBlendFactor = VfxCustomMaterialBlendFactor.Zero)
    {
        /// <summary>LTK drawKind.ts: this emitter reaches the quad renderer.</summary>
        public bool DrawsAsQuad => PrimitiveKind is
            VfxPrimitiveKind.CameraQuad or
            VfxPrimitiveKind.CameraUnitQuad or
            VfxPrimitiveKind.ArbitraryQuad or
            VfxPrimitiveKind.Ray;

        /// <summary>LTK drawKind.ts: a trail exists only when trailDefinition was authored.</summary>
        public bool DrawsAsTrail => Trail is not null;

        /// <summary>LTK drawKind.ts: a beam ribbon is suppressed when the same primitive names a mesh.</summary>
        public bool DrawsAsBeam => Beam is not null && string.IsNullOrWhiteSpace(MeshPath);

        public bool HasResolvedCustomMaterial =>
            CustomMaterial is not null && CustomMaterial.BindingKind != ModelMaterialBindingKind.Missing;

        /// <summary>
        /// LTK drawKind.ts: a resolved CustomMaterial owns the shading path and suppresses the
        /// legacy distortion pass. A missing linked material falls back to the authored emitter.
        /// </summary>
        public bool DrawsAsDistortion => Distortion is not null && !HasResolvedCustomMaterial;

        /// <summary>
        /// Riot suppresses a beam's ribbon when its primitive also names a mesh. Because a beam
        /// does not enter the mesh draw path either, that authored combination draws nothing.
        /// </summary>
        public bool SuppressesBeamRibbon => Beam is not null && !DrawsAsBeam;

        /// <summary>
        /// Does this emitter reach one of LTK's drawable non-mesh paths?
        /// Draw-kind classification is independent of texture availability: untextured quads
        /// use the shader falloff and untextured ribbons use the neutral white sampler.
        /// </summary>
        public bool IsVisual => !Disabled &&
            !SuppressesBeamRibbon &&
            (DrawsAsQuad || DrawsAsTrail || DrawsAsBeam);
    }

    public sealed record VfxSystemAuthoredFeatures(
        bool HasMaterialOverrides = false,
        bool HasAssetRemapping = false);

    public enum VfxEmissionSurfaceKind
    {
        Mesh,
        Skeleton
    }

    /// <summary>
    /// Mesh or skeleton sampled at particle birth. Asset loading/posing is deliberately
    /// separate from simulation so deterministic playback can consume a sampler later.
    /// </summary>
    public sealed record VfxEmissionSurfaceDefinition(
        VfxEmissionSurfaceKind Kind,
        string MeshPath,
        string SkeletonPath,
        string AnimationPath,
        IReadOnlyList<uint> Submeshes,
        IReadOnlyList<uint> Joints,
        float Scale = 1f,
        int MaxJointWeights = 4,
        bool UseNormal = true);

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
        bool HasPeriodControl = false,
        bool HasLegacySimple = false,
        bool HasTextureMultLayer = false);

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
        Vector4? PaletteSourceMixColor = null,
        VfxCurveF? ScrollU = null,
        VfxCurveF? ScrollV = null,
        int AddressMode = 1);

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
        VfxCurve4? ChannelMixer = null,
        float SliceWidth = 1.5f,
        VfxCurveF? LingerDrive = null,
        float DriveSource = 0f);

    public sealed record VfxLingerDefinition(
        VfxCurve3? Rotation = null,
        VfxCurve3? Scale = null,
        VfxCurve4? Color = null,
        VfxCurve3? Acceleration = null,
        VfxCurve3? Velocity = null,
        VfxCurve3? Drag = null);

    public sealed record VfxBeamDefinition(
        int Mode,
        int TrailMode,
        int Segments,
        VfxCurve3 BirthTilingSize,
        VfxCurve4 ColorByDistance,
        bool ColorBoundToDistance,
        Vector3 SourceOffset,
        Vector3 TargetOffset);

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
        int InheritanceMode,
        IReadOnlyList<string> Bones = null);

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

    /// <summary>Why the engine culls an emitter at preview settings (Very High quality, default palette).</summary>
    public enum VfxCullReason : byte
    {
        None = 0,
        Importance = 1,
        Colorblind = 2
    }

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
        byte Flags = 0,
        VfxCurve3? BirthTranslation = null)
    {
        public Vector3 SampleOffset(Random rng, float t, float? birthChance, out Matrix4x4 rotation)
        {
            rotation = Matrix4x4.Identity;
            bool volume = (Flags & 1) != 0;

            if (Kind == VfxSpawnShapeKind.Point)
                return EmitOffset.Sample(t);

            if (Kind == VfxSpawnShapeKind.Legacy)
            {
                Vector3 offset = EmitOffset.SampleBirth(t, rng, birthChance);
                if (BirthTranslation is { } translation)
                    offset += translation.SampleBirth(t, rng, birthChance);

                int count = Math.Min(RotationAxes.Count, RotationAngles.Count);
                for (int i = 0; i < count; i++)
                {
                    Vector3 axis = RotationAxes[i];
                    double axisLength = Math.Sqrt(
                        (double)axis.X * axis.X +
                        (double)axis.Y * axis.Y +
                        (double)axis.Z * axis.Z);
                    if (axisLength == 0d) continue;
                    float radians = RotationAngles[i].SampleBirth(t, rng, birthChance) * (MathF.PI / 180f);
                    Vector3 normalizedAxis = axis / (float)axisLength;
                    Matrix4x4 step = Matrix4x4.CreateFromAxisAngle(normalizedAxis, radians);
                    // Row-vector composition: first turn applies first.
                    rotation = rotation * step;
                }
                return Vector3.Transform(offset, rotation);
            }

            if (Kind == VfxSpawnShapeKind.Box)
            {
                Vector3 offset = SampleBox(rng, Size, volume);
                if (!volume)
                {
                    Matrix4x4 yTurn = Matrix4x4.CreateRotationY(rng.Next(4) * (MathF.PI * 0.5f));
                    Matrix4x4 zTurn = Matrix4x4.CreateRotationZ(rng.Next(2) * (MathF.PI * 0.5f));
                    rotation = yTurn * zTurn;
                    offset = Vector3.Transform(offset, rotation);
                }
                return offset;
            }

            if (Kind == VfxSpawnShapeKind.Sphere)
                return SampleSphere(rng, Radius, volume, out rotation);

            if (Kind == VfxSpawnShapeKind.Cylinder)
                return SampleCylinder(rng, Radius, Height, volume, out rotation);

            return Vector3.Zero;
        }

        private static float SignedUnit(Random rng) => (float)(rng.NextDouble() * 2d - 1d);

        private static Vector3 SampleBox(Random rng, Vector3 size, bool volume)
        {
            float x = SignedUnit(rng) * size.X;
            float y = SignedUnit(rng) * size.Y;
            float z = (volume ? SignedUnit(rng) : 1f) * size.Z;
            return new Vector3(x, y, z);
        }

        private static Vector3 SampleSphere(Random rng, float radius, bool volume, out Matrix4x4 rotation)
        {
            float r = (volume ? (float)rng.NextDouble() : 1f) * radius;
            float angleY = (float)(rng.NextDouble() * Math.Tau);
            float angleZ = (float)(rng.NextDouble() * Math.Tau);
            // Composes Y then Z, putting the shell poles on +-Z.
            rotation = Matrix4x4.CreateRotationY(angleY) * Matrix4x4.CreateRotationZ(angleZ);
            return Vector3.Transform(new Vector3(r, 0, 0), rotation);
        }

        private static Vector3 SampleCylinder(Random rng, float radius, float height, bool volume, out Matrix4x4 rotation)
        {
            float r = (volume ? SignedUnit(rng) : 1f) * radius;
            float h = (float)rng.NextDouble() * height; // Upwards 0..height (Riot / LTK spawnShape.ts)
            float angleY = (float)(rng.NextDouble() * Math.Tau);
            rotation = Matrix4x4.CreateRotationY(angleY);
            return Vector3.Transform(new Vector3(r, h, 0), rotation);
        }
    }

    /// <summary>One per-component probability table: a particle rolls r in 0..1 at birth and takes the piecewise-linear value at r.</summary>
    public readonly record struct VfxProbTable(
        float[] Times,
        float[] Values,
        float Single = 1f,
        bool IsPresent = true)
    {
        // LTK keeps a probability-table slot valid even when it has no keys: in that case
        // singleValue is the authored multiplier. A default struct represents an absent slot.
        public bool IsEmpty => !IsPresent;

        public float Sample(float r)
        {
            if (!IsPresent) return 1f;
            if (Times is not { Length: > 0 } || Values is not { Length: > 0 }) return Single;
            return VfxCurve.Interp(Times, Values, r, static (a, b, f) => a + (b - a) * f);
        }
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

        public float SampleBirth(float t, Random rng, float? sharedRoll = null)
        {
            float value = Sample(t);
            if (Prob is not { Length: > 0 } || Prob[0].IsEmpty) return value;
            float roll = sharedRoll ?? (float)rng.NextDouble();
            return value * Prob[0].Sample(roll);
        }
        public static readonly VfxCurveF Zero = new(0f, null, null);
        public static VfxCurveF Const(float v) => new(v, null, null);
    }

    public readonly record struct VfxCurve2(
        Vector2 Constant,
        float[] Times,
        Vector2[] Values,
        VfxProbTable[] Prob = null,
        byte ConstantWidth = 2,
        byte[] ValueWidths = null)
    {
        public Vector2 Sample(float t)
            => SampleOver(t, Vector2.Zero);

        public Vector2 SampleOver(float t, Vector2 baseline)
            => VfxCurve.SampleVector2(Constant, ConstantWidth, Times, Values, ValueWidths, t, baseline);

        public Vector2 SampleBirth(Random rng)
            => SampleBirth(0f, rng);

        public Vector2 SampleBirth(float t, Random rng, float? sharedRoll = null)
            => SampleBirthOver(t, rng, Vector2.Zero, sharedRoll);

        public Vector2 SampleBirthOver(float t, Random rng, Vector2 baseline, float? sharedRoll = null)
        {
            int width = VfxCurve.SampleWidth(ConstantWidth, Times, Values?.Length ?? 0, ValueWidths, t, 2);
            Vector2 value = SampleOver(t, baseline);
            if (!VfxCurve.HasProbability(Prob, width)) return value;
            float roll = sharedRoll ?? (float)rng.NextDouble();
            if (width > 0 && Prob.Length > 0 && !Prob[0].IsEmpty) value.X *= Prob[0].Sample(roll);
            if (width > 1 && Prob.Length > 1 && !Prob[1].IsEmpty) value.Y *= Prob[1].Sample(roll);
            return value;
        }

        public static VfxCurve2 Const(Vector2 value) => new(value, null, null);
    }

    /// <summary>A Vector3 value that is either constant or an animation curve over normalised age.</summary>
    public readonly record struct VfxCurve3(
        Vector3 Constant,
        float[] Times,
        Vector3[] Values,
        VfxProbTable[] Prob = null,
        byte ConstantWidth = 3,
        byte[] ValueWidths = null)
    {
        public Vector3 Sample(float t)
            => SampleOver(t, Vector3.Zero);

        public Vector3 SampleOver(float t, Vector3 baseline)
            => VfxCurve.SampleVector3(Constant, ConstantWidth, Times, Values, ValueWidths, t, baseline);

        /// <summary>Birth-time value with every probability-table channel sampled at one shared chance.</summary>
        public Vector3 SampleBirth(Random rng)
            => SampleBirth(0f, rng);

        public Vector3 SampleBirth(float t, Random rng, float? sharedRoll = null)
            => SampleBirthOver(t, rng, Vector3.Zero, sharedRoll);

        public Vector3 SampleBirthOver(float t, Random rng, Vector3 baseline, float? sharedRoll = null)
        {
            int width = VfxCurve.SampleWidth(ConstantWidth, Times, Values?.Length ?? 0, ValueWidths, t, 3);
            Vector3 value = SampleOver(t, baseline);
            if (!VfxCurve.HasProbability(Prob, width)) return value;
            float roll = sharedRoll ?? (float)rng.NextDouble();
            if (width > 0 && Prob.Length > 0 && !Prob[0].IsEmpty) value.X *= Prob[0].Sample(roll);
            if (width > 1 && Prob.Length > 1 && !Prob[1].IsEmpty) value.Y *= Prob[1].Sample(roll);
            if (width > 2 && Prob.Length > 2 && !Prob[2].IsEmpty) value.Z *= Prob[2].Sample(roll);
            return value;
        }

        public bool HasProb => Prob is { Length: > 0 } && Prob.Any(static p => !p.IsEmpty);
        public static VfxCurve3 Const(Vector3 v) => new(v, null, null);
    }

    /// <summary>A Vector4/colour value that is either constant or an animation curve over normalised age.</summary>
    public readonly record struct VfxCurve4(
        Vector4 Constant,
        float[] Times,
        Vector4[] Values,
        VfxProbTable[] Prob = null,
        byte ConstantWidth = 4,
        byte[] ValueWidths = null)
    {
        public Vector4 Sample(float t)
            => SampleOver(t, Vector4.Zero);

        public Vector4 SampleOver(float t, Vector4 baseline)
            => VfxCurve.SampleVector4(Constant, ConstantWidth, Times, Values, ValueWidths, t, baseline);

        public Vector4 SampleBirth(Random rng)
            => SampleBirth(0f, rng);

        public Vector4 SampleBirth(float t, Random rng, float? sharedRoll = null)
            => SampleBirthOver(t, rng, Vector4.Zero, sharedRoll);

        public Vector4 SampleBirthOver(float t, Random rng, Vector4 baseline, float? sharedRoll = null)
        {
            int width = VfxCurve.SampleWidth(ConstantWidth, Times, Values?.Length ?? 0, ValueWidths, t, 4);
            Vector4 value = SampleOver(t, baseline);
            if (!VfxCurve.HasProbability(Prob, width)) return value;
            float roll = sharedRoll ?? (float)rng.NextDouble();
            if (width > 0 && Prob.Length > 0 && !Prob[0].IsEmpty) value.X *= Prob[0].Sample(roll);
            if (width > 1 && Prob.Length > 1 && !Prob[1].IsEmpty) value.Y *= Prob[1].Sample(roll);
            if (width > 2 && Prob.Length > 2 && !Prob[2].IsEmpty) value.Z *= Prob[2].Sample(roll);
            if (width > 3 && Prob.Length > 3 && !Prob[3].IsEmpty) value.W *= Prob[3].Sample(roll);
            return value;
        }

        public static VfxCurve4 Const(Vector4 v) => new(v, null, null);
    }

    internal static class VfxCurve
    {
        /// <summary>Piecewise-linear sample using the last-key-at-or-before lookup.</summary>
        public static T Interp<T>(float[] times, T[] values, float t, Func<T, T, float, T> lerp)
        {
            int n = Math.Min(times.Length, values.Length);
            if (n == 0) return default!;

            int under = LowerKey(times, n, t);
            int loIndex = Math.Max(under, 0);
            T from = values[loIndex];
            int hiIndex = under + 1;
            if ((uint)hiIndex >= (uint)n) return from;

            float span = times[hiIndex] - times[loIndex];
            if (span <= 0f) return from;
            float f = (t - times[loIndex]) / span;
            return lerp(from, values[hiIndex], f);
        }

        public static int SampleWidth(
            byte constantWidth,
            float[] times,
            int valueCount,
            byte[] valueWidths,
            float t,
            int maximumWidth)
        {
            int n = Math.Min(times?.Length ?? 0, valueCount);
            if (n == 0) return Math.Clamp(constantWidth, (byte)0, (byte)maximumWidth);
            int loIndex = Math.Max(LowerKey(times, n, t), 0);
            return WidthAt(valueWidths, loIndex, maximumWidth);
        }

        public static bool HasProbability(VfxProbTable[] probability, int width)
        {
            if (probability is not { Length: > 0 } || width <= 0) return false;
            int count = Math.Min(width, probability.Length);
            for (int i = 0; i < count; i++)
                if (!probability[i].IsEmpty) return true;
            return false;
        }

        public static Vector2 SampleVector2(
            Vector2 constant,
            byte constantWidth,
            float[] times,
            Vector2[] values,
            byte[] valueWidths,
            float t,
            Vector2 baseline)
        {
            int n = Math.Min(times?.Length ?? 0, values?.Length ?? 0);
            if (n == 0)
            {
                int width = Math.Clamp(constantWidth, (byte)0, (byte)2);
                if (width > 0) baseline.X = constant.X;
                if (width > 1) baseline.Y = constant.Y;
                return baseline;
            }

            Span(times, valueWidths, n, t, 2, out int lo, out int hi, out float factor, out int loWidth, out int hiWidth);
            Vector2 from = values[lo];
            Vector2 to = values[hi];
            if (loWidth > 0) baseline.X = Blend(from.X, hiWidth > 0 ? to.X : from.X, factor);
            if (loWidth > 1) baseline.Y = Blend(from.Y, hiWidth > 1 ? to.Y : from.Y, factor);
            return baseline;
        }

        public static Vector3 SampleVector3(
            Vector3 constant,
            byte constantWidth,
            float[] times,
            Vector3[] values,
            byte[] valueWidths,
            float t,
            Vector3 baseline)
        {
            int n = Math.Min(times?.Length ?? 0, values?.Length ?? 0);
            if (n == 0)
            {
                int width = Math.Clamp(constantWidth, (byte)0, (byte)3);
                if (width > 0) baseline.X = constant.X;
                if (width > 1) baseline.Y = constant.Y;
                if (width > 2) baseline.Z = constant.Z;
                return baseline;
            }

            Span(times, valueWidths, n, t, 3, out int lo, out int hi, out float factor, out int loWidth, out int hiWidth);
            Vector3 from = values[lo];
            Vector3 to = values[hi];
            if (loWidth > 0) baseline.X = Blend(from.X, hiWidth > 0 ? to.X : from.X, factor);
            if (loWidth > 1) baseline.Y = Blend(from.Y, hiWidth > 1 ? to.Y : from.Y, factor);
            if (loWidth > 2) baseline.Z = Blend(from.Z, hiWidth > 2 ? to.Z : from.Z, factor);
            return baseline;
        }

        public static Vector4 SampleVector4(
            Vector4 constant,
            byte constantWidth,
            float[] times,
            Vector4[] values,
            byte[] valueWidths,
            float t,
            Vector4 baseline)
        {
            int n = Math.Min(times?.Length ?? 0, values?.Length ?? 0);
            if (n == 0)
            {
                int width = Math.Clamp(constantWidth, (byte)0, (byte)4);
                if (width > 0) baseline.X = constant.X;
                if (width > 1) baseline.Y = constant.Y;
                if (width > 2) baseline.Z = constant.Z;
                if (width > 3) baseline.W = constant.W;
                return baseline;
            }

            Span(times, valueWidths, n, t, 4, out int lo, out int hi, out float factor, out int loWidth, out int hiWidth);
            Vector4 from = values[lo];
            Vector4 to = values[hi];
            if (loWidth > 0) baseline.X = Blend(from.X, hiWidth > 0 ? to.X : from.X, factor);
            if (loWidth > 1) baseline.Y = Blend(from.Y, hiWidth > 1 ? to.Y : from.Y, factor);
            if (loWidth > 2) baseline.Z = Blend(from.Z, hiWidth > 2 ? to.Z : from.Z, factor);
            if (loWidth > 3) baseline.W = Blend(from.W, hiWidth > 3 ? to.W : from.W, factor);
            return baseline;
        }

        private static void Span(
            float[] times,
            byte[] widths,
            int count,
            float t,
            int maximumWidth,
            out int loIndex,
            out int hiIndex,
            out float factor,
            out int loWidth,
            out int hiWidth)
        {
            int under = LowerKey(times, count, t);
            loIndex = Math.Max(under, 0);
            hiIndex = under + 1;
            if ((uint)hiIndex >= (uint)count) hiIndex = loIndex;
            loWidth = WidthAt(widths, loIndex, maximumWidth);
            hiWidth = WidthAt(widths, hiIndex, maximumWidth);
            float span = times[hiIndex] - times[loIndex];
            factor = hiIndex != loIndex && span > 0f ? (t - times[loIndex]) / span : 0f;
        }

        private static int LowerKey(float[] times, int count, float t)
        {
            int under = -1;
            for (int i = 0; i < count; i++)
            {
                if (times[i] > t) break;
                under = i;
            }
            return under;
        }

        private static int WidthAt(byte[] widths, int index, int maximumWidth)
            => widths is { Length: > 0 } && (uint)index < (uint)widths.Length
                ? Math.Clamp(widths[index], (byte)0, (byte)maximumWidth)
                : maximumWidth;

        private static float Blend(float from, float to, float factor)
            => from + (to - from) * factor;
    }

    public sealed record VfxOwnerSceneContext(
        string MeshPath,
        string SkeletonPath,
        float SkinScale,
        uint AnimationGraphPathHash = 0,
        IReadOnlyList<uint> InitialHiddenSubmeshHashes = null);

    /// <summary>
    /// Authored defaults from League's VfxEmitterDefinitionData schema. BIN omits
    /// fields whose value equals these defaults, so parsing must not invent preview values.
    /// </summary>
    public static class VfxAuthoredDefaults
    {
        public const int BlendMode = 0;
        public const byte AlphaReference = 5;
        public const byte ColorLookUpTypeX = 1;
        public const byte ColorLookUpTypeY = 0;
        public const byte MeshRenderFlags = 1;
        public const byte Importance = 1;
        public const byte ColorblindVisibility = 0;
        public const byte RenderPhaseOverride = 7;
        public const byte StencilMode = 0;
        public const byte StencilReference = 0;
    }
}
