using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx
{
    /// <summary>
    /// Maintains deterministic, graphics-independent playback state for one placed effect graph.
    /// </summary>
    public sealed class VfxPlaybackRuntime
    {
        public const int InstanceStride = 35;

        /// <summary>Per-emitter live state + drawable output. One batch renders with one texture/blend.</summary>
        public sealed class EmitterState
        {
            public required VfxEmitterDefinition Def { get; init; }
            public int SourceOrder { get; init; }
            public Vector3 BasePos;                 // world spawn origin (placement + emitterPosition)
            public Vector3 PlacementRight, PlacementUp, PlacementForward;
            public uint Texture;                    // GL handle for this emitter's sprite (0 = not uploaded/skip)
            public int TextureWidth, TextureHeight;
            public uint TextureMult;                // optional Riot multiplier/noise texture stage
            public int TextureMultWidth, TextureMultHeight;
            public uint DistortionTexture;          // normal map for screen-space heat haze/refraction
            public uint ErosionTexture;
            public uint ReflectionTexture;
            public object PendingTexture;
            public object PendingTextureMult;
            public object PendingDistortionTexture;
            public object PendingErosionTexture;
            public object PendingReflectionTexture;
            /// <summary>Pending mesh data (positions, uvs, indices) for deferred GL upload of .scb/.sco mesh primitives.</summary>
            public (float[] Positions, float[] Uvs, uint[] Indices)? PendingMesh;
            internal VfxAnimatedMesh MeshAnimation;
            // CPU copy of particleColorTexture (RGBA8, top-left origin).
            public byte[] ColorGradient;
            public int ColorGradientW, ColorGradientH;
            public float SpriteAspect = 1f;         // legacy scalar quads preserve one atlas cell's width/height
            internal float SpawnAccum;
            internal float Age;                     // emitter age (seconds)
            internal bool BurstDone;                // for isSingleParticle
            internal readonly List<Particle> Particles = new();

            /// <summary>Packed instance data for the renderer: position, 3D scale, color,
            /// rotation/frame, age/velocity, and Euler rotation.</summary>
            public float[] Instances = System.Array.Empty<float>();
            public int InstanceCount;

            // Mesh-primitive emitters (0 = billboard)
            public uint MeshVao, MeshVbo, MeshEbo;
            public int MeshVertexCount, MeshIndexCount;
            public float[] MeshInterleaved;
            public bool UsesExternalAttachedMesh;
            public Matrix4x4 AttachedMeshWorld = Matrix4x4.Identity;
            /// <summary>Emitter age in seconds — drives UV scroll + wing-flap animation time.</summary>
            public float EmitterAge => Age;
        }

        internal struct Particle
        {
            public Vector3 Pos, Vel, BirthAccel, BirthOrbitalVelocity, BirthDrag;
            public float Age, Life;
            public Vector3 BirthSize;
            public Vector4 BirthColor;
            public Vector3 BirthRotation;
            public Vector3 RotationalVelocity;
            public Vector2 BirthUvOffset, BirthUvScrollRate;
            public Vector2 TextureMultBirthUvOffset, TextureMultBirthUvScrollRate;
            public float BirthUvRotateRate, TextureMultBirthUvRotateRate;
            public float Rot, RotVel;
            public float StartFrame, FrameRate, TextureMultFrame;
            public float ColorRandom;   // stable per-particle 0..1 roll for the colour-gradient variant axis
        }

        public IReadOnlyList<EmitterState> Emitters => _emitters;
        private readonly List<EmitterState> _emitters = new();
        private readonly Random _rng;
        private Matrix4x4 _worldTransform = Matrix4x4.Identity;
        private Matrix4x4 _inverseWorldTransform = Matrix4x4.Identity;
        private Vector3 _worldScale = Vector3.One;
        public int LiveParticleCount { get; private set; }
        public object UserTag { get; set; }
        public event Action<VfxPlaybackRuntime, VfxEmitterDefinition, Vector3, bool> ParticleLifecycle;
        private const int MaxParticlesPerEmitter = 4000;

        public void SetTransform(Matrix4x4 worldTransform)
        {
            Matrix4x4 previousInverse = _inverseWorldTransform;
            Matrix4x4 emitterSpaceDelta = previousInverse * worldTransform;
            _worldTransform = worldTransform;
            _worldScale = ExtractScale(worldTransform);
            if (!Matrix4x4.Invert(worldTransform, out _inverseWorldTransform))
                _inverseWorldTransform = Matrix4x4.Identity;

            foreach (var es in _emitters)
            {
                if (es.Def.IsEmitterSpace && es.Particles.Count > 0)
                {
                    for (int particleIndex = 0; particleIndex < es.Particles.Count; particleIndex++)
                    {
                        Particle particle = es.Particles[particleIndex];
                        particle.Pos = Vector3.Transform(particle.Pos, emitterSpaceDelta);
                        particle.Vel = Vector3.TransformNormal(particle.Vel, emitterSpaceDelta);
                        particle.BirthAccel = Vector3.TransformNormal(particle.BirthAccel, emitterSpaceDelta);
                        es.Particles[particleIndex] = particle;
                    }
                }
                es.BasePos = Vector3.Transform(es.Def.EmitterPosition.Sample(EmitterTime(es)), worldTransform);
                es.PlacementRight = SafeNormal(Vector3.TransformNormal(Vector3.UnitX, worldTransform), Vector3.UnitX);
                es.PlacementUp = SafeNormal(Vector3.TransformNormal(Vector3.UnitY, worldTransform), Vector3.UnitY);
                es.PlacementForward = SafeNormal(Vector3.TransformNormal(Vector3.UnitZ, worldTransform), Vector3.UnitZ);
            }
        }

        public VfxPlaybackRuntime(int seed = 1234) => _rng = new Random(seed);

        /// <summary>Configure from a system placed at worldPos. Only visual emitters are simulated.</summary>
        public void SetSystem(VfxSystemDefinition system, Vector3 worldPos, bool includeNonVisual = false)
            => SetSystem(system, Matrix4x4.CreateTranslation(worldPos), includeNonVisual);

        /// <summary>Configure a system with its complete authored placement transform.</summary>
        public void SetSystem(VfxSystemDefinition system, Matrix4x4 worldTransform, bool includeNonVisual = false)
        {
            _emitters.Clear();
            _worldTransform = worldTransform;
            _worldScale = ExtractScale(worldTransform);
            if (!Matrix4x4.Invert(worldTransform, out _inverseWorldTransform))
                _inverseWorldTransform = Matrix4x4.Identity;
            for (int emitterIndex = 0; emitterIndex < system.Emitters.Count; emitterIndex++)
            {
                var e = system.Emitters[emitterIndex];
                if (e.Disabled) continue;
                _emitters.Add(new EmitterState
                {
                    Def = e,
                    SourceOrder = emitterIndex,
                    BasePos = Vector3.Transform(e.EmitterPosition.Sample(0f), worldTransform),
                    PlacementRight = SafeNormal(Vector3.TransformNormal(Vector3.UnitX, worldTransform), Vector3.UnitX),
                    PlacementUp = SafeNormal(Vector3.TransformNormal(Vector3.UnitY, worldTransform), Vector3.UnitY),
                    PlacementForward = SafeNormal(Vector3.TransformNormal(Vector3.UnitZ, worldTransform), Vector3.UnitZ),
                });
            }
            Reset();
        }

        public void ApplyRenderOrder()
        {
            _emitters.Sort((left, right) =>
            {
                int leftPass = (left.Def.RenderState ?? VfxEmitterRenderState.Default).RenderPass;
                int rightPass = (right.Def.RenderState ?? VfxEmitterRenderState.Default).RenderPass;
                int passOrder = leftPass.CompareTo(rightPass);
                return passOrder != 0 ? passOrder : left.SourceOrder.CompareTo(right.SourceOrder);
            });
        }

        private static Vector3 SafeNormal(Vector3 value, Vector3 fallback)
            => value.LengthSquared() > 1e-8f ? Vector3.Normalize(value) : fallback;

        private static Vector3 ExtractScale(Matrix4x4 transform)
            => new(
                MathF.Max(1e-6f, Vector3.TransformNormal(Vector3.UnitX, transform).Length()),
                MathF.Max(1e-6f, Vector3.TransformNormal(Vector3.UnitY, transform).Length()),
                MathF.Max(1e-6f, Vector3.TransformNormal(Vector3.UnitZ, transform).Length()));

        private static float EmitterTime(EmitterState state)
        {
            VfxEmitterDefinition definition = state.Def;
            return definition.EmitterLifetime is > 0f
                ? Math.Clamp((state.Age - definition.TimeBeforeFirstEmission) / definition.EmitterLifetime.Value, 0f, 1f)
                : 0f;
        }

        public void Reset()
        {
            foreach (var s in _emitters) { s.Particles.Clear(); s.SpawnAccum = 0; s.Age = 0; s.BurstDone = false; s.InstanceCount = 0; }
            LiveParticleCount = 0;
        }

        private float _startDelay;
        public void SetStartDelay(float seconds) => _startDelay = MathF.Max(0f, seconds);

        public Vector3 TransformOffset(Vector3 localOffset)
            => Vector3.TransformNormal(localOffset, _worldTransform);

        public bool IsComplete
            => _emitters.Count == 0 || _emitters.TrueForAll(state =>
                (state.BurstDone || (state.Def.EmitterLifetime is { } lifetime && state.Age > state.Def.TimeBeforeFirstEmission + lifetime)) &&
                state.Particles.Count == 0);

        public void Update(float dt)
        {
            if (dt <= 0f) return;
            if (_startDelay > 0f)
            {
                _startDelay -= dt;
                if (_startDelay > 0f) return;
                dt = MathF.Min(dt, -_startDelay);   // only the portion past the trigger point
                _startDelay = 0f;
                if (dt <= 0f) return;
            }
            dt = MathF.Min(dt, 0.1f);   // clamp big frame gaps so bursts don't teleport
            int live = 0;
            foreach (var s in _emitters)
            {
                UpdateEmitter(s, dt);
                BuildInstances(s);
                live += s.InstanceCount;
            }
            LiveParticleCount = live;
        }
        private void UpdateEmitter(EmitterState s, float dt)
        {
            var d = s.Def;
            s.Age += dt;
            float emitterT = EmitterTime(s);
            s.BasePos = Vector3.Transform(d.EmitterPosition.Sample(emitterT), _worldTransform);

            if (d.IsLoop && d.EmitterLifetime is { } loopLife && s.Age > d.TimeBeforeFirstEmission + loopLife)
            {
                s.Age = d.TimeBeforeFirstEmission;
                s.BurstDone = false;
            }

            bool emitting = s.Age >= d.TimeBeforeFirstEmission
                            && (d.EmitterLifetime is not { } life || s.Age <= d.TimeBeforeFirstEmission + life);
            if (emitting)
            {
                if (d.IsSingleParticle)
                {
                    if (!s.BurstDone) { Spawn(s, emitterT); s.BurstDone = true; }
                }
                else
                {
                    float rawRate = MathF.Max(0f, d.Rate.Sample(emitterT));
                    float rate = d.RateIsPeriod ? (rawRate > 0.001f ? 1f / rawRate : 0f) : rawRate;
                    s.SpawnAccum += rate * dt;
                    while (s.SpawnAccum >= 1f && s.Particles.Count < MaxParticlesPerEmitter)
                    {
                        Spawn(s, emitterT);
                        s.SpawnAccum -= 1f;
                    }
                    if (s.Particles.Count >= MaxParticlesPerEmitter) s.SpawnAccum = 0f;
                }
            }

            for (int i = s.Particles.Count - 1; i >= 0; i--)
            {
                var p = s.Particles[i];
                p.Age += dt;
                if (p.Age >= p.Life)
                {
                    ParticleLifecycle?.Invoke(this, d, p.Pos, true);
                    s.Particles.RemoveAt(i);
                    continue;
                }
                float particleT = float.IsPositiveInfinity(p.Life) ? 0f : Math.Clamp(p.Age / p.Life, 0f, 1f);
                var worldAccel = d.Acceleration?.Sample(particleT) ?? Vector3.Zero;
                worldAccel = Vector3.TransformNormal(worldAccel, _worldTransform);
                Vector3 fieldDrag = Vector3.Zero;
                ApplyFields(d.Fields, particleT, p.Age, p.Pos, ref worldAccel, ref fieldDrag);
                p.Vel += (p.BirthAccel + worldAccel) * dt;
                var dragOverLife = d.DragOverLife?.Sample(particleT) ?? Vector3.Zero;
                var drag = Vector3.Max(Vector3.Zero, p.BirthDrag + dragOverLife + fieldDrag);
                p.Vel *= new Vector3(MathF.Exp(-drag.X * dt), MathF.Exp(-drag.Y * dt), MathF.Exp(-drag.Z * dt));
                var authoredVelocity = d.VelocityOverLife?.Sample(particleT) ?? Vector3.Zero;
                authoredVelocity = Vector3.TransformNormal(authoredVelocity, _worldTransform);
                p.Pos += (p.Vel + authoredVelocity) * dt;
                if (p.BirthOrbitalVelocity.LengthSquared() > 1e-8f)
                {
                    var localRelative = Vector3.TransformNormal(p.Pos - s.BasePos, _inverseWorldTransform);
                    var angularStep = p.BirthOrbitalVelocity * dt;
                    var orbit = Quaternion.CreateFromYawPitchRoll(angularStep.Y, angularStep.X, angularStep.Z);
                    p.Pos = s.BasePos + Vector3.TransformNormal(Vector3.Transform(localRelative, orbit), _worldTransform);
                }
                p.Rot += p.RotVel * dt;
                p.BirthRotation += p.RotationalVelocity * dt;
                s.Particles[i] = p;
            }

            if (d.IsSingleParticle && s.BurstDone && s.Particles.Count == 0 && d.EmitterLifetime is null)
                s.BurstDone = false;
        }

        private void Spawn(EmitterState s, float emitterT)
        {
            var d = s.Def;
            float sampledLife = d.ParticleLifetime.SampleBirth(emitterT, _rng);
            float life = sampledLife < 0f ? float.PositiveInfinity : MathF.Max(0.05f, sampledLife);
            var birthScale = d.BirthScale.SampleBirth(emitterT, _rng);
            if (d.IsUniformScale)
                birthScale = new Vector3(birthScale.X);
            var vel = d.BirthVelocity?.SampleBirth(emitterT, _rng) ?? Vector3.Zero;
            var birthAccel = d.BirthAcceleration?.SampleBirth(emitterT, _rng) ?? Vector3.Zero;
            var birthOrbitalVelocity = d.BirthOrbitalVelocity?.SampleBirth(emitterT, _rng) ?? Vector3.Zero;
            var birthDrag = d.BirthDrag?.SampleBirth(emitterT, _rng) ?? Vector3.Zero;
            var birthRotation = d.BirthRotation?.SampleBirth(emitterT, _rng) ?? Vector3.Zero;
            var rotVel = d.BirthRotationalVelocity?.SampleBirth(emitterT, _rng) ?? Vector3.Zero;
            Vector2 birthUvOffset = d.BirthUvOffset?.SampleBirth(emitterT, _rng) ?? Vector2.Zero;
            Vector2 birthUvScrollRate = d.BirthUvScrollRateCurve?.SampleBirth(emitterT, _rng) ?? d.UvScrollRate;
            float birthUvRotateRate = d.BirthUvRotateRate?.SampleBirth(emitterT, _rng) ?? 0f;
            Vector2 textureMultBirthUvOffset = d.TextureMultBirthUvOffset?.SampleBirth(emitterT, _rng) ?? Vector2.Zero;
            Vector2 textureMultBirthUvScrollRate = d.TextureMultBirthUvScrollRate?.SampleBirth(emitterT, _rng)
                ?? d.TextureMultUvScrollRate;
            float textureMultBirthUvRotateRate = d.TextureMultBirthUvRotateRate?.SampleBirth(emitterT, _rng) ?? 0f;

            var localOffset = d.SpawnShape?.SampleOffset(_rng, emitterT) ?? Vector3.Zero;
            var worldOffset = Vector3.Transform(localOffset, _worldTransform) - Vector3.Transform(Vector3.Zero, _worldTransform);
            vel = Vector3.TransformNormal(vel, _worldTransform);
            birthAccel = Vector3.TransformNormal(birthAccel, _worldTransform);
            Vector3 finalBirthSize = birthScale * _worldScale;

            s.Particles.Add(new Particle
            {
                Pos = s.BasePos + worldOffset,
                Vel = vel,
                BirthAccel = birthAccel,
                BirthOrbitalVelocity = birthOrbitalVelocity,
                BirthDrag = birthDrag,
                Age = 0f,
                Life = life,
                BirthSize = finalBirthSize,
                BirthColor = d.BirthColor.SampleBirth(emitterT, _rng),
                BirthRotation = birthRotation * (MathF.PI / 180f),
                RotationalVelocity = rotVel * (MathF.PI / 180f),
                Rot = d.IsMeshPrimitive ? 0f : birthRotation.X * (MathF.PI / 180f),
                RotVel = rotVel.X * (MathF.PI / 180f),
                StartFrame = d.RandomStartFrame && d.NumFrames > 1
                    ? _rng.Next(d.NumFrames)
                    : Math.Clamp(d.StartFrame, 0f, Math.Max(0, d.NumFrames - 1)),
                FrameRate = d.BirthFrameRate?.SampleBirth(emitterT, _rng) ?? d.FrameRate ?? 0f,
                TextureMultFrame = d.TextureMultRandomStartFrame
                    ? _rng.Next(Math.Max(1,
                        (int)MathF.Max(1f, d.TextureMultTexDiv.X) *
                        (int)MathF.Max(1f, d.TextureMultTexDiv.Y)))
                    : 0f,
                ColorRandom = (float)_rng.NextDouble(),
                BirthUvOffset = birthUvOffset,
                BirthUvScrollRate = birthUvScrollRate,
                BirthUvRotateRate = birthUvRotateRate,
                TextureMultBirthUvOffset = textureMultBirthUvOffset,
                TextureMultBirthUvScrollRate = textureMultBirthUvScrollRate,
                TextureMultBirthUvRotateRate = textureMultBirthUvRotateRate
            });
            ParticleLifecycle?.Invoke(this, d, s.Particles[^1].Pos, false);
        }

        private void BuildInstances(EmitterState s)
        {
            var d = s.Def;
            int n = s.Particles.Count;
            bool isTrail = d.PrimitiveKind is VfxPrimitiveKind.CameraTrail or VfxPrimitiveKind.ArbitraryTrail;
            int instanceCount = isTrail ? Math.Max(0, n - 1) : n;
            if (s.Instances.Length < instanceCount * InstanceStride)
                s.Instances = new float[Math.Max(instanceCount * InstanceStride, InstanceStride * 4)];
            var buf = s.Instances;
            int k = 0;
            for (int i = isTrail ? 1 : 0; i < n; i++)
            {
                var p = s.Particles[i];
                float t = float.IsPositiveInfinity(p.Life) ? 0f : Math.Clamp(p.Age / p.Life, 0f, 1f);
                var scaleMul = d.ScaleOverLife?.Sample(t) ?? Vector3.One;
                if (d.IsUniformScale)
                    scaleMul = new Vector3(scaleMul.X);
                var colMul = d.ColorOverLife?.Sample(t) ?? Vector4.One;
                var col = p.BirthColor * colMul;

                if (s.ColorGradient is { } grad && s.ColorGradientW > 0 && s.ColorGradientH > 0)
                {
                    float speed = p.Vel.Length();
                    Vector2 lookupScale = d.ColorLookUpScales == Vector2.Zero
                        ? Vector2.One
                        : d.ColorLookUpScales;
                    float u = LookupCoord(d.ColorLookUpTypeX ?? 1, t, speed, p.ColorRandom) *
                        lookupScale.X + d.ColorLookUpOffsets.X;
                    float v = LookupCoord(d.ColorLookUpTypeY ?? 0, t, speed, p.ColorRandom) *
                        lookupScale.Y + d.ColorLookUpOffsets.Y;
                    col *= SampleGradient(grad, s.ColorGradientW, s.ColorGradientH, u, v);
                }

                float frame = 0f;
                if (d.NumFrames > 1)
                    frame = MathF.Floor(p.FrameRate > 0f
                        ? (p.StartFrame + p.Age * p.FrameRate) % d.NumFrames
                        : (p.StartFrame + t * d.NumFrames) % d.NumFrames);

                Vector3 position = p.Pos;
                float sizeX = p.BirthSize.X * scaleMul.X;
                float sizeY = p.BirthSize.Y * scaleMul.Y;
                Vector3 direction = p.Vel;
                if (d.PrimitiveKind == VfxPrimitiveKind.Ray && d.RayTargetOffset is { } targetOffset)
                    direction = Vector3.TransformNormal(targetOffset, _worldTransform);
                if (isTrail)
                {
                    Vector3 start = s.Particles[i - 1].Pos;
                    Vector3 segment = p.Pos - start;
                    float length = segment.Length();
                    if (length > 1e-5f)
                    {
                        position = (start + p.Pos) * 0.5f;
                        direction = segment;
                        sizeY = length;
                    }
                }
                if (d.UseTextureAspect) sizeX *= s.SpriteAspect;
                buf[k++] = position.X; buf[k++] = position.Y; buf[k++] = position.Z;
                buf[k++] = sizeX;
                buf[k++] = sizeY;
                buf[k++] = col.X; buf[k++] = col.Y; buf[k++] = col.Z; buf[k++] = col.W;
                buf[k++] = p.Rot;
                buf[k++] = frame;
                buf[k++] = p.Age;
                buf[k++] = direction.X; buf[k++] = direction.Y; buf[k++] = direction.Z;
                Vector3 lifeRotation = d.RotationOverLife?.Sample(t) ?? Vector3.Zero;
                lifeRotation *= MathF.PI / 180f;
                if (d.IsDirectionOriented && direction.LengthSquared() > 1e-6f)
                {
                    Vector3 dir = Vector3.Normalize(direction);
                    float yaw = MathF.Atan2(dir.X, dir.Z);
                    float pitch = MathF.Asin(Math.Clamp(-dir.Y, -1f, 1f));
                    buf[k++] = pitch + lifeRotation.X;
                    buf[k++] = yaw + lifeRotation.Y;
                    buf[k++] = p.BirthRotation.Z + lifeRotation.Z;
                }
                else
                {
                    buf[k++] = p.BirthRotation.X + lifeRotation.X;
                    buf[k++] = p.BirthRotation.Y + lifeRotation.Y;
                    buf[k++] = p.BirthRotation.Z + lifeRotation.Z;
                }
                float sizeZ = p.BirthSize.Z * scaleMul.Z;
                buf[k++] = sizeZ;
                Vector2 uvOffset = p.BirthUvOffset + p.BirthUvScrollRate * p.Age
                    + SampleIntegrated(d.ParticleUvScrollRate, t, p.Age, p.Life);
                Vector2 uvScale = d.UvScale?.Sample(t) ?? Vector2.One;
                float uvRotationDegrees = (d.UvRotation?.Sample(t) ?? 0f) + p.BirthUvRotateRate * p.Age
                    + SampleIntegrated(d.ParticleUvRotateRate, t, p.Age, p.Life);
                float uvRotation = uvRotationDegrees * (MathF.PI / 180f);
                buf[k++] = uvOffset.X; buf[k++] = uvOffset.Y;
                buf[k++] = uvScale.X; buf[k++] = uvScale.Y;
                buf[k++] = uvRotation;
                buf[k++] = d.AlphaErosion?.Drive.Sample(t) ?? 0f;
                Vector4 erosionMixer = d.AlphaErosion?.ChannelMixer?.Sample(t) ?? new Vector4(1f, 0f, 0f, 0f);
                buf[k++] = erosionMixer.X; buf[k++] = erosionMixer.Y;
                buf[k++] = erosionMixer.Z; buf[k++] = erosionMixer.W;
                Vector2 textureMultUvOffset = p.TextureMultBirthUvOffset
                    + p.TextureMultBirthUvScrollRate * p.Age
                    + SampleIntegrated(d.TextureMultParticleUvScroll, t, p.Age, p.Life);
                Vector2 textureMultUvScale = d.TextureMultUvScale?.Sample(t) ?? Vector2.One;
                float textureMultUvRotationDegrees = (d.TextureMultUvRotation?.Sample(t) ?? 0f)
                    + p.TextureMultBirthUvRotateRate * p.Age
                    + SampleIntegrated(d.TextureMultParticleUvRotate, t, p.Age, p.Life);
                buf[k++] = textureMultUvOffset.X; buf[k++] = textureMultUvOffset.Y;
                buf[k++] = textureMultUvScale.X; buf[k++] = textureMultUvScale.Y;
                buf[k++] = textureMultUvRotationDegrees * (MathF.PI / 180f);
                buf[k++] = p.TextureMultFrame;
            }
            s.InstanceCount = instanceCount;
        }

        private static Vector2 SampleIntegrated(VfxCurve2? curve, float normalizedAge, float age, float life)
        {
            if (curve is not { } value) return Vector2.Zero;
            if (value.Times is not { Length: > 0 }) return value.Constant * age;
            if (!float.IsFinite(life)) return value.Sample(normalizedAge) * age;

            Vector2 integrated = Vector2.Zero;
            float previousTime = 0f;
            Vector2 previousValue = value.Sample(0f);
            foreach (float authoredTime in value.Times)
            {
                float time = Math.Clamp(authoredTime, 0f, normalizedAge);
                if (time <= previousTime) continue;
                Vector2 currentValue = value.Sample(time);
                integrated += (previousValue + currentValue) * (0.5f * (time - previousTime));
                previousTime = time;
                previousValue = currentValue;
                if (time >= normalizedAge) break;
            }
            if (previousTime < normalizedAge)
            {
                Vector2 currentValue = value.Sample(normalizedAge);
                integrated += (previousValue + currentValue) * (0.5f * (normalizedAge - previousTime));
            }
            return integrated * life;
        }

        private static float SampleIntegrated(VfxCurveF? curve, float normalizedAge, float age, float life)
        {
            if (curve is not { } value) return 0f;
            if (value.Times is not { Length: > 0 }) return value.Constant * age;
            if (!float.IsFinite(life)) return value.Sample(normalizedAge) * age;

            float integrated = 0f;
            float previousTime = 0f;
            float previousValue = value.Sample(0f);
            foreach (float authoredTime in value.Times)
            {
                float time = Math.Clamp(authoredTime, 0f, normalizedAge);
                if (time <= previousTime) continue;
                float currentValue = value.Sample(time);
                integrated += (previousValue + currentValue) * (0.5f * (time - previousTime));
                previousTime = time;
                previousValue = currentValue;
                if (time >= normalizedAge) break;
            }
            if (previousTime < normalizedAge)
            {
                float currentValue = value.Sample(normalizedAge);
                integrated += (previousValue + currentValue) * (0.5f * (normalizedAge - previousTime));
            }
            return integrated * life;
        }

        private void ApplyFields(
            VfxFieldCollectionDefinition fields,
            float particleT,
            float age,
            Vector3 particlePosition,
            ref Vector3 acceleration,
            ref Vector3 drag)
        {
            if (fields is null) return;
            foreach (var field in fields.Acceleration)
            {
                Vector3 value = field.Acceleration.Sample(particleT);
                acceleration += field.LocalSpace ? Vector3.TransformNormal(value, _worldTransform) : value;
            }
            foreach (var field in fields.Attraction)
            {
                Vector3 center = Vector3.Transform(field.Position.Sample(particleT), _worldTransform);
                Vector3 delta = center - particlePosition;
                float radius = field.Radius.Sample(particleT);
                if ((radius <= 0f || delta.LengthSquared() <= radius * radius) && delta.LengthSquared() > 1e-8f)
                    acceleration += Vector3.Normalize(delta) * field.Acceleration.Sample(particleT);
            }
            foreach (var field in fields.Drag)
            {
                Vector3 center = Vector3.Transform(field.Position.Sample(particleT), _worldTransform);
                float radius = field.Radius.Sample(particleT);
                if (radius <= 0f || Vector3.DistanceSquared(center, particlePosition) <= radius * radius)
                    drag += new Vector3(MathF.Max(0f, field.Strength.Sample(particleT)));
            }
            foreach (var field in fields.Orbital)
            {
                Vector3 direction = field.Direction.Sample(particleT);
                if (field.LocalSpace) direction = Vector3.TransformNormal(direction, _worldTransform);
                Vector3 radial = particlePosition - Vector3.Transform(Vector3.Zero, _worldTransform);
                if (direction.LengthSquared() > 1e-8f && radial.LengthSquared() > 1e-8f)
                    acceleration += Vector3.Cross(Vector3.Normalize(direction), Vector3.Normalize(radial)) * direction.Length();
            }
            foreach (var field in fields.Noise)
            {
                Vector3 center = Vector3.Transform(field.Position.Sample(particleT), _worldTransform);
                float radius = field.Radius.Sample(particleT);
                if (radius > 0f && Vector3.DistanceSquared(center, particlePosition) > radius * radius) continue;
                float frequency = field.Frequency.Sample(particleT);
                float amplitude = field.VelocityDelta.Sample(particleT);
                Vector3 wave = new(
                    MathF.Sin((particlePosition.Y + age) * frequency),
                    MathF.Sin((particlePosition.Z + age * 1.37f) * frequency),
                    MathF.Sin((particlePosition.X + age * 1.91f) * frequency));
                acceleration += wave * field.AxisFraction * amplitude;
            }
        }

        private static float LookupCoord(int type, float age, float speed, float random) => type switch
        {
            1 => age,
            2 => Math.Clamp(speed / 400f, 0f, 1f),
            3 => random,
            _ => 0.5f,
        };

        private static Vector4 SampleGradient(byte[] rgba, int w, int h, float u, float v)
        {
            u = Math.Clamp(u, 0f, 1f);
            v = Math.Clamp(v, 0f, 1f);
            float fx = u * (w - 1), fy = v * (h - 1);
            int x0 = (int)fx, y0 = (int)fy;
            int x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
            float tx = fx - x0, tyf = fy - y0;
            Vector4 c00 = Texel(rgba, w, x0, y0), c10 = Texel(rgba, w, x1, y0);
            Vector4 c01 = Texel(rgba, w, x0, y1), c11 = Texel(rgba, w, x1, y1);
            return Vector4.Lerp(Vector4.Lerp(c00, c10, tx), Vector4.Lerp(c01, c11, tx), tyf);
        }

        private static Vector4 Texel(byte[] rgba, int w, int x, int y)
        {
            int i = (y * w + x) * 4;
            return new Vector4(rgba[i] / 255f, rgba[i + 1] / 255f, rgba[i + 2] / 255f, rgba[i + 3] / 255f);
        }
    }
}
