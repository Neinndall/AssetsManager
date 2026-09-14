using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Resources;
using AssetsManager.Services.Viewer.Vfx.Semantics;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Runtime
{
    /// <summary>
    /// Maintains deterministic, graphics-independent playback state for one placed effect graph.
    /// </summary>
    public sealed class VfxPlaybackRuntime
    {
        public const int InstanceStride = 36;

        /// <summary>Per-emitter live state + drawable output. One batch renders with one texture/blend.</summary>
        public sealed class EmitterState
        {
            public required VfxEmitterDefinition Def { get; init; }
            public int SourceOrder { get; init; }
            public bool IsVisible { get; set; } = true;
            public Vector3 BasePos;                 // world spawn origin (placement + emitterPosition)
            public Vector3 PlacementRight, PlacementUp, PlacementForward;
            public uint Texture;                    // GL handle for this emitter's sprite (0 = not uploaded/skip)
            public int TextureWidth, TextureHeight;
            public uint TextureMult;                // optional Riot multiplier/noise texture stage
            public int TextureMultWidth, TextureMultHeight;
            public uint DistortionTexture;          // normal map for screen-space heat haze/refraction
            public uint ErosionTexture;
            public uint ReflectionTexture;
            public uint PaletteTexture;
            public object PendingTexture;
            public object PendingTextureMult;
            public object PendingDistortionTexture;
            public object PendingErosionTexture;
            public object PendingReflectionTexture;
            public object PendingPaletteTexture;
            /// <summary>Pending mesh data for deferred GL upload of .scb/.sco mesh primitives.</summary>
            public (float[] Positions, float[] Uvs, float[] Colors, uint[] Indices)? PendingMesh;
            internal VfxAnimatedMesh MeshAnimation;
            /// <summary>GPU handle for particleColorTexture (0 = unavailable).</summary>
            public uint ColorGradientTexture;
            public object PendingColorGradient;
            public float SpriteAspect = 1f;         // legacy scalar quads preserve one atlas cell's width/height
            internal float SharedRandom;
            internal bool SharedRandomRolled;
            internal float EmittedThrough;
            internal float Age;                     // emitter age (seconds)
            internal bool BurstDone;                // for isSingleParticle
            internal bool InitialEmissionDone;
            internal readonly List<Particle> Particles = new();

            /// <summary>Packed instance data for the renderer: position, color, motion, UV stages,
            /// erosion state, and the authored palette selector.</summary>
            public float[] Instances = System.Array.Empty<float>();
            public int InstanceCount;
            internal float TrailDistance;

            // Mesh-primitive emitters (0 = billboard)
            public uint MeshVao, MeshVbo, MeshEbo;
            public int MeshVertexCount, MeshIndexCount;
            public float[] MeshInterleaved;
            /// <summary>Emitter age in seconds; drives UV scroll and mesh animation time.</summary>
            public float EmitterAge => Age;
        }

        internal struct Particle
        {
            public Vector3 Pos, Vel, BirthAccel, BirthOrbitalVelocity, BirthDrag;
            public Quaternion SpawnRotation;
            public float Age, Life;
            public Vector3 TrailTiling;
            public float TrailBirthDistance;
            public Vector3 BirthSize;
            public Vector4 BirthColor;
            public Vector3 BirthRotation;
            public Vector3 RotationalVelocity, RotationalAcceleration;
            public float RangeRandom;
            public Vector2 BirthUvOffset, BirthUvScrollRate;
            public Vector2 TextureMultBirthUvOffset, TextureMultBirthUvScrollRate;
            public float BirthUvRotateRate, TextureMultBirthUvRotateRate;
            public float Rot, RotVel;
            public float StartFrame, FrameRate, TextureMultFrame;
        }

        public IReadOnlyList<EmitterState> Emitters => _emitters;
        private readonly List<EmitterState> _emitters = new();
        private readonly int _seed;
        private VfxXorShift64Random _rng;
        public float CurrentTime { get; private set; }
        private Matrix4x4 _worldTransform = Matrix4x4.Identity;
        private Matrix4x4 _inverseWorldTransform = Matrix4x4.Identity;
        private Vector3 _worldScale = Vector3.One;
        private bool _isKilled;
        public bool IsStopped { get; set; }
        public int LiveParticleCount { get; private set; }
        public object UserTag { get; set; }
        /// <summary>
        /// Optional attached-object bounds in authored LoL units. The UI can provide this
        /// when a champion scene is available; null keeps the standalone VFX preview neutral.
        /// </summary>
        public Vector3? BoundObjectSize { get; set; }
        public event Action<VfxPlaybackRuntime, VfxEmitterDefinition, Vector3, bool> ParticleLifecycle;
        private const int MaxParticlesPerEmitter = 4000;
        private const float MaximumSimulationStep = 0.1f;

        internal Matrix4x4 WorldTransform => _worldTransform;

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
                Matrix4x4 placement = EmitterTransform(es.Def, worldTransform);
                Vector3 nextBasePos = Vector3.Transform(es.Def.EmitterPosition.Sample(EmitterTime(es)), placement);
                es.TrailDistance += Vector3.Distance(es.BasePos, nextBasePos);
                es.BasePos = nextBasePos;
                es.PlacementRight = SafeNormal(Vector3.TransformNormal(Vector3.UnitX, placement), Vector3.UnitX);
                es.PlacementUp = SafeNormal(Vector3.TransformNormal(Vector3.UnitY, placement), Vector3.UnitY);
                es.PlacementForward = SafeNormal(Vector3.TransformNormal(Vector3.UnitZ, placement), Vector3.UnitZ);
            }
        }

        public VfxPlaybackRuntime(int seed = 1234)
        {
            _seed = seed;
            _rng = new VfxXorShift64Random(seed);
        }

        /// <summary>Configure from a system placed at worldPos.</summary>
        public void SetSystem(VfxSystemDefinition system, Vector3 worldPos)
            => SetSystem(system, Matrix4x4.CreateTranslation(worldPos));

        /// <summary>Configure a system with its complete authored placement transform.</summary>
        public void SetSystem(VfxSystemDefinition system, Matrix4x4 worldTransform)
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
                    BasePos = Vector3.Transform(e.EmitterPosition.Sample(0f), EmitterTransform(e, worldTransform)),
                    PlacementRight = SafeNormal(Vector3.TransformNormal(Vector3.UnitX, worldTransform), Vector3.UnitX),
                    PlacementUp = SafeNormal(Vector3.TransformNormal(Vector3.UnitY, worldTransform), Vector3.UnitY),
                    PlacementForward = SafeNormal(Vector3.TransformNormal(Vector3.UnitZ, worldTransform), Vector3.UnitZ),
                });
            }
            Reset();
            SetTransform(worldTransform);
        }

        public bool SetEmitterVisibility(int sourceOrder, bool isVisible)
        {
            foreach (EmitterState emitter in _emitters)
            {
                if (emitter.SourceOrder != sourceOrder) continue;
                emitter.IsVisible = isVisible;
                return true;
            }

            return false;
        }

        public void ApplyRenderOrder()
        {
            _emitters.Sort((left, right) =>
            {
                VfxEmitterRenderState leftState = left.Def.RenderState ?? VfxEmitterRenderState.Default;
                VfxEmitterRenderState rightState = right.Def.RenderState ?? VfxEmitterRenderState.Default;
                int phaseOrder = leftState.RenderPhase.CompareTo(rightState.RenderPhase);
                if (phaseOrder != 0) return phaseOrder;

                int leftPass = leftState.RenderPass;
                int rightPass = rightState.RenderPass;
                int passOrder = leftPass.CompareTo(rightPass);
                if (passOrder != 0) return passOrder;

                int importanceOrder = left.Def.Importance.CompareTo(right.Def.Importance);
                return importanceOrder != 0
                    ? importanceOrder
                    : left.SourceOrder.CompareTo(right.SourceOrder);
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
                ? Math.Clamp(state.Age / definition.EmitterLifetime.Value, 0f, 1f)
                : 0f;
        }

        private static Matrix4x4 EmitterTransform(VfxEmitterDefinition definition, Matrix4x4 world)
        {
            Vector3 rotation = definition.RotationOverride.GetValueOrDefault() * (MathF.PI / 180f);
            return Matrix4x4.CreateScale(definition.ScaleOverride ?? Vector3.One) *
                Matrix4x4.CreateRotationZ(rotation.Z) * Matrix4x4.CreateRotationX(rotation.X) *
                Matrix4x4.CreateRotationY(rotation.Y) *
                Matrix4x4.CreateTranslation(definition.TranslationOverride.GetValueOrDefault()) * world;
        }

        public void Reset()
        {
            _rng = new VfxXorShift64Random(_seed);
            _isKilled = false;
            IsStopped = false;
            CurrentTime = 0f;
            _startDelay = _configuredStartDelay;
            foreach (var s in _emitters)
            {
                s.Particles.Clear();
                s.TrailDistance = 0f;
                s.BasePos = Vector3.Transform(s.Def.EmitterPosition.Sample(0f), EmitterTransform(s.Def, _worldTransform));
                s.EmittedThrough = s.Def.TimeBeforeFirstEmission;
                s.Age = 0;
                s.BurstDone = false;
                s.InitialEmissionDone = false;
                s.SharedRandomRolled = false;
                s.InstanceCount = 0;
            }
            LiveParticleCount = 0;
        }

        /// <summary>
        /// Seeks deterministically to an exact target time in seconds.
        /// If seeking backward or to zero, resets and fast-forwards deterministically.
        /// </summary>
        public void Seek(float targetTime)
        {
            targetTime = MathF.Max(0f, targetTime);
            if (targetTime < CurrentTime)
            {
                Reset();
            }
            float dt = targetTime - CurrentTime;
            if (dt > 0f)
            {
                Update(dt);
            }
        }

        private float _configuredStartDelay;
        private float _startDelay;
        public void SetStartDelay(float seconds)
        {
            _configuredStartDelay = MathF.Max(0f, seconds);
            _startDelay = _configuredStartDelay;
        }

        public Vector3 TransformOffset(Vector3 localOffset)
            => Vector3.TransformNormal(localOffset, _worldTransform);

        public bool IsComplete
            => _isKilled || _emitters.Count == 0 || _emitters.TrueForAll(state =>
                (IsStopped || state.BurstDone || (!state.Def.IsLoop && state.Def.EmitterLifetime is { } lifetime && state.Age > lifetime)) &&
                state.Particles.Count == 0);

        public void Update(float dt)
        {
            if (_isKilled || dt <= 0f || !float.IsFinite(dt)) return;
            while (dt > 0f)
            {
                float step = MathF.Min(dt, MaximumSimulationStep);
                UpdateStep(step);
                dt -= step;
            }
        }

        public void Kill()
        {
            _isKilled = true;
            foreach (EmitterState emitter in _emitters)
            {
                emitter.Particles.Clear();
                emitter.InstanceCount = 0;
            }
            LiveParticleCount = 0;
        }

        private void UpdateStep(float dt)
        {
            if (_startDelay > 0f)
            {
                _startDelay -= dt;
                if (_startDelay > 0f) return;
                dt = MathF.Min(dt, -_startDelay);   // only the portion past the trigger point
                _startDelay = 0f;
                if (dt <= 0f) return;
            }
            CurrentTime += dt;
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

            if (d.IsLoop && d.EmitterLifetime is { } loopLife && loopLife > 0f && s.Age > loopLife)
            {
                s.Age %= loopLife;
                s.BurstDone = false;
                s.InitialEmissionDone = false;
                s.EmittedThrough = d.TimeBeforeFirstEmission;
            }

            float emitterT = EmitterTime(s);
            Vector3 previousBasePos = s.BasePos;
            Matrix4x4 placement = EmitterTransform(d, _worldTransform);
            s.BasePos = Vector3.Transform(d.EmitterPosition.Sample(emitterT), placement);
            Vector3 emitterDelta = s.BasePos - previousBasePos;
            s.TrailDistance += emitterDelta.Length();
            if (d.IsEmitterSpace && s.Particles.Count > 0)
            {
                if (emitterDelta.LengthSquared() > 1e-12f)
                {
                    for (int particleIndex = 0; particleIndex < s.Particles.Count; particleIndex++)
                    {
                        Particle particle = s.Particles[particleIndex];
                        particle.Pos += emitterDelta;
                        s.Particles[particleIndex] = particle;
                    }
                }
            }

            bool emitting = !IsStopped
                            && s.Age >= d.TimeBeforeFirstEmission
                            && (d.EmitterLifetime is not { } life || s.Age <= life);
            if (emitting && !(d.IsSingleParticle && s.BurstDone))
            {
                float rawRate = MathF.Max(0f, d.Rate.Sample(emitterT));
                float rate = d.RateIsPeriod ? (rawRate > 0.001f ? 1f / rawRate : 0f) : rawRate;
                if (!float.IsFinite(rate)) rate = 0f;
                int count = (int)MathF.Min(MaxParticlesPerEmitter,
                    MathF.Min(MathF.Truncate(MathF.Max(0f, s.Age - s.EmittedThrough) * rate + 0.00001f), MathF.Truncate(rate * 0.33f) + 1f));
                if (!s.InitialEmissionDone)
                    count = d.IsSingleParticle ? Math.Max(1, (int)MathF.Min(rate, ushort.MaxValue)) : Math.Max(1, count);
                if (d.Trail?.MaxAddedPerFrame is > 0) count = Math.Min(count, d.Trail.MaxAddedPerFrame);
                count = Math.Min(count, MaxParticlesPerEmitter - s.Particles.Count);
                for (int born = 0; born < count; born++) Spawn(s, emitterT);
                if (count > 0)
                {
                    s.InitialEmissionDone = true;
                    s.BurstDone = d.IsSingleParticle;
                    s.EmittedThrough = rate > 0f ? s.EmittedThrough + count / rate : s.Age;
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
                if (!d.IsEmitterSpace && d.BindWeight is { } bindWeight && emitterDelta.LengthSquared() > 1e-12f)
                {
                    float bind = Math.Clamp(bindWeight.Sample(particleT), 0f, 1f);
                    if (bind > 0f)
                    {
                        p.Pos += emitterDelta * bind;
                    }
                }
                var worldAccel = d.AccelerationOverLife?.Sample(emitterT) ?? Vector3.Zero;
                worldAccel = Vector3.TransformNormal(worldAccel, placement);
                worldAccel += d.Acceleration?.Sample(emitterT) ?? Vector3.Zero;
                Vector3 fieldDrag = Vector3.Zero;
                ApplyFields(d.Fields, particleT, p.Age, p.Pos, ref worldAccel, ref fieldDrag);
                p.Vel += (p.BirthAccel + worldAccel) * dt;
                var dragOverLife = d.DragOverLife?.Sample(particleT) ?? Vector3.Zero;
                var drag = Vector3.Max(Vector3.Zero, p.BirthDrag + dragOverLife + fieldDrag);
                p.Vel *= new Vector3(MathF.Exp(-drag.X * dt), MathF.Exp(-drag.Y * dt), MathF.Exp(-drag.Z * dt));
                var authoredVelocity = d.VelocityOverLife?.Sample(particleT) ?? Vector3.Zero;
                authoredVelocity = Vector3.Transform(authoredVelocity, p.SpawnRotation);
                authoredVelocity = Vector3.TransformNormal(authoredVelocity, _worldTransform);
                p.Pos += (p.Vel + authoredVelocity) * dt;
                if (p.BirthOrbitalVelocity.LengthSquared() > 1e-8f)
                {
                    var localRelative = Vector3.TransformNormal(p.Pos - s.BasePos, _inverseWorldTransform);
                    var angularStep = p.BirthOrbitalVelocity * dt;
                    var orbit = Quaternion.CreateFromYawPitchRoll(angularStep.Y, angularStep.X, angularStep.Z);
                    p.Pos = s.BasePos + Vector3.TransformNormal(Vector3.Transform(localRelative, orbit), _worldTransform);
                    p.Rot += angularStep.Y;
                }
                if (d.IsRotationEnabled)
                {
                    p.RotationalVelocity += p.RotationalAcceleration * dt;
                    p.RotVel = p.RotationalVelocity.X;
                    p.Rot += p.RotVel * dt;
                    p.BirthRotation += p.RotationalVelocity * dt;
                }
                s.Particles[i] = p;
            }

        }

        private void Spawn(EmitterState s, float emitterT)
        {
            var d = s.Def;
            if (d.ParticlesShareRandomValue && !s.SharedRandomRolled)
            {
                s.SharedRandom = _rng.NextUnitFloat();
                s.SharedRandomRolled = true;
            }
            float roll = _rng.NextUnitFloat();
            float? sharedRoll = d.ParticlesShareRandomValue ? s.SharedRandom : _rng.NextUnitFloat();

            float sampledLife = d.ParticleLifetime.SampleBirth(emitterT, _rng, sharedRoll);
            float life = sampledLife < 0f ? float.PositiveInfinity : MathF.Max(0.05f, sampledLife);
            bool hasRange = d.BirthScale1 is { } || d.Rotation1 is { };
            var rangeRandom = hasRange ? roll : 0f;
            var birthScale = d.BirthScale.SampleBirth(emitterT, _rng, sharedRoll);
            if (d.BirthScale1 is { } birthScale1)
                birthScale = Vector3.Lerp(birthScale, birthScale1.SampleBirth(emitterT, _rng, sharedRoll), rangeRandom);
            // Mesh emitters with the authored uniform flag use X as their scalar;
            // billboard primitives retain their authored width/height vector.
            if (d.IsMeshPrimitive && d.IsUniformScale)
                birthScale = new Vector3(birthScale.X);
            birthScale *= ResolveFlexMultiplier(d.FlexShape?.ScaleBirthScaleByBoundObjectSize);
            var vel = d.BirthVelocity?.SampleBirth(emitterT, _rng, sharedRoll) ?? Vector3.Zero;
            var birthAccel = d.BirthAcceleration?.SampleBirth(emitterT, _rng, sharedRoll) ?? Vector3.Zero;
            var birthOrbitalVelocity = d.BirthOrbitalVelocity?.SampleBirth(emitterT, _rng, sharedRoll) ?? Vector3.Zero;
            var birthDrag = d.BirthDrag?.SampleBirth(emitterT, _rng, sharedRoll) ?? Vector3.Zero;
            var birthRotation = d.BirthRotation?.SampleBirth(emitterT, _rng, sharedRoll) ?? Vector3.Zero;
            var rotVel = d.BirthRotationalVelocity?.SampleBirth(emitterT, _rng, sharedRoll) ?? Vector3.Zero;
            var rotationalAcceleration = d.BirthRotationalAcceleration?.SampleBirth(emitterT, _rng, sharedRoll) ?? Vector3.Zero;
            Vector2 birthUvOffset = d.BirthUvOffset?.SampleBirth(emitterT, _rng, sharedRoll) ?? Vector2.Zero;
            Vector2 birthUvScrollRate = d.BirthUvScrollRateCurve?.SampleBirth(emitterT, _rng, sharedRoll) ?? d.UvScrollRate;
            float birthUvRotateRate = d.BirthUvRotateRate?.SampleBirth(emitterT, _rng, sharedRoll) ?? 0f;
            Vector2 textureMultBirthUvOffset = d.TextureMultBirthUvOffset?.SampleBirth(emitterT, _rng, sharedRoll) ?? Vector2.Zero;
            Vector2 textureMultBirthUvScrollRate = d.TextureMultBirthUvScrollRate?.SampleBirth(emitterT, _rng, sharedRoll)
                ?? d.TextureMultUvScrollRate;
            float textureMultBirthUvRotateRate = d.TextureMultBirthUvRotateRate?.SampleBirth(emitterT, _rng, sharedRoll) ?? 0f;

            Matrix4x4 spawnRotation = Matrix4x4.Identity;
            var localOffset = d.SpawnShape is { } shape
                ? shape.SampleOffset(_rng, emitterT, out spawnRotation)
                : Vector3.Zero;
            localOffset *= ResolveFlexMultiplier(d.FlexShape?.ScaleEmitOffsetByBoundObjectSize);
            Matrix4x4 placement = EmitterTransform(d, _worldTransform);
            var worldOffset = Vector3.TransformNormal(localOffset, placement);
            vel = Vector3.TransformNormal(vel, spawnRotation);
            birthAccel = Vector3.TransformNormal(birthAccel, spawnRotation);
            birthOrbitalVelocity = Vector3.TransformNormal(birthOrbitalVelocity, spawnRotation);
            vel = Vector3.TransformNormal(vel, placement);
            birthAccel = Vector3.TransformNormal(birthAccel, placement);
            Vector3 finalBirthSize = birthScale * ExtractScale(placement);

            s.Particles.Add(new Particle
            {
                Pos = s.BasePos + worldOffset,
                Vel = vel,
                BirthAccel = birthAccel,
                BirthOrbitalVelocity = birthOrbitalVelocity,
                BirthDrag = birthDrag,
                SpawnRotation = Quaternion.CreateFromRotationMatrix(spawnRotation),
                Age = 0f,
                Life = life,
                TrailTiling = d.Trail?.BirthTilingSize.SampleBirth(emitterT, _rng, sharedRoll) ?? Vector3.Zero,
                TrailBirthDistance = s.TrailDistance,
                BirthSize = finalBirthSize,
                BirthColor = VfxColorSemantics.ResolveBirth(d.BirthColor, emitterT, _rng, sharedRoll),
                BirthRotation = birthRotation * (MathF.PI / 180f),
                RotationalVelocity = d.IsRotationEnabled ? rotVel * (MathF.PI / 180f) : Vector3.Zero,
                RotationalAcceleration = d.IsRotationEnabled ? rotationalAcceleration * (MathF.PI / 180f) : Vector3.Zero,
                Rot = d.IsMeshPrimitive ? 0f : birthRotation.X * (MathF.PI / 180f),
                RotVel = d.IsRotationEnabled ? rotVel.X * (MathF.PI / 180f) : 0f,
                RangeRandom = rangeRandom,
                StartFrame = d.RandomStartFrame && d.NumFrames > 1
                    ? _rng.Next(d.NumFrames)
                    : Math.Clamp(d.StartFrame, 0f, Math.Max(0, d.NumFrames - 1)),
                FrameRate = d.BirthFrameRate?.SampleBirth(emitterT, _rng, sharedRoll) ?? d.FrameRate ?? 0f,
                TextureMultFrame = d.TextureMultRandomStartFrame
                    ? _rng.Next(Math.Max(1,
                        (int)MathF.Max(1f, d.TextureMultTexDiv.X) *
                        (int)MathF.Max(1f, d.TextureMultTexDiv.Y)))
                    : 0f,
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
            int instanceCount = n;
            if (s.Instances.Length < instanceCount * InstanceStride)
                s.Instances = new float[Math.Max(instanceCount * InstanceStride, InstanceStride * 4)];
            var buf = s.Instances;
            int k = 0;
            for (int i = 0; i < n; i++)
            {
                var p = s.Particles[i];
                float t = float.IsPositiveInfinity(p.Life) ? 0f : Math.Clamp(p.Age / p.Life, 0f, 1f);
                var scaleMul = d.ScaleOverLife?.Sample(t) ?? Vector3.One;
                if (d.IsUniformScale)
                    scaleMul = new Vector3(scaleMul.X);
                var col = VfxColorSemantics.ResolveParticle(p.BirthColor, d.ColorOverLife, t);
                col = VfxColorSemantics.PremultiplyForAddOrSubtract(col, d.BlendMode, d.Distortion != null);

                float frame = 0f;
                if (d.NumFrames > 1)
                    frame = MathF.Floor(p.FrameRate > 0f
                        ? (p.StartFrame + p.Age * p.FrameRate) % d.NumFrames
                        : (p.StartFrame + t * d.NumFrames) % d.NumFrames);

                Vector3 position = p.Pos;
                float sizeX = p.BirthSize.X * scaleMul.X;
                float sizeY = p.BirthSize.Y * scaleMul.Y;
                if (d.PrimitiveKind == VfxPrimitiveKind.ArbitraryQuad)
                {
                    sizeX *= 2f;
                    sizeY *= 2f;
                    if (d.IsGroundLayer && d.IsUniformScale)
                        sizeY = sizeX;
                }
                Vector3 direction = p.Vel;
                if (p.BirthOrbitalVelocity.LengthSquared() > 1e-8f)
                    direction += Vector3.Cross(p.BirthOrbitalVelocity, p.Pos - s.BasePos);
                if (d.PrimitiveKind == VfxPrimitiveKind.Ray && d.RayTargetOffset is { } targetOffset)
                    direction = Vector3.TransformNormal(targetOffset, _worldTransform);

                if (d.UseTextureAspect) sizeX *= s.SpriteAspect;
                buf[k++] = position.X; buf[k++] = position.Y; buf[k++] = position.Z;
                buf[k++] = sizeX;
                buf[k++] = sizeY;
                buf[k++] = col.X; buf[k++] = col.Y; buf[k++] = col.Z; buf[k++] = col.W;
                buf[k++] = p.Rot;
                buf[k++] = frame;
                buf[k++] = t;
                buf[k++] = direction.X; buf[k++] = direction.Y; buf[k++] = direction.Z;
                Vector3 lifeRotation = Vector3.Zero;
                if (d.IsRotationEnabled && d.RotationOverLife is { } rotationCurve)
                {
                    lifeRotation = rotationCurve.Sample(t);
                    if (d.Rotation1 is { } rotationMax)
                        lifeRotation = Vector3.Lerp(lifeRotation, rotationMax.Sample(t), p.RangeRandom);
                    // rotation0 is an authored angular velocity, not an absolute angle.
                    lifeRotation *= p.Age * (MathF.PI / 180f);
                }
                bool authoredPlane = d.IsArbitraryQuad || d.IsLocalOrientation || d.ParticleIsLocalOrientation || d.PrimitiveKind is
                    VfxPrimitiveKind.ArbitraryTrail or VfxPrimitiveKind.PlanarProjection;
                if (d.IsDirectionOriented && !authoredPlane && direction.LengthSquared() > 1e-6f)
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
                buf[k++] = d.PaletteDefinition?.PaletteSelector.Sample(t).X ?? 0f;
            }
            s.InstanceCount = k / InstanceStride;
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

        private float ResolveFlexMultiplier(float? coefficient)
        {
            if (coefficient is not { } value || BoundObjectSize is not { } bounds) return 1f;
            float extent = MathF.Max(MathF.Abs(bounds.X), MathF.Max(MathF.Abs(bounds.Y), MathF.Abs(bounds.Z)));
            return MathF.Max(0f, 1f + value * extent);
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

    }
}
