using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Semantics;
using AssetsManager.Views.Models.Viewer;
using AssetsManager.Utils.Rendering;

namespace AssetsManager.Services.Viewer.Vfx.Runtime
{
    /// <summary>
    /// Maintains deterministic, graphics-independent playback state for one placed effect graph.
    /// </summary>
    public sealed partial class VfxPlaybackRuntime
    {
        public const int InstanceStride = 45;

        public IReadOnlyList<EmitterState> Emitters => _emitters;
        private readonly List<EmitterState> _emitters = new();
        private EmitterStepContext[] _stepContexts = Array.Empty<EmitterStepContext>();
        private int[] _newbornStarts = Array.Empty<int>();
        private readonly int _seed;
        private VfxSystemDefinition _definition;
        private static readonly IReadOnlyDictionary<VfxEmitterDefinition, IVfxEmissionSurfaceSampler> EmptyEmissionSurfaces =
            new Dictionary<VfxEmitterDefinition, IVfxEmissionSurfaceSampler>(ReferenceEqualityComparer.Instance);
        private IReadOnlyDictionary<VfxEmitterDefinition, IVfxEmissionSurfaceSampler> _emissionSurfaces = EmptyEmissionSurfaces;
        private uint _initialRandomState;
        private VfxLtkRandom _rng;
        private float? _pinnedBirthChance;
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
        public readonly record struct ParticleLifecycleInfo(
            Vector3 Position,
            Matrix4x4 Basis,
            Matrix4x4 Frame,
            uint Serial,
            int SourceOrder,
            float ParticleTime,
            float ParticleLifetime,
            float EmitterPhase,
            bool Died);

        public event Action<VfxPlaybackRuntime, VfxEmitterDefinition, ParticleLifecycleInfo> ParticleLifecycle;
        // Carried child systems need the parent's current drawn bearing every step, not only
        // its birth/death notifications. Keep this internal to the graph runtime pipeline.
        internal event Action<VfxPlaybackRuntime, VfxEmitterDefinition, ParticleLifecycleInfo> ParticleUpdated;
        // LTK gives the opened/root system a shared 32K particle pool. Child systems use
        // smaller lineage pools chosen from their authored peak demand (16..4096).
        private const int RootParticleCapacity = 32_768;
        private int _particleCapacity = RootParticleCapacity;

        internal Matrix4x4 WorldTransform => _worldTransform;
        internal VfxSystemDefinition Definition => _definition;
        internal int Seed => _seed;
        internal uint InitialRandomState => _initialRandomState;
        internal uint RandomState => _rng.State;
        internal float? PinnedBirthChance => _pinnedBirthChance;
        internal int ParticleCapacity => _particleCapacity;

        public void SetTransform(Matrix4x4 worldTransform)
            => SetTransform(worldTransform, worldTransform);

