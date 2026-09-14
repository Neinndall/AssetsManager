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
        public const int InstanceStride = 45;

        /// <summary>Per-emitter live state + drawable output. One batch renders with one texture/blend.</summary>
        public sealed class EmitterState
        {
            public required VfxEmitterDefinition Def { get; init; }
            public int SourceOrder { get; init; }
            public bool IsVisible { get; set; } = true;
            public Vector3 BasePos;                 // world spawn origin (placement + emitterPosition)
            public Vector3 SystemOrigin, SystemTarget;
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
            internal float FinishedAt = -1f;
            internal bool BurstDone;                // for isSingleParticle
            internal bool InitialEmissionDone;
            internal readonly List<Particle> Particles = new();

            /// <summary>Packed instance data for the renderer: position, color, motion, UV stages,
            /// erosion state, and the authored palette selector.</summary>
            public float[] Instances = System.Array.Empty<float>();
            public int InstanceCount;
            internal float TrailDistance;
            internal float[] NoiseLast = Array.Empty<float>();
            internal int[] NoiseFired = Array.Empty<int>();

            // Mesh-primitive emitters (0 = billboard)
            public uint MeshVao, MeshVbo, MeshEbo;
            public int MeshVertexCount, MeshIndexCount;
            public float[] MeshInterleaved;
            /// <summary>Emitter age in seconds; drives UV scroll and mesh animation time.</summary>
            public float EmitterAge => Age;
        }

        internal struct Particle
        {
            public Vector3 Pos, Vel, Travel, BirthAccel, BirthOrbitalVelocity, BirthDrag;
            public Vector3 AnalyticTerminal, AnalyticOffset;
            public Matrix4x4 BirthFrame;
            public Quaternion SpawnRotation;
            public float Age, Life;
            public uint Serial;
            public Vector3 TrailTiling;
            public float TrailBirthDistance;
            public Vector3 BirthSize;
            public Vector4 BirthColor;
            public Vector3 BirthRotation;
            public Vector3 RotationalVelocity, RotationalAcceleration;
            public float RangeRandom;
            public Vector2 BirthUvOffset, BirthUvScrollRate;
            public Vector2 IntegratedUvOffset;
            public Vector2 TextureMultBirthUvOffset, TextureMultBirthUvScrollRate;
            public Vector2 IntegratedTextureMultUvOffset;
            public float BirthUvRotateRate, IntegratedUvRotation;
            public float TextureMultBirthUvRotateRate, IntegratedTextureMultUvRotation;
            public float LingerFrom;
            public float Rot, RotVel;
            public float StartFrame, FrameRate, TextureMultFrame;
        }

        public IReadOnlyList<EmitterState> Emitters => _emitters;
        private readonly List<EmitterState> _emitters = new();
        private readonly int _seed;
        private VfxXorShift64Random _rng;
        private uint _particleSerial;
        public float CurrentTime { get; private set; }
        private Matrix4x4 _worldTransform = Matrix4x4.Identity;
        private Matrix4x4 _orientationRootTransform = Matrix4x4.Identity;
        private Matrix4x4 _inverseWorldTransform = Matrix4x4.Identity;
        private Vector3 _pendingOriginDelta;
        private VfxDragMotion _dragMotion;
        private float _buildUpTime;
        private bool _needsBuildUp;
        private bool _isKilled;
        public bool IsStopped { get; set; }
        public int LiveParticleCount { get; private set; }
        public object UserTag { get; set; }
        /// <summary>
        /// Optional attached-object bounds in authored LoL units. The UI can provide this
        /// when a champion scene is available; null keeps the standalone VFX preview neutral.
        /// </summary>
        public Vector3? BoundObjectSize { get; set; }
        public readonly record struct ParticleLifecycleInfo(
            Vector3 Position,
            Matrix4x4 Basis,
            Matrix4x4 Frame,
            uint Serial,
            int SourceOrder,
            float ParticleTime,
            float EmitterPhase,
            bool Died);

        public event Action<VfxPlaybackRuntime, VfxEmitterDefinition, ParticleLifecycleInfo> ParticleLifecycle;
        private const int MaxParticlesPerEmitter = 4000;
        private const float MaximumSimulationStep = 0.1f;

        internal Matrix4x4 WorldTransform => _worldTransform;

        public void SetTransform(Matrix4x4 worldTransform)
            => SetTransform(worldTransform, worldTransform);

        internal void SetTransform(Matrix4x4 worldTransform, Matrix4x4 orientationRootTransform)
        {
            Matrix4x4 previousInverse = _inverseWorldTransform;
            Matrix4x4 emitterSpaceDelta = previousInverse * worldTransform;
            Vector3 previousOrigin = new(_worldTransform.M41, _worldTransform.M42, _worldTransform.M43);
            Vector3 nextOrigin = new(worldTransform.M41, worldTransform.M42, worldTransform.M43);
            _pendingOriginDelta += nextOrigin - previousOrigin;
            _worldTransform = worldTransform;
            _orientationRootTransform = orientationRootTransform;
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
                Matrix4x4 placement = EmitterPlacement(es.Def);
                Vector3 nextBasePos = Vector3.Transform(es.Def.EmitterPosition.Sample(EmitterTime(es)), placement);
                es.TrailDistance += Vector3.Distance(es.BasePos, nextBasePos);
                es.BasePos = nextBasePos;
                es.SystemOrigin = nextOrigin;
                es.SystemTarget = Vector3.Transform(new Vector3(600f, 0f, 0f), worldTransform);
                es.PlacementRight = SafeNormal(Vector3.TransformNormal(Vector3.UnitX, placement), Vector3.UnitX);
                es.PlacementUp = SafeNormal(Vector3.TransformNormal(Vector3.UnitY, placement), Vector3.UnitY);
                es.PlacementForward = SafeNormal(Vector3.TransformNormal(Vector3.UnitZ, placement), Vector3.UnitZ);
            }
        }

        public void SetTarget(Vector3 worldTarget)
        {
            foreach (EmitterState emitter in _emitters)
                emitter.SystemTarget = worldTarget;
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
            _pendingOriginDelta = Vector3.Zero;
            _dragMotion = system.DragMotion;
            _buildUpTime = MathF.Max(0f, system.BuildUpTime);
            _worldTransform = worldTransform;
            _orientationRootTransform = worldTransform;
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

        private static Matrix4x4 OrientationOnly(Matrix4x4 transform)
        {
            Vector3 right = SafeNormal(Vector3.TransformNormal(Vector3.UnitX, transform), Vector3.UnitX);
            Vector3 up = SafeNormal(Vector3.TransformNormal(Vector3.UnitY, transform), Vector3.UnitY);
            Vector3 forward = SafeNormal(Vector3.TransformNormal(Vector3.UnitZ, transform), Vector3.UnitZ);
            return new Matrix4x4(
                right.X, right.Y, right.Z, 0f,
                up.X, up.Y, up.Z, 0f,
                forward.X, forward.Y, forward.Z, 0f,
                0f, 0f, 0f, 1f);
        }

        private static Matrix4x4 ParticleBasis(
            in Particle particle,
            EmitterState emitter,
            Vector3 direction,
            Quaternion orbitalTurn,
            float legacyRoll)
        {
            if (emitter.Def.IsDirectionOriented &&
                emitter.Def.PrimitiveKind != VfxPrimitiveKind.Ray &&
                direction.LengthSquared() > 1e-8f)
            {
                Vector3 up = Vector3.Normalize(direction);
                Vector3 axis = MathF.Abs(up.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
                Vector3 right = SafeNormal(Vector3.Cross(axis, up), Vector3.UnitX);
                Vector3 forward = SafeNormal(Vector3.Cross(right, up), Vector3.UnitZ);
                return new Matrix4x4(
                    right.X, right.Y, right.Z, 0f,
                    up.X, up.Y, up.Z, 0f,
                    forward.X, forward.Y, forward.Z, 0f,
                    0f, 0f, 0f, 1f);
            }

            Vector3 rotation = particle.BirthRotation;
            Matrix4x4 standing =
                Matrix4x4.CreateRotationZ(rotation.Z + legacyRoll) *
                Matrix4x4.CreateRotationX(rotation.X) *
                Matrix4x4.CreateRotationY(rotation.Y);

            Matrix4x4 frame;
            if (emitter.Def.ParticleIsLocalOrientation)
            {
                frame = new Matrix4x4(
                    emitter.PlacementRight.X, emitter.PlacementRight.Y, emitter.PlacementRight.Z, 0f,
                    emitter.PlacementUp.X, emitter.PlacementUp.Y, emitter.PlacementUp.Z, 0f,
                    emitter.PlacementForward.X, emitter.PlacementForward.Y, emitter.PlacementForward.Z, 0f,
                    0f, 0f, 0f, 1f);
            }
            else
            {
                frame = OrientationOnly(particle.BirthFrame);
            }

            Matrix4x4 basis = standing * frame;
            if (orbitalTurn != Quaternion.Identity)
                basis *= Matrix4x4.CreateFromQuaternion(orbitalTurn);
            return OrientationOnly(basis);
        }

        private ParticleLifecycleInfo LifecycleInfo(EmitterState state, in Particle particle, bool died)
        {
            float particleT = float.IsPositiveInfinity(particle.Life)
                ? 0f
                : Math.Clamp(particle.Age / particle.Life, 0f, 1f);
            float emitterT = EmitterTime(state);
            Vector3 position = particle.Pos;
            Vector3 orbitalAngles = particle.BirthOrbitalVelocity * particle.Age;
            Quaternion orbitalTurn = Quaternion.Identity;
            if (orbitalAngles.LengthSquared() > 1e-8f)
            {
                Vector3 origin = state.SystemOrigin;
                orbitalTurn = Quaternion.CreateFromYawPitchRoll(orbitalAngles.Y, orbitalAngles.X, orbitalAngles.Z);
                position = origin + Vector3.Transform(position - origin, orbitalTurn);
            }
            if (state.Def.Acceleration is { } worldAcceleration && float.IsFinite(particle.Life))
            {
                float reached = particleT * particle.Life * particle.Life;
                position += worldAcceleration.Sample(emitterT) * reached;
            }

            float legacyRoll = state.Def.LegacyRotation?.Sample(particleT) * (MathF.PI / 180f) ?? 0f;
            Matrix4x4 basis = ParticleBasis(particle, state, particle.Travel, orbitalTurn, legacyRoll);
            Matrix4x4 frame = state.Def.ParticleIsLocalOrientation
                ? new Matrix4x4(
                    state.PlacementRight.X, state.PlacementRight.Y, state.PlacementRight.Z, 0f,
                    state.PlacementUp.X, state.PlacementUp.Y, state.PlacementUp.Z, 0f,
                    state.PlacementForward.X, state.PlacementForward.Y, state.PlacementForward.Z, 0f,
                    0f, 0f, 0f, 1f)
                : OrientationOnly(particle.BirthFrame);
            if (orbitalTurn != Quaternion.Identity)
                frame = OrientationOnly(frame * Matrix4x4.CreateFromQuaternion(orbitalTurn));

            float particleTime = died && float.IsFinite(particle.Life) ? particle.Life : particle.Age;
            return new ParticleLifecycleInfo(position, basis, frame, particle.Serial, state.SourceOrder, particleTime, emitterT, died);
        }

        private static float EmitterTime(EmitterState state)
        {
            VfxEmitterDefinition definition = state.Def;
            return definition.EmitterLifetime is > 0f
                ? Math.Clamp(state.Age / definition.EmitterLifetime.Value, 0f, 1f)
                : 0f;
        }

        private static bool IsLegacySimple(VfxEmitterDefinition definition)
            => definition.LegacyBirthScale is not null;

        private static float LingerSeconds(VfxEmitterDefinition definition)
        {
            float lifetime = IsLegacySimple(definition) ? 0f : MathF.Max(0f, definition.ParticleLifetime.Constant);
            return MathF.Min(lifetime + 10f, MathF.Max(0f, definition.ParticleLinger));
        }

        private static float StopWaitSeconds(VfxEmitterDefinition definition)
        {
            float lifetime = IsLegacySimple(definition)
                ? 0f
                : definition.EmitterLifetime ?? float.PositiveInfinity;
            return MathF.Min(lifetime + 10f, MathF.Max(0f, definition.EmitterLinger));
        }

        private static float LingerProgress(EmitterState state)
        {
            if (state.FinishedAt < 0f) return 0f;
            float seconds = LingerSeconds(state.Def);
            return seconds > 0f ? Math.Clamp((state.Age - state.FinishedAt) / seconds, 0f, 1f) : 1f;
        }

        private Matrix4x4 EmitterPlacement(VfxEmitterDefinition definition)
            => EmitterTransform(definition, definition.IsLocalOrientation ? _worldTransform : _orientationRootTransform);

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
            _particleSerial = 0;
            _isKilled = false;
            IsStopped = false;
            CurrentTime = 0f;
            _pendingOriginDelta = Vector3.Zero;
            _needsBuildUp = _buildUpTime > 0f;
            _startDelay = _configuredStartDelay;
            foreach (var s in _emitters)
            {
                s.Particles.Clear();
                s.TrailDistance = 0f;
                s.NoiseLast = Array.Empty<float>();
                s.NoiseFired = Array.Empty<int>();
                s.BasePos = Vector3.Transform(s.Def.EmitterPosition.Sample(0f), EmitterPlacement(s.Def));
                s.EmittedThrough = s.Def.TimeBeforeFirstEmission;
                s.Age = 0;
                s.FinishedAt = -1f;
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

        public bool IsComplete
            => _isKilled || _emitters.Count == 0 || _emitters.TrueForAll(state =>
                (IsStopped || state.BurstDone || (!state.Def.IsLoop && state.Def.EmitterLifetime is { } lifetime && state.Age > lifetime)) &&
                state.Particles.Count == 0);

        /// <summary>
        /// Pre-simulates the authored build-up at 60 Hz while the rig stays at its initial
        /// placement. The visible timeline remains at zero, matching the League/LTK contract.
        /// </summary>
        public void WarmUp()
        {
            if (!_needsBuildUp || _isKilled) return;
            _needsBuildUp = false;
            int steps = (int)MathF.Round(_buildUpTime * 60f);
            const float dt = 1f / 60f;
            for (int step = 0; step < steps; step++)
            {
                int live = 0;
                foreach (EmitterState emitter in _emitters)
                {
                    UpdateEmitter(emitter, dt, Vector3.Zero);
                    BuildInstances(emitter);
                    live += emitter.InstanceCount;
                }
                LiveParticleCount = live;
            }
        }

        public void Update(float dt)
        {
            if (_isKilled || dt <= 0f || !float.IsFinite(dt)) return;
            WarmUp();
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
            Vector3 systemDelta = _pendingOriginDelta;
            _pendingOriginDelta = Vector3.Zero;
            int live = 0;
            foreach (var s in _emitters)
            {
                UpdateEmitter(s, dt, systemDelta);
                BuildInstances(s);
                live += s.InstanceCount;
            }
            LiveParticleCount = live;
        }
        private void SettleEmitter(EmitterState state, float dt)
        {
            VfxEmitterDefinition definition = state.Def;
            bool finished = IsStopped
                ? state.Age > StopWaitSeconds(definition)
                : definition.ParticleLingerType == 2 &&
                  definition.EmitterLifetime is { } lifetime &&
                  state.Age > lifetime;
            if (!finished || state.FinishedAt >= 0f) return;

            state.FinishedAt = state.Age;
            float seconds = LingerSeconds(definition);
            bool capped = definition.ParticleLingerType == 0;
            for (int index = 0; index < state.Particles.Count; index++)
            {
                Particle particle = state.Particles[index];
                particle.LingerFrom = particle.Age + dt;
                particle.Life = capped
                    ? MathF.Min(particle.Life, seconds)
                    : particle.LingerFrom + seconds;
                state.Particles[index] = particle;
            }
        }

        private void UpdateEmitter(EmitterState s, float dt, Vector3 systemDelta)
        {
            var d = s.Def;
            s.Age += dt;

            if (d.IsLoop && d.EmitterLifetime is { } loopLife && loopLife > 0f && s.Age > loopLife)
            {
                s.Age %= loopLife;
                s.BurstDone = false;
                s.InitialEmissionDone = false;
                s.FinishedAt = -1f;
                s.EmittedThrough = d.TimeBeforeFirstEmission;
            }

            SettleEmitter(s, dt);
            float emitterT = EmitterTime(s);
            Vector3 previousBasePos = s.BasePos;
            Matrix4x4 placement = EmitterPlacement(d);
            s.BasePos = Vector3.Transform(d.EmitterPosition.Sample(emitterT), placement);
            Vector3 emitterDelta = s.BasePos - previousBasePos;
            s.TrailDistance += emitterDelta.Length();

            // Existing particles are integrated before this step's births. Riot spawns new
            // particles with a zero-sized birth step, so they remain exactly at their birth
            // transform until the following simulation step.
            for (int i = s.Particles.Count - 1; i >= 0; i--)
            {
                var p = s.Particles[i];
                Vector3 positionBeforeStep = p.Pos;
                if (d.IsEmitterSpace && emitterDelta.LengthSquared() > 1e-12f)
                    p.Pos += emitterDelta;

                p.Age += dt;
                if (p.Age >= p.Life)
                {
                    ParticleLifecycle?.Invoke(this, d, LifecycleInfo(s, p, died: true));
                    s.Particles.RemoveAt(i);
                    continue;
                }

                float particleT = float.IsPositiveInfinity(p.Life) ? 0f : Math.Clamp(p.Age / p.Life, 0f, 1f);
                if (d.BindWeight is { } bindWeight && systemDelta.LengthSquared() > 1e-12f)
                {
                    float bind = Math.Clamp(bindWeight.Sample(emitterT), 0f, 1f);
                    if (bind > 0f) p.Pos += systemDelta * bind;
                }

                float lingerT = LingerProgress(s);
                Vector3 acceleration = s.FinishedAt >= 0f && d.Linger?.Acceleration is { } lingerAcceleration
                    ? lingerAcceleration.Sample(lingerT)
                    : d.AccelerationOverLife?.Sample(emitterT) ?? Vector3.Zero;
                acceleration = Vector3.TransformNormal(acceleration, p.BirthFrame);
                p.Vel += (p.BirthAccel + acceleration) * dt;

                Vector3 authoredVelocity = s.FinishedAt >= 0f && d.Linger?.Velocity is { } lingerVelocity
                    ? lingerVelocity.Sample(lingerT)
                    : d.VelocityOverLife?.Sample(emitterT) ?? Vector3.Zero;
                authoredVelocity = Vector3.TransformNormal(authoredVelocity, p.BirthFrame);
                Vector3 moving = p.Vel + authoredVelocity;
                Vector3 dragOverLife = s.FinishedAt >= 0f && d.Linger?.Drag is { } lingerDrag
                    ? lingerDrag.Sample(lingerT)
                    : d.DragOverLife?.Sample(emitterT) ?? Vector3.Zero;
                Vector3 drag = p.BirthDrag + dragOverLife;
                if (_dragMotion == VfxDragMotion.Analytic)
                    ApplyAnalyticDrag(ref p, ref moving, drag, dt);
                else
                    ApplySteppedDrag(ref p.Vel, ref moving, drag, dt);

                // Force fields act after the particle's own drag. Their delta is applied
                // both to this step's movement and to the velocity the particle keeps,
                // matching LTK/Riot's pushInto stage.
                ApplyFields(d.Fields, s, emitterT, CurrentTime, p.Pos, p.Serial, dt, ref moving, ref p.Vel);
                p.Pos += moving * dt;

                // Birth angular velocity and acceleration always integrate. rotation0 is
                // a separate integrated value authored per 1/60 second and is gated only
                // by isRotationEnabled.
                p.BirthRotation += (p.RotationalVelocity + p.RotationalAcceleration * p.Age) * dt;
                if (d.IsRotationEnabled && d.RotationOverLife is { } rotationCurve)
                {
                    Vector3 rotationRate = rotationCurve.Sample(particleT);
                    if (d.Rotation1 is { } rotationMax)
                        rotationRate = Vector3.Lerp(rotationRate, rotationMax.Sample(particleT), p.RangeRandom);
                    if (s.FinishedAt >= 0f && d.Linger?.Rotation is { } lingerRotation)
                        rotationRate = lingerRotation.Sample(lingerT);
                    p.BirthRotation += rotationRate * (60f * MathF.PI / 180f) * dt;
                }

                if (d.ParticleUvScrollRate is { } uvScroll)
                    p.IntegratedUvOffset += uvScroll.Sample(particleT) * dt;
                if (d.ParticleUvRotateRate is { } uvRotate)
                    p.IntegratedUvRotation += uvRotate.Sample(particleT) * dt;
                if (d.TextureMultParticleUvScroll is { } multScroll)
                    p.IntegratedTextureMultUvOffset += multScroll.Sample(particleT) * dt;
                if (d.TextureMultParticleUvRotate is { } multRotate)
                    p.IntegratedTextureMultUvRotation += multRotate.Sample(particleT) * dt;

                Vector3 displacement = p.Pos - positionBeforeStep;
                if (d.IsEmitterSpace) displacement += systemDelta;
                p.Travel = dt > 0f ? displacement / dt : Vector3.Zero;
                s.Particles[i] = p;
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
        }

        private static void ApplyAnalyticDrag(ref Particle particle, ref Vector3 moving, Vector3 drag, float dt)
        {
            for (int axis = 0; axis < 3; axis++)
            {
                float dragAxis = axis == 0 ? drag.X : axis == 1 ? drag.Y : drag.Z;
                if (dragAxis <= 0f)
                {
                    Vector3 kept = particle.Vel;
                    ApplySteppedDragAxis(ref kept, ref moving, dragAxis, dt, axis);
                    particle.Vel = kept;
                    continue;
                }
                if (dt <= 0f) continue;
                float terminal = axis == 0 ? particle.AnalyticTerminal.X : axis == 1 ? particle.AnalyticTerminal.Y : particle.AnalyticTerminal.Z;
                float previous = axis == 0 ? particle.AnalyticOffset.X : axis == 1 ? particle.AnalyticOffset.Y : particle.AnalyticOffset.Z;
                float next = MathF.Exp(-dragAxis * particle.Age) * terminal;
                float contribution = (previous - next) / dt;
                if (axis == 0)
                {
                    moving.X += contribution;
                    particle.AnalyticOffset.X = next;
                }
                else if (axis == 1)
                {
                    moving.Y += contribution;
                    particle.AnalyticOffset.Y = next;
                }
                else
                {
                    moving.Z += contribution;
                    particle.AnalyticOffset.Z = next;
                }
            }
        }

        private static void ApplySteppedDragAxis(ref Vector3 kept, ref Vector3 moving, float dragAxis, float dt, int axis)
        {
            float movingAxis = axis == 0 ? moving.X : axis == 1 ? moving.Y : moving.Z;
            if (dragAxis == 0f || movingAxis == 0f) return;
            float change = -dragAxis * movingAxis * dt;
            if ((change + movingAxis) * movingAxis < 0f) change = -movingAxis;
            if (axis == 0)
            {
                moving.X += change;
                kept.X += change;
            }
            else if (axis == 1)
            {
                moving.Y += change;
                kept.Y += change;
            }
            else
            {
                moving.Z += change;
                kept.Z += change;
            }
        }

        private static void ApplySteppedDrag(ref Vector3 kept, ref Vector3 moving, Vector3 drag, float dt)
        {
            for (int axis = 0; axis < 3; axis++)
            {
                float movingAxis = axis == 0 ? moving.X : axis == 1 ? moving.Y : moving.Z;
                float dragAxis = axis == 0 ? drag.X : axis == 1 ? drag.Y : drag.Z;
                if (dragAxis == 0f || movingAxis == 0f) continue;
                float change = -dragAxis * movingAxis * dt;
                if ((change + movingAxis) * movingAxis < 0f) change = -movingAxis;
                if (axis == 0)
                {
                    moving.X += change;
                    kept.X += change;
                }
                else if (axis == 1)
                {
                    moving.Y += change;
                    kept.Y += change;
                }
                else
                {
                    moving.Z += change;
                    kept.Z += change;
                }
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

            float life = d.ParticleLifetime.SampleBirth(emitterT, _rng, sharedRoll);
            var rangeRandom = roll;
            var birthScale = d.BirthScale.SampleBirth(emitterT, _rng, sharedRoll);
            if (d.LegacyBirthScale is { } legacyBirthScale)
            {
                float scalar = legacyBirthScale.SampleBirth(emitterT, _rng, sharedRoll);
                Vector2 bias = d.LegacyScaleBias ?? Vector2.One;
                birthScale = new Vector3(scalar * bias.X, scalar * bias.Y, scalar);
            }
            else if (d.BirthScale1 is { } birthScale1)
            {
                birthScale = Vector3.Lerp(birthScale, birthScale1.SampleBirth(emitterT, _rng, sharedRoll), rangeRandom);
            }
            // isUniformScale promotes the first authored component to every axis for all
            // particle kinds, not only mesh primitives.
            if (d.IsUniformScale)
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
            Matrix4x4 placement = EmitterPlacement(d);
            var worldOffset = Vector3.TransformNormal(localOffset, placement);
            vel = Vector3.TransformNormal(vel, spawnRotation);
            birthAccel = Vector3.TransformNormal(birthAccel, spawnRotation);
            vel = Vector3.TransformNormal(vel, placement);
            birthAccel = Vector3.TransformNormal(birthAccel, placement);
            Vector3 finalBirthSize = birthScale * ExtractScale(placement);
            Vector3 analyticTerminal = Vector3.Zero;
            Vector3 analyticOffset = Vector3.Zero;
            if (_dragMotion == VfxDragMotion.Analytic)
            {
                analyticTerminal = new Vector3(
                    birthDrag.X > 0f ? vel.X / birthDrag.X : 0f,
                    birthDrag.Y > 0f ? vel.Y / birthDrag.Y : 0f,
                    birthDrag.Z > 0f ? vel.Z / birthDrag.Z : 0f);
                analyticOffset = analyticTerminal;
                vel = Vector3.Zero;
            }

            s.Particles.Add(new Particle
            {
                Pos = s.BasePos + worldOffset,
                Vel = vel,
                BirthAccel = birthAccel,
                BirthOrbitalVelocity = birthOrbitalVelocity,
                BirthDrag = birthDrag,
                AnalyticTerminal = analyticTerminal,
                AnalyticOffset = analyticOffset,
                BirthFrame = placement,
                SpawnRotation = Quaternion.CreateFromRotationMatrix(spawnRotation),
                Age = 0f,
                Life = life,
                Serial = _particleSerial++,
                TrailTiling = d.Trail?.BirthTilingSize.SampleBirth(emitterT, _rng, sharedRoll)
                    ?? d.Beam?.BirthTilingSize.SampleBirth(emitterT, _rng, sharedRoll)
                    ?? Vector3.Zero,
                TrailBirthDistance = s.TrailDistance,
                BirthSize = finalBirthSize,
                BirthColor = VfxColorSemantics.ResolveBirth(d.BirthColor, emitterT, _rng, sharedRoll),
                BirthRotation = birthRotation * (MathF.PI / 180f),
                RotationalVelocity = rotVel * (MathF.PI / 180f),
                RotationalAcceleration = rotationalAcceleration * (MathF.PI / 180f),
                Rot = birthRotation.X * (MathF.PI / 180f),
                RotVel = rotVel.X * (MathF.PI / 180f),
                RangeRandom = rangeRandom,
                StartFrame = d.RandomStartFrame && d.NumFrames > 1 ? roll * d.NumFrames : 0f,
                FrameRate = (d.FrameRate ?? 0f) *
                    (d.BirthFrameRate?.SampleBirth(emitterT, _rng, sharedRoll) ?? 1f),
                TextureMultFrame = 0f,
                BirthUvOffset = birthUvOffset,
                BirthUvScrollRate = birthUvScrollRate,
                BirthUvRotateRate = birthUvRotateRate,
                TextureMultBirthUvOffset = textureMultBirthUvOffset,
                TextureMultBirthUvScrollRate = textureMultBirthUvScrollRate,
                TextureMultBirthUvRotateRate = textureMultBirthUvRotateRate,
                LingerFrom = -1f
            });
            ParticleLifecycle?.Invoke(this, d, LifecycleInfo(s, s.Particles[^1], died: false));
        }

        private void BuildInstances(EmitterState s)
        {
            var d = s.Def;
            int n = s.Particles.Count;
            int instanceCount = n;
            if (s.Instances.Length < instanceCount * InstanceStride)
                s.Instances = new float[Math.Max(instanceCount * InstanceStride, InstanceStride * 4)];
            var buf = s.Instances;
            float emitterT = EmitterTime(s);
            int k = 0;
            for (int i = 0; i < n; i++)
            {
                var p = s.Particles[i];
                float t = float.IsPositiveInfinity(p.Life) ? 0f : Math.Clamp(p.Age / p.Life, 0f, 1f);
                float particleLingerT = 0f;
                bool lingering = p.LingerFrom >= 0f;
                if (lingering)
                {
                    float window = d.ParticleLingerType == 0
                        ? LingerSeconds(d)
                        : MathF.Max(0f, p.Life - p.LingerFrom);
                    particleLingerT = window > 0f
                        ? Math.Clamp((p.Age - p.LingerFrom) / window, 0f, 1f)
                        : 1f;
                }
                var scaleMul = lingering && d.Linger?.Scale is { } lingerScale
                    ? lingerScale.Sample(particleLingerT)
                    : d.ScaleOverLife?.Sample(t) ?? Vector3.One;
                if (d.IsUniformScale)
                    scaleMul = new Vector3(scaleMul.X);
                Vector4 col = lingering && d.Linger?.Color is { } lingerColor
                    ? p.BirthColor * lingerColor.Sample(particleLingerT)
                    : VfxColorSemantics.ResolveParticle(p.BirthColor, d.ColorOverLife, t);
                col = VfxColorSemantics.PremultiplyForAddOrSubtract(col, d.BlendMode, d.Distortion != null);

                float frame = Math.Clamp(d.StartFrame, 0f, Math.Max(0, d.NumFrames - 1));
                if (d.NumFrames > 1)
                {
                    float played = PositiveModulo(p.StartFrame + p.Age * p.FrameRate, d.NumFrames);
                    int cells = Math.Max(1, (int)MathF.Round(MathF.Max(1f, d.TexDiv.X)) *
                        (int)MathF.Round(MathF.Max(1f, d.TexDiv.Y)));
                    frame = PositiveModulo(MathF.Floor(d.StartFrame + played), cells);
                }

                Vector3 position = p.Pos;
                Vector3 orbitalAngles = p.BirthOrbitalVelocity * p.Age;
                Quaternion orbitalTurn = Quaternion.Identity;
                if (orbitalAngles.LengthSquared() > 1e-8f)
                {
                    Vector3 origin = new(_worldTransform.M41, _worldTransform.M42, _worldTransform.M43);
                    orbitalTurn = Quaternion.CreateFromYawPitchRoll(orbitalAngles.Y, orbitalAngles.X, orbitalAngles.Z);
                    position = origin + Vector3.Transform(position - origin, orbitalTurn);
                }
                if (d.Acceleration is { } worldAcceleration && float.IsFinite(p.Life))
                {
                    float reached = t * p.Life * p.Life;
                    position += worldAcceleration.Sample(emitterT) * reached;
                }
                float sizeX = p.BirthSize.X * scaleMul.X;
                float sizeY = p.BirthSize.Y * scaleMul.Y;
                if (d.PrimitiveKind == VfxPrimitiveKind.ArbitraryQuad)
                {
                    sizeX *= 2f;
                    sizeY *= 2f;
                    if (d.IsGroundLayer && d.IsUniformScale)
                        sizeY = sizeX;
                }
                Vector3 direction = p.Travel;

                if (d.IsDirectionOriented && d.PrimitiveKind != VfxPrimitiveKind.Ray && direction.LengthSquared() > 1e-8f)
                {
                    float stretch = MathF.Max(d.DirectionVelocityMinScale, direction.Length() * d.DirectionVelocityScale);
                    sizeY *= stretch;
                }
                if (d.UseTextureAspect) sizeX *= s.SpriteAspect;
                buf[k++] = position.X; buf[k++] = position.Y; buf[k++] = position.Z;
                buf[k++] = sizeX;
                buf[k++] = sizeY;
                buf[k++] = col.X; buf[k++] = col.Y; buf[k++] = col.Z; buf[k++] = col.W;
                int rotationSlot = k++;
                buf[k++] = frame;
                buf[k++] = t;
                buf[k++] = direction.X; buf[k++] = direction.Y; buf[k++] = direction.Z;
                Vector3 currentRotation = p.BirthRotation;
                float legacyRoll = d.LegacyRotation?.Sample(t) * (MathF.PI / 180f) ?? 0f;
                buf[rotationSlot] = currentRotation.X + legacyRoll;
                bool authoredPlane = d.IsArbitraryQuad || d.PrimitiveKind is
                    VfxPrimitiveKind.ArbitraryTrail or VfxPrimitiveKind.PlanarProjection;
                if (d.IsDirectionOriented && !authoredPlane && direction.LengthSquared() > 1e-6f)
                {
                    Vector3 dir = Vector3.Normalize(direction);
                    float yaw = MathF.Atan2(dir.X, dir.Z);
                    float pitch = MathF.Asin(Math.Clamp(-dir.Y, -1f, 1f));
                    buf[k++] = pitch + currentRotation.X;
                    buf[k++] = yaw + currentRotation.Y;
                    buf[k++] = currentRotation.Z;
                }
                else
                {
                    buf[k++] = currentRotation.X;
                    buf[k++] = currentRotation.Y;
                    buf[k++] = currentRotation.Z;
                }
                float sizeZ = p.BirthSize.Z * scaleMul.Z;
                buf[k++] = sizeZ;
                Vector2 uvRamp = p.BirthUvOffset + p.BirthUvScrollRate * p.Age;
                if (d.RenderState?.ClampUvScroll == true)
                    uvRamp = Vector2.Clamp(uvRamp, new Vector2(-1f), Vector2.One);
                Vector2 uvOffset = uvRamp + p.IntegratedUvOffset;
                Vector2 uvScale = d.UvScale?.Sample(t) ?? Vector2.One;
                float uvRotationDegrees = (d.UvRotation?.Sample(t) ?? 0f) + p.BirthUvRotateRate * p.Age
                    + p.IntegratedUvRotation;
                float uvRotation = uvRotationDegrees * (MathF.PI / 180f);
                buf[k++] = uvOffset.X; buf[k++] = uvOffset.Y;
                buf[k++] = uvScale.X; buf[k++] = uvScale.Y;
                buf[k++] = uvRotation;
                float erosionDrive = lingering && d.AlphaErosion?.LingerDrive is { } lingerErosion
                    ? lingerErosion.Sample(particleLingerT)
                    : d.AlphaErosion?.Drive.Sample(t) ?? 1f;
                buf[k++] = erosionDrive;
                Vector4 erosionMixer = d.AlphaErosion?.ChannelMixer?.Sample(0f) ?? new Vector4(0f, 0f, 0f, 1f);
                buf[k++] = erosionMixer.X; buf[k++] = erosionMixer.Y;
                buf[k++] = erosionMixer.Z; buf[k++] = erosionMixer.W;
                Vector2 textureMultRamp = p.TextureMultBirthUvOffset + p.TextureMultBirthUvScrollRate * p.Age;
                if (d.TextureMultClampUvScroll)
                    textureMultRamp = Vector2.Clamp(textureMultRamp, new Vector2(-1f), Vector2.One);
                Vector2 textureMultUvOffset = textureMultRamp + p.IntegratedTextureMultUvOffset;
                Vector2 textureMultUvScale = d.TextureMultUvScale?.Sample(t) ?? Vector2.One;

                float textureMultUvRotationDegrees = (d.TextureMultUvRotation?.Sample(t) ?? 0f)
                    + p.TextureMultBirthUvRotateRate * p.Age
                    + p.IntegratedTextureMultUvRotation;
                buf[k++] = textureMultUvOffset.X; buf[k++] = textureMultUvOffset.Y;
                buf[k++] = textureMultUvScale.X; buf[k++] = textureMultUvScale.Y;
                buf[k++] = textureMultUvRotationDegrees * (MathF.PI / 180f);
                buf[k++] = p.RangeRandom;
                buf[k++] = d.PaletteDefinition?.PaletteSelector.Sample(0f).X ?? 0f;

                Matrix4x4 basis = ParticleBasis(p, s, direction, orbitalTurn, legacyRoll);
                Vector3 basisX = SafeNormal(Vector3.TransformNormal(Vector3.UnitX, basis), Vector3.UnitX);
                Vector3 basisY = SafeNormal(Vector3.TransformNormal(Vector3.UnitY, basis), Vector3.UnitY);
                Vector3 basisZ = SafeNormal(Vector3.TransformNormal(Vector3.UnitZ, basis), Vector3.UnitZ);
                buf[k++] = basisX.X; buf[k++] = basisX.Y; buf[k++] = basisX.Z;
                buf[k++] = basisY.X; buf[k++] = basisY.Y; buf[k++] = basisY.Z;
                buf[k++] = basisZ.X; buf[k++] = basisZ.Y; buf[k++] = basisZ.Z;
            }
            s.InstanceCount = k / InstanceStride;
        }

        private static float PositiveModulo(float value, float span)
        {
            if (span <= 0f) return 0f;
            float wrapped = value % span;
            return wrapped < 0f ? wrapped + span : wrapped;
        }

        private float ResolveFlexMultiplier(float? coefficient)
        {
            if (coefficient is not { } value || BoundObjectSize is not { } bounds) return 1f;
            float extent = MathF.Max(MathF.Abs(bounds.X), MathF.Max(MathF.Abs(bounds.Y), MathF.Abs(bounds.Z)));
            return MathF.Max(0f, 1f + value * extent);
        }

        private void ApplyFields(
            VfxFieldCollectionDefinition fields,
            EmitterState state,
            float emitterT,
            float now,
            Vector3 particlePosition,
            uint serial,
            float dt,
            ref Vector3 moving,
            ref Vector3 kept)
        {
            if (fields is null) return;

            Vector3 before = moving;
            Matrix4x4 localOrientation = OrientationOnly(_worldTransform);

            // Riot samples every field at emitter life, not particle life, and applies
            // fields after the particle's own drag in this exact order.
            foreach (VfxAccelerationField field in fields.Acceleration)
            {
                Vector3 value = field.Acceleration.Sample(emitterT);
                if (field.LocalSpace && state.Def.IsLocalOrientation)
                    value = Vector3.TransformNormal(value, localOrientation);
                moving += value * dt;
            }

            foreach (VfxAttractionField field in fields.Attraction)
            {
                Vector3 center = state.SystemOrigin + field.Position.Sample(emitterT);
                Vector3 delta = center - particlePosition;
                float radius = field.Radius.Sample(emitterT);
                float reach = delta.LengthSquared();
                if (reach > radius * radius) continue;
                float strength = field.Acceleration.Sample(emitterT);
                float scale = strength * dt / MathF.Sqrt(MathF.Max(reach, 1f));
                moving += delta * scale;
            }

            int noiseCount = fields.Noise.Count;
            if (noiseCount > 0)
            {
                if (state.NoiseLast.Length != noiseCount)
                {
                    state.NoiseLast = new float[noiseCount];
                    Array.Fill(state.NoiseLast, float.NaN);
                    state.NoiseFired = new int[noiseCount];
                }

                for (int slot = 0; slot < noiseCount; slot++)
                {
                    VfxNoiseField field = fields.Noise[slot];
                    float frequency = field.Frequency.Sample(emitterT);
                    int first = state.NoiseFired[slot];
                    int owed;
                    if (float.IsNaN(state.NoiseLast[slot]))
                    {
                        owed = 1;
                    }
                    else
                    {
                        double currentTicks = Math.Truncate((double)frequency * now);
                        double previousTicks = Math.Truncate((double)frequency * state.NoiseLast[slot]);
                        owed = (int)Math.Clamp(currentTicks - previousTicks, 0d, int.MaxValue);
                    }
                    if (owed > 0) state.NoiseLast[slot] = now;
                    int kicks = Math.Min(owed, 256);
                    state.NoiseFired[slot] += kicks;
                    if (kicks == 0) continue;

                    Vector3 center = state.SystemOrigin + field.Position.Sample(emitterT);
                    float radius = field.Radius.Sample(emitterT);
                    if (Vector3.DistanceSquared(particlePosition, center) > radius * radius) continue;
                    float delta = field.VelocityDelta.Sample(emitterT);
                    for (int kick = 0; kick < kicks; kick++)
                    {
                        Vector3 direction = NoiseDirection(serial, slot, first + kick);
                        moving += direction * field.AxisFraction * delta;
                    }
                }
            }

            foreach (VfxDragField field in fields.Drag)
            {
                Vector3 center = state.SystemOrigin + field.Position.Sample(emitterT);
                float radius = field.Radius.Sample(emitterT);
                if (Vector3.DistanceSquared(particlePosition, center) > radius * radius) continue;
                float strength = field.Strength.Sample(emitterT);
                moving *= MathF.Max(1f - strength * dt, 0f);
            }

            foreach (VfxOrbitalField field in fields.Orbital)
            {
                Vector3 axis = field.Direction.Sample(emitterT);
                if (field.LocalSpace && state.Def.IsLocalOrientation)
                    axis = Vector3.TransformNormal(axis, localOrientation);
                if (axis.LengthSquared() <= 1e-12f) continue;
                axis = Vector3.Normalize(axis);
                ApplyOrbitalField(ref moving, particlePosition, state.SystemOrigin, axis);
            }

            // Field deltas persist in the particle velocity just as LTK's pushInto adds
            // MOVING-PUSHED into KEPT.
            kept += moving - before;
        }

        private static void ApplyOrbitalField(ref Vector3 velocity, Vector3 particle, Vector3 center, Vector3 axis)
        {
            float along = Vector3.Dot(velocity, axis);
            Vector3 planar = velocity - axis * along;
            float speed = planar.Length();
            if (speed <= 0.001f) return;

            Vector3 radial = particle - center;
            float reach = radial.Length();
            if (reach <= 1e-12f) return;
            radial /= reach;
            if (MathF.Abs(1f - Vector3.Dot(axis, radial)) <= 1e-4f) return;

            Vector3 tangent = Vector3.Cross(radial, axis);
            float tangentLength = tangent.Length();
            if (tangentLength <= 1e-12f) return;
            float scale = speed / tangentLength;
            if (Vector3.Dot(tangent, planar) < 0f) scale = -scale;
            velocity = axis * along + tangent * scale;
        }

        private static Vector3 NoiseDirection(uint serial, int slot, int impulse)
        {
            uint first = Mix32(Mix32(serial + 1u) ^ Mix32(unchecked((uint)((slot + 1) * (int)0x9e3779b1 + impulse))));
            uint second = Mix32(first ^ 0x68e31da4u);
            uint third = Mix32(second ^ 0x1b56c4e9u);
            const double span = 4294967296.0;
            Vector3 value = new(
                (float)(first / span * 2.0 - 1.0),
                (float)(second / span * 2.0 - 1.0),
                (float)(third / span * 2.0 - 1.0));
            return value.LengthSquared() < 1e-12f ? value : Vector3.Normalize(value);
        }

        private static uint Mix32(uint value)
        {
            unchecked
            {
                uint held = value;
                held = (held ^ (held >> 16)) * 0x7feb352du;
                held = (held ^ (held >> 15)) * 0x846ca68bu;
                return held ^ (held >> 16);
            }
        }

    }
}
