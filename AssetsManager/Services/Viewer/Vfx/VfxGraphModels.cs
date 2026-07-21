using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace AssetsManager.Services.Viewer.Vfx
{
    internal sealed record VfxBinDocument(
        IReadOnlyDictionary<uint, VfxSystemDefinition> Systems,
        IReadOnlyDictionary<string, VfxAnimationClip> AnimationClips,
        IReadOnlyDictionary<uint, uint> ResourceMap,
        IReadOnlyList<string> Dependencies);

    /// <summary>
    /// Domain graph for a League VFX system and its emitter nodes.
    /// </summary>
    public sealed record VfxSystemDefinition(
        uint PathHash,
        string Name,
        string ParticlePath,
        IReadOnlyList<VfxEmitterDefinition> Emitters,
        string PersistentSoundEventName = null,
        string OnCreateSoundEventName = null,
        float VisibilityRadius = 0f);

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
        int BlendMode,                  // 1 = additive (most VFX), else alpha
        VfxCurve3 BirthScale,           // ABSOLUTE size at birth (birthScale0), world units
        VfxCurve3? ScaleOverLife,       // scale0: normalised MULTIPLIER over age → effective size = BirthScale * this
        VfxCurve4 BirthColor,           // rgba at birth
        VfxCurve4? ColorOverLife,       // color: MULTIPLIER over age → effective colour = BirthColor * this (alpha usually fades)
        VfxCurve3? BirthVelocity,       // initial velocity
        VfxCurve3? Acceleration,        // worldAcceleration (gravity/wind)
        VfxCurve3? BirthRotationalVelocity,
        Vector3 EmitterPosition,        // offset of this emitter within the system
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
        VfxEmitterRenderState RenderState = null)
    {
        /// <summary>Does this emitter produce anything drawable (has a texture and isn't disabled)?</summary>
        public bool IsVisual => !Disabled && (!string.IsNullOrEmpty(TexturePath) ||
            !string.IsNullOrEmpty(TextureMultPath) || !string.IsNullOrEmpty(MeshPath) ||
            Distortion is { NormalMapTexturePath.Length: > 0 });
    }

    public sealed record VfxEmitterRenderState(
        int RenderPass,
        byte AlphaReference,
        int TextureAddressMode,
        bool ClampUvScroll,
        bool FlipU,
        bool FlipV,
        bool DisableBackfaceCull)
    {
        public static readonly VfxEmitterRenderState Default = new(0, 5, 0, false, false, false, false);
        public float AlphaCutoff => AlphaReference / 255f;
    }

    /// <summary>Riot's screen-space particle distortion stage (heat haze/refraction).</summary>
    public sealed record VfxDistortionDefinition(float Strength, int Mode, string NormalMapTexturePath);

    /// <summary>
    /// Authored particle spawn volume. EmitOffset is randomized by its ValueVector3
    /// probability tables, then the authored axis/angle rotations are applied in order.
    /// </summary>
    public sealed record VfxSpawnShape(
        VfxCurve3 EmitOffset,
        IReadOnlyList<Vector3> RotationAxes,
        IReadOnlyList<VfxCurveF> RotationAngles)
    {
        public Vector3 SampleOffset(Random rng)
        {
            var offset = EmitOffset.SampleBirth(rng);
            int count = Math.Min(RotationAxes.Count, RotationAngles.Count);
            for (int i = 0; i < count; i++)
            {
                var axis = RotationAxes[i];
                if (axis.LengthSquared() <= 1e-8f) continue;
                float radians = RotationAngles[i].SampleBirth(rng) * (MathF.PI / 180f);
                offset = Vector3.Transform(offset,
                    Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), radians));
            }
            return offset;
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
        {
            float value = Sample(0f);
            return Prob is { Length: > 0 } && !Prob[0].IsEmpty
                ? value * Prob[0].Sample((float)rng.NextDouble())
                : value;
        }
        public static readonly VfxCurveF Zero = new(0f, null, null);
        public static VfxCurveF Const(float v) => new(v, null, null);
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
        {
            var v = Sample(0f);
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
        {
            var v = Sample(0f);
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