        internal void SetTransform(Matrix4x4 worldTransform, Matrix4x4 orientationRootTransform)
        {
            Vector3 previousOrigin = new(_worldTransform.M41, _worldTransform.M42, _worldTransform.M43);
            Vector3 nextOrigin = new(worldTransform.M41, worldTransform.M42, worldTransform.M43);
            _pendingOriginDelta += nextOrigin - previousOrigin;
            _worldTransform = worldTransform;
            _orientationRootTransform = orientationRootTransform;
            if (!Matrix4x4.Invert(worldTransform, out _inverseWorldTransform))
                _inverseWorldTransform = Matrix4x4.Identity;

            foreach (var es in _emitters)
            {
                Matrix4x4 placement = EmitterPlacement(es.Def);
                es.PlacementTransform = placement;
                float emitterT = EmitterTime(es);
                Vector3 nextBasePos = Vector3.Transform(es.Def.EmitterPosition.Sample(emitterT), placement);
                es.BasePos = nextBasePos;
                es.FieldBasePos = EmitterFieldPosition(es.Def, emitterT);
                es.SystemOrigin = nextOrigin;
                es.SystemTarget = Vector3.Transform(new Vector3(600f, 0f, 0f), worldTransform);
                es.PlacementRight = VectorMathUtils.NormalizeOr(Vector3.TransformNormal(Vector3.UnitX, placement), Vector3.UnitX);
                es.PlacementUp = VectorMathUtils.NormalizeOr(Vector3.TransformNormal(Vector3.UnitY, placement), Vector3.UnitY);
                es.PlacementForward = VectorMathUtils.NormalizeOr(Vector3.TransformNormal(Vector3.UnitZ, placement), Vector3.UnitZ);
                es.InvalidateInstances();
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
            _rng = new VfxLtkRandom(seed);
            _initialRandomState = _rng.State;
        }

        /// <summary>
        /// Starts this runtime from an already-advanced lineage RNG state. LTK hands a
        /// normal child the same RNG that selected its childrenProbability slot, so the
        /// child must retain the consumed draw rather than restart from the original seed.
        /// </summary>
        internal void SetInitialRandomState(uint state)
        {
            _initialRandomState = state == 0 ? new VfxLtkRandom(_seed).State : state;
            _rng.State = _initialRandomState;
        }

        internal void SetPinnedBirthChance(float? chance)
            => _pinnedBirthChance = chance;

        /// <summary>
        /// Sets the shared particle capacity for this runtime. The root keeps 32768 while
        /// LTK child systems receive a smaller lineage capacity before their build-up runs.
        /// </summary>
        internal void SetParticleCapacity(int capacity)
            => _particleCapacity = Math.Clamp(capacity, 1, RootParticleCapacity);

        /// <summary>
        /// Installs loaded emission surfaces by emitter identity. A late surface
        /// install rewinds and deterministically replays the current time so already-born
        /// particles are not left in the old spawn state.
        /// </summary>
        public void SetEmissionSurfaces(
            IReadOnlyDictionary<VfxEmitterDefinition, IVfxEmissionSurfaceSampler> surfaces,
            bool replayCurrentTime = true)
        {
            surfaces ??= EmptyEmissionSurfaces;
            if (SameReferences(_emissionSurfaces, surfaces)) return;
            _emissionSurfaces = surfaces;

            if (!replayCurrentTime || _definition is null || CurrentTime <= 0f) return;
            float targetTime = CurrentTime;
            Reset();
            Seek(targetTime);
        }

        /// <summary>Whether both maps hold the same keys bound to the very same instances.</summary>
        internal static bool SameReferences<TKey, TValue>(
            IReadOnlyDictionary<TKey, TValue> a,
            IReadOnlyDictionary<TKey, TValue> b)
            where TValue : class
        {
            if (ReferenceEquals(a, b)) return true;
            if (a.Count != b.Count) return false;
            foreach (var (k, v) in a)
            {
                if (!b.TryGetValue(k, out var other) || !ReferenceEquals(v, other))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Child systems in LTK start empty and receive their first simulation step on the
        /// frame after their birth; buildUpTime is a root-driver pre-roll only.
        /// </summary>
        internal void SuppressBuildUp()
            => _needsBuildUp = false;

        /// <summary>Configure from a system placed at worldPos.</summary>
        public void SetSystem(VfxSystemDefinition system, Vector3 worldPos)
            => SetSystem(system, Matrix4x4.CreateTranslation(worldPos));

        /// <summary>Configure a system with its complete authored placement transform.</summary>
        public void SetSystem(VfxSystemDefinition system, Matrix4x4 worldTransform)
        {
            _definition = system;
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
                var emitterState = new EmitterState
                {
                    Def = e,
                    SourceOrder = emitterIndex,
                    BasePos = Vector3.Transform(e.EmitterPosition.Sample(0f), EmitterTransform(e, worldTransform)),
                    FieldBasePos = Vector3.Transform(e.EmitterPosition.Sample(0f), EmitterFieldTransform(e, worldTransform)),
                    PlacementRight = VectorMathUtils.NormalizeOr(Vector3.TransformNormal(Vector3.UnitX, worldTransform), Vector3.UnitX),
                    PlacementUp = VectorMathUtils.NormalizeOr(Vector3.TransformNormal(Vector3.UnitY, worldTransform), Vector3.UnitY),
                    PlacementForward = VectorMathUtils.NormalizeOr(Vector3.TransformNormal(Vector3.UnitZ, worldTransform), Vector3.UnitZ),
                };
                emitterState.BindOwner(this);
                _emitters.Add(emitterState);
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
            _emitters.Sort((left, right) => VfxDrawOrderSemantics.Compare(
                left.Def,
                left.SourceOrder,
                right.Def,
                right.SourceOrder));
        }

        public void Reset()
        {
            _rng = VfxLtkRandom.FromState(_initialRandomState);
            _particleSerial = 0;
            ResetRunState();
        }

        internal void ReplayLoop()
        {
            // A rig loop starts another pass on the same random stream. Preserve both
            // RNG state and the particle serial so child lineage remains deterministic.
            ResetRunState();
        }

        private void ResetRunState()
        {
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
                s.TrailSpawnedAt = null;
                s.NoiseLast = Array.Empty<float>();
                s.NoiseFired = Array.Empty<int>();
                s.BasePos = Vector3.Transform(s.Def.EmitterPosition.Sample(0f), EmitterPlacement(s.Def));
                s.FieldBasePos = EmitterFieldPosition(s.Def, 0f);
                s.EmittedThrough = s.Def.TimeBeforeFirstEmission;
                s.Age = 0;
                s.FinishedAt = -1f;
                s.BurstDone = false;
                s.InitialEmissionDone = false;
                s.SharedRandomRolled = false;
                s.RenderTime = 0f;
                s.InvalidateInstances();
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
                (state.Def.Disabled || IsStopped || state.BurstDone ||
                 (state.Def.EmitterLifetime is { } lifetime && state.Age > lifetime)) &&
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
            if (steps <= 0) return;

            const float dt = 1f / 60f;
            CurrentTime = -steps * dt;
            for (int step = 0; step < steps; step++)
            {
                CurrentTime += dt;
                StepEmitters(dt, Vector3.Zero);
            }
            CurrentTime = 0f;
        }

        public void Update(float dt)
        {
            if (_isKilled || dt <= 0f || !float.IsFinite(dt)) return;
            WarmUp();
            // The driver owns timestep policy. LTK's variable stepper advances the runtime once
            // with the frame duration it receives; seek/replay obtains fixed steps by calling it
            // repeatedly with 1/60 s, not by subdividing again inside the particle runtime.
            UpdateStep(dt);
        }

        public void Kill()
        {
            _isKilled = true;
            foreach (EmitterState emitter in _emitters)
            {
                emitter.Particles.Clear();
                emitter.InvalidateInstances();
            }
            LiveParticleCount = 0;
        }

    }
}
