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
            public required VfxEmitterDefinition Def { get; set; }
            public int SourceOrder { get; init; }
            /// <summary>
            /// Render identity of the authored emitter path. LTK groups every live source of the
            /// same graph/path/emitter into one draw component (not one draw component per runtime).
            /// </summary>
            internal object RenderGraphKey { get; set; }
            internal string RenderPath { get; set; } = string.Empty;
            /// <summary>The opened/root emitter this definition descends from, matching LTK DrawnEmitter.root.</summary>
            internal int RenderRootSourceOrder { get; set; }
            /// <summary>Stable definition-tree rank used by the renderer across all live sources of this path.</summary>
            internal int RenderRank { get; set; }
            public bool IsVisible { get; set; } = true;
            public Vector3 BasePos;                 // world spawn origin (placement + emitterPosition)
            internal Vector3 FieldBasePos;          // emitterPosition under the frame basis, excluding translationOverride
            public Vector3 SystemOrigin, SystemTarget;
            public Vector3 PlacementRight, PlacementUp, PlacementForward;
            internal Matrix4x4 PlacementTransform;
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
            /// <summary>Pending mesh data for deferred GL upload of authored VFX mesh primitives.</summary>
            public VfxMeshData? PendingMesh;
            /// <summary>GPU handle for particleColorTexture (0 = unavailable).</summary>
            public uint ColorGradientTexture;
            public object PendingColorGradient;
            internal float SharedRandom;
            internal bool SharedRandomRolled;
            internal float EmittedThrough;
            internal float Age;                     // emitter age (seconds)
            internal float FinishedAt = -1f;
            internal bool BurstDone;                // for isSingleParticle
            internal bool InitialEmissionDone;
            internal readonly List<Particle> Particles = new();

            private VfxPlaybackRuntime _owner;
            private float[] _instances = System.Array.Empty<float>();
            private int _manualInstanceCount;
            private int _preparedInstanceCount;
            private bool _instancesDirty = true;

            /// <summary>
            /// Packed draw data. Runtime-owned emitters build this lazily so simulation does not pay
            /// render preparation for particles the renderer will never consume. Directly-constructed
            /// diagnostic/test states keep the legacy assignable buffer contract.
            /// </summary>
            public float[] Instances
            {
                get
                {
                    _owner?.EnsureInstances(this, Particles.Count);
                    return _instances;
                }
                set
                {
                    _instances = value ?? System.Array.Empty<float>();
                    _preparedInstanceCount = _instances.Length / InstanceStride;
                    _instancesDirty = false;
                }
            }

            /// <summary>Live particle rows for this emitter, independent of the prepared draw buffer.</summary>
            public int InstanceCount
            {
                get => _owner != null ? Particles.Count : _manualInstanceCount;
                set => _manualInstanceCount = Math.Max(0, value);
            }

            internal int PreparedInstanceCount => _owner != null ? _preparedInstanceCount : Math.Min(_manualInstanceCount, _preparedInstanceCount);
            internal int InstanceBufferCapacity => _instances.Length;

            internal void BindOwner(VfxPlaybackRuntime owner)
            {
                _owner = owner;
                _instancesDirty = true;
                _preparedInstanceCount = 0;
            }

            internal void InvalidateInstances()
            {
                if (_owner == null) return;
                _instancesDirty = true;
                _preparedInstanceCount = 0;
            }

            internal void ResetInstanceBuffer(int length)
            {
                _instances = length > 0 ? new float[length] : System.Array.Empty<float>();
                _instancesDirty = true;
                _preparedInstanceCount = 0;
            }

            internal ReadOnlySpan<float> PrepareInstances(int requestedCount)
            {
                int available = InstanceCount;
                int wanted = Math.Clamp(requestedCount, 0, available);
                if (_owner != null)
                    _owner.EnsureInstances(this, wanted);
                else
                    wanted = Math.Min(wanted, _preparedInstanceCount);
                return new ReadOnlySpan<float>(_instances, 0, wanted * InstanceStride);
            }

            internal float[] RawInstances => _instances;
            internal bool InstancesDirty => _instancesDirty;
            internal void MarkInstancesPrepared(int count)
            {
                _preparedInstanceCount = Math.Max(0, count);
                _instancesDirty = false;
            }

            internal float TrailDistance;
            internal Vector3? TrailSpawnedAt;
            internal float[] NoiseLast = Array.Empty<float>();
            internal int[] NoiseFired = Array.Empty<int>();
            internal PreparedNoiseField[] PreparedNoise = Array.Empty<PreparedNoiseField>();

            // Mesh-primitive emitters (0 = billboard)
            public uint MeshVao, MeshVbo, MeshEbo;
            public int MeshVertexCount, MeshIndexCount;
            public float[] MeshInterleaved;
            /// <summary>True when the uploaded mesh carries a valid four-weight skinning layout.</summary>
            public bool MeshHasSkinning;
            /// <summary>Owner skinScale, applied after skeleton skinning for AttachedMesh.</summary>
            public float MeshOwnerScale = 1f;
            /// <summary>Owner SKN draw groups retained so clip visibility can change AttachedMesh live.</summary>
            public VfxMeshRangeData[] MeshRanges = Array.Empty<VfxMeshRangeData>();
            /// <summary>Selected particle-mesh pose. Unlike AttachedMesh, it advances on each particle's age.</summary>
            internal VfxAnimatedMesh MeshAnimation;
            internal VfxAnimatedMesh MeshBaseAnimation;
            internal VfxAnimatedMesh[] MeshAnimationVariants = Array.Empty<VfxAnimatedMesh>();

            /// <summary>
            /// Detaches resolved CPU/GPU appearance resources without touching the simulation pool.
            /// Renderer caches keep ownership of old handles, so a live definition swap can repoint
            /// the emitter and reuse any unchanged resource on the next deferred upload.
            /// </summary>
            internal void ResetResolvedResources()
            {
                Texture = 0;
                TextureWidth = 0;
                TextureHeight = 0;
                TextureMult = 0;
                TextureMultWidth = 0;
                TextureMultHeight = 0;
                DistortionTexture = 0;
                ErosionTexture = 0;
                ReflectionTexture = 0;
                PaletteTexture = 0;
                ColorGradientTexture = 0;
                PendingTexture = null;
                PendingTextureMult = null;
                PendingDistortionTexture = null;
                PendingErosionTexture = null;
                PendingReflectionTexture = null;
                PendingPaletteTexture = null;
                PendingColorGradient = null;
                PendingMesh = null;
                MeshVao = 0;
                MeshVbo = 0;
                MeshEbo = 0;
                MeshVertexCount = 0;
                MeshIndexCount = 0;
                MeshInterleaved = null;
                MeshHasSkinning = false;
                MeshOwnerScale = 1f;
                MeshRanges = Array.Empty<VfxMeshRangeData>();
                MeshAnimation = null;
                MeshBaseAnimation = null;
                MeshAnimationVariants = Array.Empty<VfxAnimatedMesh>();
            }

            /// <summary>Emitter-local age in seconds; drives emitter-phase curves and mesh animation time.</summary>
            public float EmitterAge => Age;
            /// <summary>
            /// Draw clock for this source. Root emitters use their runtime clock; graph children are
            /// overwritten with the root driver's clock so emitterUvScrollRate matches LTK Source.time.
            /// </summary>
            public float RenderTime { get; internal set; }
        }

        internal struct Particle
        {
            public Vector3 Pos, Vel, Travel, BirthOrbitalVelocity, BirthDrag;
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
            public float StartFrame, FrameRate;
        }

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

        internal sealed record EmitterSnapshot(
            Vector3 BasePos,
            Vector3 FieldBasePos,
            Vector3 SystemOrigin,
            Vector3 SystemTarget,
            Vector3 PlacementRight,
            Vector3 PlacementUp,
            Vector3 PlacementForward,
            float SharedRandom,
            bool SharedRandomRolled,
            float EmittedThrough,
            float Age,
            float FinishedAt,
            bool BurstDone,
            bool InitialEmissionDone,
            float TrailDistance,
            Vector3? TrailSpawnedAt,
            int InstanceBufferLength,
            float[] NoiseLast,
            int[] NoiseFired,
            Particle[] Particles);

        internal sealed record Snapshot(
            float CurrentTime,
            uint InitialRandomState,
            uint RandomState,
            uint ParticleSerial,
            Matrix4x4 WorldTransform,
            Matrix4x4 OrientationRootTransform,
            Matrix4x4 InverseWorldTransform,
            Vector3 PendingOriginDelta,
            bool NeedsBuildUp,
            bool IsKilled,
            bool IsStopped,
            float ConfiguredStartDelay,
            float StartDelay,
            EmitterSnapshot[] Emitters,
            long Bytes);

        internal Snapshot CaptureSnapshot()
        {
            var emitters = new EmitterSnapshot[_emitters.Count];
            long bytes = 256;
            for (int index = 0; index < _emitters.Count; index++)
            {
                EmitterState state = _emitters[index];
                Particle[] particles = state.Particles.ToArray();
                float[] noiseLast = (float[])state.NoiseLast.Clone();
                int[] noiseFired = (int[])state.NoiseFired.Clone();
                emitters[index] = new EmitterSnapshot(
                    state.BasePos,
                    state.FieldBasePos,
                    state.SystemOrigin,
                    state.SystemTarget,
                    state.PlacementRight,
                    state.PlacementUp,
                    state.PlacementForward,
                    state.SharedRandom,
                    state.SharedRandomRolled,
                    state.EmittedThrough,
                    state.Age,
                    state.FinishedAt,
                    state.BurstDone,
                    state.InitialEmissionDone,
                    state.TrailDistance,
                    state.TrailSpawnedAt,
                    state.InstanceBufferCapacity,
                    noiseLast,
                    noiseFired,
                    particles);
                // Conservative accounting keeps the session checkpoint budget bounded without
                // depending on CLR struct layout details.
                bytes += 192L + particles.LongLength * 256L + noiseLast.LongLength * sizeof(float) +
                         noiseFired.LongLength * sizeof(int);
            }

            return new Snapshot(
                CurrentTime,
                _initialRandomState,
                _rng.State,
                _particleSerial,
                _worldTransform,
                _orientationRootTransform,
                _inverseWorldTransform,
                _pendingOriginDelta,
                _needsBuildUp,
                _isKilled,
                IsStopped,
                _configuredStartDelay,
                _startDelay,
                emitters,
                bytes);
        }

        internal void RestoreSnapshot(Snapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            if (snapshot.Emitters.Length != _emitters.Count)
                throw new InvalidOperationException("VFX snapshot emitter layout no longer matches the runtime definition.");

            CurrentTime = snapshot.CurrentTime;
            _initialRandomState = snapshot.InitialRandomState;
            _rng = VfxLtkRandom.FromState(snapshot.RandomState);
            _particleSerial = snapshot.ParticleSerial;
            _worldTransform = snapshot.WorldTransform;
            _orientationRootTransform = snapshot.OrientationRootTransform;
            _inverseWorldTransform = snapshot.InverseWorldTransform;
            _pendingOriginDelta = snapshot.PendingOriginDelta;
            _needsBuildUp = snapshot.NeedsBuildUp;
            _isKilled = snapshot.IsKilled;
            IsStopped = snapshot.IsStopped;
            _configuredStartDelay = snapshot.ConfiguredStartDelay;
            _startDelay = snapshot.StartDelay;

            int live = 0;
            for (int index = 0; index < _emitters.Count; index++)
            {
                EmitterState state = _emitters[index];
                EmitterSnapshot saved = snapshot.Emitters[index];
                state.BasePos = saved.BasePos;
                state.FieldBasePos = saved.FieldBasePos;
                state.SystemOrigin = saved.SystemOrigin;
                state.SystemTarget = saved.SystemTarget;
                state.PlacementRight = saved.PlacementRight;
                state.PlacementUp = saved.PlacementUp;
                state.PlacementForward = saved.PlacementForward;
                state.SharedRandom = saved.SharedRandom;
                state.SharedRandomRolled = saved.SharedRandomRolled;
                state.EmittedThrough = saved.EmittedThrough;
                state.Age = saved.Age;
                state.FinishedAt = saved.FinishedAt;
                state.BurstDone = saved.BurstDone;
                state.InitialEmissionDone = saved.InitialEmissionDone;
                state.TrailDistance = saved.TrailDistance;
                state.TrailSpawnedAt = saved.TrailSpawnedAt;
                state.NoiseLast = (float[])saved.NoiseLast.Clone();
                state.NoiseFired = (int[])saved.NoiseFired.Clone();
                state.Particles.Clear();
                state.Particles.AddRange(saved.Particles);
                state.ResetInstanceBuffer(saved.InstanceBufferLength);
                state.RenderTime = CurrentTime;
                live += state.Particles.Count;
            }
            LiveParticleCount = live;
        }

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
                es.PlacementRight = SafeNormal(Vector3.TransformNormal(Vector3.UnitX, placement), Vector3.UnitX);
                es.PlacementUp = SafeNormal(Vector3.TransformNormal(Vector3.UnitY, placement), Vector3.UnitY);
                es.PlacementForward = SafeNormal(Vector3.TransformNormal(Vector3.UnitZ, placement), Vector3.UnitZ);
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
            if (SurfacesEquals(_emissionSurfaces, surfaces)) return;
            _emissionSurfaces = surfaces;

            if (!replayCurrentTime || _definition is null || CurrentTime <= 0f) return;
            float targetTime = CurrentTime;
            Reset();
            Seek(targetTime);
        }

        private static bool SurfacesEquals(
            IReadOnlyDictionary<VfxEmitterDefinition, IVfxEmissionSurfaceSampler> a,
            IReadOnlyDictionary<VfxEmitterDefinition, IVfxEmissionSurfaceSampler> b)
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
                    PlacementRight = SafeNormal(Vector3.TransformNormal(Vector3.UnitX, worldTransform), Vector3.UnitX),
                    PlacementUp = SafeNormal(Vector3.TransformNormal(Vector3.UnitY, worldTransform), Vector3.UnitY),
                    PlacementForward = SafeNormal(Vector3.TransformNormal(Vector3.UnitZ, worldTransform), Vector3.UnitZ),
                };
                emitterState.BindOwner(this);
                _emitters.Add(emitterState);
            }
            Reset();
            SetTransform(worldTransform);
        }

        /// <summary>
        /// Repoints an already-running system at an edited definition when every pool emitter index
        /// still addresses the same authored emitter: birth values stay on the particles while
        /// appearance/integration reads the new model.
        /// </summary>
        internal bool TrySwapDefinition(VfxSystemDefinition next)
        {
            if (!AddressesSameEmitters(_definition, next)) return false;

            _definition = next;
            _dragMotion = next.DragMotion;
            _buildUpTime = MathF.Max(0f, next.BuildUpTime);
            foreach (EmitterState state in _emitters)
            {
                if ((uint)state.SourceOrder >= (uint)next.Emitters.Count) return false;
                state.Def = next.Emitters[state.SourceOrder];
                state.InvalidateInstances();
            }

            // Re-read emitter placement immediately without touching the particles already alive.
            SetTransform(_worldTransform, _orientationRootTransform);
            return true;
        }

        internal static bool AddressesSameEmitters(VfxSystemDefinition held, VfxSystemDefinition next)
        {
            if (held?.Emitters is null || next?.Emitters is null || held.Emitters.Count != next.Emitters.Count)
                return false;

            for (int index = 0; index < held.Emitters.Count; index++)
            {
                VfxEmitterDefinition before = held.Emitters[index];
                VfxEmitterDefinition after = next.Emitters[index];
                if (before is null || after is null ||
                    before.IsSimpleEmitter != after.IsSimpleEmitter ||
                    !string.Equals(before.Name, after.Name, StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return true;
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

        private static Matrix4x4 OrbitalTurn(Vector3 radians)
        {
            if (radians.X == 0f && radians.Y == 0f && radians.Z == 0f)
                return Matrix4x4.Identity;

            return Matrix4x4.CreateRotationZ(radians.Z) *
                   Matrix4x4.CreateRotationX(radians.X) *
                   Matrix4x4.CreateRotationY(radians.Y);
        }

        private static Matrix4x4 ParticleBasis(
            in Particle particle,
            EmitterState emitter,
            Vector3 direction,
            Matrix4x4 orbitalTurn,
            float legacyRoll)
        {
            if (emitter.Def.IsDirectionOriented &&
                emitter.Def.PrimitiveKind != VfxPrimitiveKind.Ray &&
                direction.LengthSquared() > 0f)
            {
                Vector3 up = Vector3.Normalize(direction);
                Vector3 axis = MathF.Abs(up.Y) < 0.99999f ? Vector3.UnitY : Vector3.UnitX;
                Vector3 right = SafeNormal(Vector3.Cross(axis, up), Vector3.UnitX);
                Vector3 forward = SafeNormal(Vector3.Cross(right, up), Vector3.UnitZ);
                return new Matrix4x4(
                    right.X, right.Y, right.Z, 0f,
                    up.X, up.Y, up.Z, 0f,
                    forward.X, forward.Y, forward.Z, 0f,
                    0f, 0f, 0f, 1f);
            }

            return StandingBasis(particle, emitter, orbitalTurn, legacyRoll);
        }

        /// <summary>
        /// The particle's own standing frame for geometry paths that deliberately ignore
        /// isDirectionOriented (arbitrary trails and arbitrary beams). It keeps the birth/current
        /// orientation choice, the particle's authored turn, and its orbital turn together.
        /// </summary>
        internal static Matrix4x4 ResolveStandingBasisForRender(EmitterState emitter, in Particle particle)
        {
            ArgumentNullException.ThrowIfNull(emitter);
            float particleT = ParticleAge01(particle.Age, particle.Life);
            float legacyRoll = emitter.Def.LegacyRotation?.Sample(particleT) * (MathF.PI / 180f) ?? 0f;
            Matrix4x4 orbitalTurn = OrbitalTurn(particle.BirthOrbitalVelocity * particle.Age);
            return StandingBasis(particle, emitter, orbitalTurn, legacyRoll);
        }

        private static Matrix4x4 StandingBasis(
            in Particle particle,
            EmitterState emitter,
            Matrix4x4 orbitalTurn,
            float legacyRoll)
        {
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
            if (orbitalTurn != Matrix4x4.Identity)
                basis *= orbitalTurn;
            return OrientationOnly(basis);
        }

        private ParticleLifecycleInfo LifecycleInfo(EmitterState state, in Particle particle, bool died)
        {
            float particleT = ParticleAge01(particle.Age, particle.Life);
            float emitterT = EmitterTime(state);
            Vector3 position = particle.Pos;
            Vector3 orbitalAngles = particle.BirthOrbitalVelocity * particle.Age;
            Matrix4x4 orbitalTurn = OrbitalTurn(orbitalAngles);
            if (orbitalTurn != Matrix4x4.Identity)
            {
                Vector3 origin = state.SystemOrigin;
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
            if (orbitalTurn != Matrix4x4.Identity)
                frame = OrientationOnly(frame * orbitalTurn);

            float particleTime = died && float.IsFinite(particle.Life) ? particle.Life : particle.Age;
            return new ParticleLifecycleInfo(
                position,
                basis,
                frame,
                particle.Serial,
                state.SourceOrder,
                particleTime,
                particle.Life,
                emitterT,
                died);
        }

        internal static float EmitterTime(EmitterState state)
        {
            VfxEmitterDefinition definition = state.Def;
            return definition.EmitterLifetime is > 0f
                ? Math.Clamp(state.Age / definition.EmitterLifetime.Value, 0f, 1f)
                : 0f;
        }

        internal static bool IsSimpleListEmitter(VfxEmitterDefinition definition)
            => definition?.IsSimpleEmitter == true;

        internal static float LingerSeconds(VfxEmitterDefinition definition)
        {
            float lifetime = IsSimpleListEmitter(definition) ? 0f : MathF.Max(0f, definition.ParticleLifetime.Constant);
            return MathF.Min(lifetime + 10f, MathF.Max(0f, definition.ParticleLinger));
        }

        internal static float StopWaitSeconds(VfxEmitterDefinition definition)
        {
            float lifetime = IsSimpleListEmitter(definition)
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

        private Vector3 EmitterFieldPosition(VfxEmitterDefinition definition, float emitterT)
        {
            Matrix4x4 world = definition.IsLocalOrientation ? _worldTransform : _orientationRootTransform;
            return Vector3.Transform(definition.EmitterPosition.Sample(emitterT), EmitterFieldTransform(definition, world));
        }

        private Matrix4x4 FieldLocalOrientation()
        {
            if (!Matrix4x4.Invert(_orientationRootTransform, out Matrix4x4 inverseRoot))
                return Matrix4x4.Identity;
            return OrientationOnly(_worldTransform * inverseRoot);
        }

        private static Matrix4x4 EmitterTransform(VfxEmitterDefinition definition, Matrix4x4 world)
        {
            Vector3 rotation = definition.RotationOverride.GetValueOrDefault() * (MathF.PI / 180f);
            return Matrix4x4.CreateTranslation(definition.TranslationOverride.GetValueOrDefault()) *
                EmitterFieldTransform(definition, world, rotation);
        }

        private static Matrix4x4 EmitterFieldTransform(VfxEmitterDefinition definition, Matrix4x4 world)
        {
            Vector3 rotation = definition.RotationOverride.GetValueOrDefault() * (MathF.PI / 180f);
            return EmitterFieldTransform(definition, world, rotation);
        }

        private static Matrix4x4 EmitterFieldTransform(VfxEmitterDefinition definition, Matrix4x4 world, Vector3 rotation)
            => Matrix4x4.CreateScale(definition.ScaleOverride ?? Vector3.One) *
               Matrix4x4.CreateRotationZ(rotation.Z) * Matrix4x4.CreateRotationX(rotation.X) *
               Matrix4x4.CreateRotationY(rotation.Y) * world;

        private Vector3 EmitterOdometerPosition(VfxEmitterDefinition definition, float emitterT)
        {
            Vector3 rotation = definition.RotationOverride.GetValueOrDefault() * (MathF.PI / 180f);
            Matrix4x4 world = definition.IsLocalOrientation ? _worldTransform : _orientationRootTransform;
            Matrix4x4 spawnFrame = Matrix4x4.CreateScale(definition.ScaleOverride ?? Vector3.One) *
                Matrix4x4.CreateRotationZ(rotation.Z) * Matrix4x4.CreateRotationX(rotation.X) *
                Matrix4x4.CreateRotationY(rotation.Y) * world;
            return Vector3.Transform(definition.EmitterPosition.Sample(emitterT), spawnFrame);
        }

        private void AdvanceTrailOdometer(EmitterState state, float emitterT)
        {
            Vector3 spawnedAt = EmitterOdometerPosition(state.Def, emitterT);
            if (state.TrailSpawnedAt is Vector3 previous)
                state.TrailDistance += Vector3.Distance(previous, spawnedAt);
            state.TrailSpawnedAt = spawnedAt;
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
            StepEmitters(dt, systemDelta);
        }

        private void StepEmitters(float dt, Vector3 systemDelta)
        {
            // LTK integrates and retires the entire shared pool before any emitter is
            // allowed to consume slots for newborns. Keep the per-emitter storage, but
            // preserve that system-wide phase ordering.
            EnsureStepScratch();
            int availableParticleSlots = _particleCapacity;
            for (int index = 0; index < _emitters.Count; index++)
            {
                EmitterState emitter = _emitters[index];
                _stepContexts[index] = IntegrateEmitter(emitter, dt, systemDelta);
                availableParticleSlots -= emitter.Particles.Count;
            }
            availableParticleSlots = Math.Max(0, availableParticleSlots);

            Array.Fill(_newbornStarts, -1);
            for (int index = 0; index < _emitters.Count; index++)
            {
                _newbornStarts[index] = EmitEmitter(
                    _emitters[index],
                    _stepContexts[index],
                    ref availableParticleSlots);
            }

            int live = 0;
            for (int index = 0; index < _emitters.Count; index++)
            {
                EmitterState state = _emitters[index];
                if (_newbornStarts[index] >= 0)
                {
                    ApplyFieldsToNewborns(
                        state.Def.Fields,
                        state,
                        _stepContexts[index].EmitterT,
                        _stepContexts[index].PreparedNoise,
                        _stepContexts[index].FieldOrigin,
                        _newbornStarts[index]);
                }
                state.RenderTime = CurrentTime;
                state.InvalidateInstances();
                live += state.Particles.Count;
            }
            LiveParticleCount = live;
        }
        private void EnsureStepScratch()
        {
            int count = _emitters.Count;
            if (_stepContexts.Length != count)
                _stepContexts = new EmitterStepContext[count];
            if (_newbornStarts.Length != count)
                _newbornStarts = new int[count];
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

        private EmitterStepContext IntegrateEmitter(EmitterState s, float dt, Vector3 systemDelta)
        {
            var d = s.Def;
            float previousEmitterT = EmitterTime(s);
            Vector3 previousEmitterPosition = d.EmitterPosition.Sample(previousEmitterT);
            s.Age += dt;

            SettleEmitter(s, dt);
            float emitterT = EmitterTime(s);
            Vector3 emitterPosition = d.EmitterPosition.Sample(emitterT);
            Vector3 emitterPositionDelta = emitterPosition - previousEmitterPosition;
            Vector3 previousFieldBasePos = s.FieldBasePos;
            // LTK rides emitter-space fields on EmitterPosition under the emitter's basis, but
            // translationOverride belongs to the spawn frame only and must not move field centres.
            Vector3 fieldOrigin = d.IsEmitterSpace
                ? previousFieldBasePos - systemDelta
                : s.SystemOrigin - systemDelta;
            Matrix4x4 placement = EmitterPlacement(d);
            s.PlacementTransform = placement;
            s.BasePos = Vector3.Transform(emitterPosition, placement);
            s.FieldBasePos = EmitterFieldPosition(d, emitterT);

            PreparedNoiseField[] preparedNoise = PrepareNoiseFields(d.Fields, s, emitterT, CurrentTime, fieldOrigin);

            // Existing particles are integrated before this step's births. Riot spawns new
            // particles with a zero-sized birth step, so they remain exactly at their birth
            // transform until the following simulation step.
            for (int i = s.Particles.Count - 1; i >= 0; i--)
            {
                var p = s.Particles[i];
                Vector3 positionBeforeStep = p.Pos;

                p.Age += dt;
                if (p.Age >= p.Life)
                {
                    ParticleLifecycle?.Invoke(this, d, LifecycleInfo(s, p, died: true));
                    // LTK retires from its packed pool by moving the final live particle into
                    // the freed slot. Keep the same O(1) retirement here; renderers that need
                    // birth order (trails) reconstruct it from the particle serial.
                    int last = s.Particles.Count - 1;
                    if (i != last)
                        s.Particles[i] = s.Particles[last];
                    s.Particles.RemoveAt(last);
                    continue;
                }

                float particleT = ParticleAge01(p.Age, p.Life);
                Vector3 kinematicShift = Vector3.Zero;
                if (d.IsEmitterSpace && emitterPositionDelta != Vector3.Zero)
                    kinematicShift += Vector3.TransformNormal(emitterPositionDelta, p.BirthFrame);
                if (d.BindWeight is { } bindWeight && systemDelta != Vector3.Zero)
                {
                    // LTK forwards the authored bind value verbatim. Values outside 0..1
                    // intentionally over-/counter-follow the moving system origin.
                    float bind = bindWeight.Sample(emitterT);
                    if (bind != 0f) kinematicShift += systemDelta * bind;
                }

                float lingerT = LingerProgress(s);
                Vector3 acceleration = s.FinishedAt >= 0f && d.Linger?.Acceleration is { } lingerAcceleration
                    ? lingerAcceleration.Sample(lingerT)
                    : d.AccelerationOverLife?.Sample(emitterT) ?? Vector3.Zero;
                acceleration = Vector3.TransformNormal(acceleration, p.BirthFrame);
                // LTK does not carry a per-particle birthAcceleration term. Only the
                // emitter's acceleration (or keyed linger replacement) is integrated.
                p.Vel += acceleration * dt;

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

                // Force fields read the particle where the step began. Bind/root travel and the
                // emitter-space EmitterPosition shift are added only after the field pass, as in LTK.
                ApplyFields(d.Fields, s, emitterT, preparedNoise, fieldOrigin, p.Pos, p.Serial, dt, ref moving, ref p.Vel);
                p.Pos += moving * dt + kinematicShift;

                // Birth angular velocity and acceleration always integrate. rotation0 is
                // a separate integrated value authored per 1/60 second and is gated only
                // by isRotationEnabled.
                p.BirthRotation += (p.RotationalVelocity + p.RotationalAcceleration * p.Age) * dt;
                if (d.IsRotationEnabled && d.RotationOverLife is { } rotationCurve)
                {
                    Vector3 rotationRate = rotationCurve.Sample(particleT);
                    if (s.FinishedAt >= 0f && d.Linger?.Rotation is { } lingerRotation)
                    {
                        // LTK samples LingerRotation on the particle's rewritten age01,
                        // unlike linger acceleration/velocity/drag which use emitter linger progress.
                        rotationRate = lingerRotation.Sample(particleT);
                    }
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
                p.Travel = dt > 0f ? displacement / dt : Vector3.Zero;
                s.Particles[i] = p;
                if (d.ChildParticleSet is { Children.Count: > 0 })
                    ParticleUpdated?.Invoke(this, d, LifecycleInfo(s, p, died: false));
            }

            return new EmitterStepContext(emitterT, preparedNoise, fieldOrigin);
        }

        private int EmitEmitter(
            EmitterState s,
            EmitterStepContext context,
            ref int availableParticleSlots)
        {
            VfxEmitterDefinition d = s.Def;
            float emitterT = context.EmitterT;
            bool emitting = !d.Disabled
                            && !IsStopped
                            && s.Age >= d.TimeBeforeFirstEmission
                            && (d.EmitterLifetime is not { } life || s.Age <= life);
            if (!emitting || (d.IsSingleParticle && s.BurstDone)) return -1;
            {
                // LTK samples rate directly. Legacy rateIsPeriod is retained in the model
                // for inspection but does not reinterpret the simulation rate.
                float rate = MathF.Max(0f, d.Rate.Sample(emitterT));
                if (!float.IsFinite(rate)) rate = 0f;
                float owed = MathF.Min(
                    MathF.Truncate(MathF.Max(0f, s.Age - s.EmittedThrough) * rate),
                    MathF.Truncate(rate * 0.33f) + 1f);
                int requestedCount = owed >= int.MaxValue ? int.MaxValue : (int)MathF.Max(0f, owed);
                if (!s.InitialEmissionDone)
                {
                    if (d.IsSingleParticle)
                    {
                        // Riot stores the authored burst count through a uint16 lane, so
                        // values above 65535 wrap rather than clamp.
                        int wrapped = (int)(Math.Truncate((double)rate) % (ushort.MaxValue + 1d));
                        requestedCount = Math.Max(1, wrapped);
                    }
                    else
                    {
                        requestedCount = Math.Max(1, requestedCount);
                    }
                }
                if (d.Trail?.MaxAddedPerFrame is > 0)
                    requestedCount = Math.Min(requestedCount, d.Trail.MaxAddedPerFrame);
                if (requestedCount <= 0) return -1;

                // LTK advances a trail odometer only when an emission batch is actually due,
                // before attempting the shared-pool spawn. TranslationOverride and shape offset
                // do not participate; only EmitterPosition stood on the spawn frame does.
                AdvanceTrailOdometer(s, emitterT);

                // LTK advances the emitter by the requested batch even if its shared pool
                // fills partway through. Preserve that debt semantics instead of retrying
                // dropped births on a later frame.
                if (d.ParticlesShareRandomValue && !s.SharedRandomRolled)
                {
                    s.SharedRandom = _rng.NextUnitFloat();
                    s.SharedRandomRolled = true;
                }
                int actualCount = Math.Min(requestedCount, availableParticleSlots);
                availableParticleSlots -= actualCount;
                int firstNewborn = s.Particles.Count;
                for (int born = 0; born < actualCount; born++) Spawn(s, emitterT);

                if (actualCount < requestedCount)
                {
                    // emit.ts draws the failed particle's roll (and its per-particle chance)
                    // before pool.spawn reports a full pool, then breaks the batch.
                    _rng.NextUnitFloat();
                    if (!d.ParticlesShareRandomValue) _rng.NextUnitFloat();
                }

                s.InitialEmissionDone = true;
                s.BurstDone = d.IsSingleParticle;
                s.EmittedThrough = rate > 0f ? s.EmittedThrough + requestedCount / rate : s.Age;
                return actualCount > 0 ? firstNewborn : -1;
            }
        }

        private readonly record struct EmitterStepContext(
            float EmitterT,
            PreparedNoiseField[] PreparedNoise,
            Vector3 FieldOrigin);

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
            // The ordinary chance is drawn even while inspection pins a replacement value. This
            // keeps every later RNG draw on the same stream as the unpinned run.
            float drawnChance = d.ParticlesShareRandomValue ? s.SharedRandom : _rng.NextUnitFloat();
            float sharedRoll = _pinnedBirthChance ?? drawnChance;

            float life = d.ParticleLifetime.SampleBirth(emitterT, _rng, sharedRoll);
            var birthScale = d.BirthScale.SampleBirthOver(emitterT, _rng, Vector3.One, sharedRoll);
            if (d.LegacyBirthScale is { } legacyBirthScale)
            {
                float scalar = legacyBirthScale.SampleBirth(emitterT, _rng, sharedRoll);
                Vector2 bias = d.LegacyScaleBias ?? Vector2.One;
                birthScale = new Vector3(scalar * bias.X, scalar * bias.Y, scalar);
            }
            // isUniformScale promotes the first authored component to every axis for all
            // particle kinds, not only mesh primitives.
            if (d.IsUniformScale)
                birthScale = new Vector3(birthScale.X);
            var vel = d.BirthVelocity?.SampleBirth(emitterT, _rng, sharedRoll) ?? Vector3.Zero;
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
                ? shape.SampleOffset(_rng, emitterT, sharedRoll, out spawnRotation)
                : Vector3.Zero;

            bool onEmissionSurface = false;
            VfxSurfaceBirth surfaceBirth = default;
            if (d.EmissionSurface is not null &&
                _emissionSurfaces.TryGetValue(d, out IVfxEmissionSurfaceSampler surfaceSampler))
            {
                onEmissionSurface = surfaceSampler.TrySample(s.Age, _rng, out surfaceBirth);
                if (onEmissionSurface)
                    localOffset += surfaceBirth.Position;
            }

            if (onEmissionSurface && d.EmissionSurface.UseNormal)
                vel = surfaceBirth.Normal * vel.Length();

            Matrix4x4 placement = EmitterPlacement(d);
            var worldOffset = Vector3.TransformNormal(localOffset, placement);
            vel = Vector3.TransformNormal(vel, spawnRotation);
            vel = Vector3.TransformNormal(vel, placement);
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
                RangeRandom = roll,
                StartFrame = d.RandomStartFrame && d.NumFrames > 1 ? roll * d.NumFrames : 0f,
                FrameRate = (d.FrameRate ?? 0f) *
                    (d.BirthFrameRate?.SampleBirth(emitterT, _rng, sharedRoll) ?? 1f),
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

        private void EnsureInstances(EmitterState state, int requestedCount)
        {
            int wanted = Math.Clamp(requestedCount, 0, state.Particles.Count);
            if (!state.InstancesDirty && state.PreparedInstanceCount >= wanted)
                return;
            BuildInstances(state, wanted);
        }

        private void BuildInstances(EmitterState s, int maxCount)
        {
            var d = s.Def;
            int n = Math.Min(s.Particles.Count, Math.Max(0, maxCount));
            int requiredLength = n * InstanceStride;
            if (s.InstanceBufferCapacity < requiredLength)
            {
                int currentParticleCapacity = Math.Max(4, s.InstanceBufferCapacity / InstanceStride);
                int nextParticleCapacity = currentParticleCapacity;
                while (nextParticleCapacity < n)
                {
                    int doubled = nextParticleCapacity * 2;
                    nextParticleCapacity = Math.Min(_particleCapacity, Math.Max(n, doubled));
                    if (nextParticleCapacity >= n) break;
                }
                s.ResetInstanceBuffer(nextParticleCapacity * InstanceStride);
            }
            var buf = s.RawInstances;
            float emitterT = EmitterTime(s);
            int k = 0;
            for (int i = 0; i < n; i++)
            {
                var p = s.Particles[i];
                float t = ParticleAge01(p.Age, p.Life);
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
                    ? lingerScale.SampleOver(particleLingerT, Vector3.One)
                    : d.ScaleOverLife?.SampleOver(t, Vector3.One) ?? Vector3.One;
                if (d.IsUniformScale)
                    scaleMul = new Vector3(scaleMul.X);
                Vector4 col = lingering && d.Linger?.Color is { } lingerColor
                    ? p.BirthColor * lingerColor.SampleOver(particleLingerT, Vector4.One)
                    : VfxColorSemantics.ResolveParticle(p.BirthColor, d.ColorOverLife, t);
                col = VfxColorSemantics.PremultiplyForAddOrSubtract(
                    col,
                    d.BlendMode,
                    d.DrawsAsDistortion,
                    d.HasResolvedCustomMaterial);

                // League keeps one logical flipbook counter for both texture layers. Each
                // sampler wraps that counter against its own texDiv grid at draw time, so do
                // not collapse it to the base grid here (texDivMult may be different).
                int authoredFrames = Math.Max(1, d.NumFrames);
                float playedFrame = PositiveModulo(p.StartFrame + p.Age * p.FrameRate, authoredFrames);
                float frame = MathF.Floor(d.StartFrame + playedFrame);

                Vector3 position = p.Pos;
                Vector3 orbitalAngles = p.BirthOrbitalVelocity * p.Age;
                Matrix4x4 orbitalTurn = OrbitalTurn(orbitalAngles);
                if (orbitalTurn != Matrix4x4.Identity)
                {
                    Vector3 origin = new(_worldTransform.M41, _worldTransform.M42, _worldTransform.M43);
                    position = origin + Vector3.Transform(position - origin, orbitalTurn);
                }
                if (d.Acceleration is { } worldAcceleration && float.IsFinite(p.Life))
                {
                    float reached = t * p.Life * p.Life;
                    position += worldAcceleration.Sample(emitterT) * reached;
                }
                float sizeX = p.BirthSize.X * scaleMul.X;
                float sizeY = p.BirthSize.Y * scaleMul.Y;
                float sizeZ = p.BirthSize.Z * scaleMul.Z;
                if (d.PrimitiveKind == VfxPrimitiveKind.ArbitraryQuad)
                {
                    sizeX *= 2f;
                    sizeY *= 2f;
                    if (d.IsGroundLayer && d.IsUniformScale)
                        sizeY = sizeX;
                }
                Vector3 direction = p.Travel;

                // LTK stretchOf() reaches only direction-oriented quads and ordinary meshes.
                // LegacySimple and rays explicitly keep a factor of one; meshes stretch local +Z,
                // while quads stretch their authored long/local Y axis.
                bool canDirectionStretch =
                    d.IsDirectionOriented &&
                    d.PrimitiveKind != VfxPrimitiveKind.Ray &&
                    d.AuthoredFeatures?.HasLegacySimple != true &&
                    (d.DrawsAsQuad || d.PrimitiveKind == VfxPrimitiveKind.Mesh) &&
                    direction.LengthSquared() > 0f;
                if (canDirectionStretch)
                {
                    float stretch = MathF.Max(d.DirectionVelocityMinScale, direction.Length() * d.DirectionVelocityScale);
                    if (d.PrimitiveKind == VfxPrimitiveKind.Mesh) sizeZ *= stretch;
                    else sizeY *= stretch;
                }
                buf[k++] = position.X; buf[k++] = position.Y; buf[k++] = position.Z;
                buf[k++] = sizeX;
                buf[k++] = sizeY;
                buf[k++] = col.X; buf[k++] = col.Y; buf[k++] = col.Z; buf[k++] = col.W;
                int rotationSlot = k++;
                buf[k++] = frame;
                buf[k++] = t;
                buf[k++] = direction.X; buf[k++] = direction.Y; buf[k++] = direction.Z;
                Vector3 currentRotation = p.BirthRotation;
                float legacyRollDegrees = d.LegacyRotation?.Sample(t) ?? 0f;
                float legacyRoll = legacyRollDegrees * (MathF.PI / 180f);
                if (d.AuthoredFeatures?.HasLegacySimple == true)
                {
                    float spinDegrees = currentRotation.Z * (180f / MathF.PI) + legacyRollDegrees;
                    spinDegrees = ((MathF.Truncate(spinDegrees) % 360f) + 360f) % 360f;
                    buf[rotationSlot] = spinDegrees * (MathF.PI / 180f);
                }
                else
                {
                    buf[rotationSlot] = currentRotation.X;
                }
                bool authoredPlane = d.IsArbitraryQuad || d.PrimitiveKind is
                    VfxPrimitiveKind.ArbitraryTrail or VfxPrimitiveKind.PlanarProjection;
                if (d.IsDirectionOriented && !authoredPlane && direction.LengthSquared() > 0f)
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
                buf[k++] = sizeZ;
                VfxEmitterRenderState renderState = d.RenderState ?? VfxEmitterRenderState.Default;
                Vector2 uvRamp = VfxUvSemantics.BirthRamp(
                    p.BirthUvOffset + p.BirthUvScrollRate * p.Age,
                    renderState.ClampUvScroll);
                Vector2 uvOffset = VfxUvSemantics.Periodic(
                    uvRamp + p.IntegratedUvOffset,
                    renderState.TextureAddressMode);
                Vector2 uvScale = d.UvScale?.SampleOver(t, Vector2.One) ?? Vector2.One;
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
                Vector2 textureMultRamp = VfxUvSemantics.BirthRamp(
                    p.TextureMultBirthUvOffset + p.TextureMultBirthUvScrollRate * p.Age,
                    d.TextureMultClampUvScroll);
                Vector2 textureMultUvOffset = VfxUvSemantics.Periodic(
                    textureMultRamp + p.IntegratedTextureMultUvOffset,
                    d.TextureMultAddressMode);
                Vector2 textureMultUvScale = d.TextureMultUvScale?.SampleOver(t, Vector2.One) ?? Vector2.One;

                float textureMultUvRotationDegrees = (d.TextureMultUvRotation?.Sample(t) ?? 0f)
                    + p.TextureMultBirthUvRotateRate * p.Age
                    + p.IntegratedTextureMultUvRotation;
                buf[k++] = textureMultUvOffset.X; buf[k++] = textureMultUvOffset.Y;
                buf[k++] = textureMultUvScale.X; buf[k++] = textureMultUvScale.Y;
                buf[k++] = textureMultUvRotationDegrees * (MathF.PI / 180f);
                // Birth random drives ColorLookUpType=3 exactly like LTK's pool.roll.
                buf[k++] = p.RangeRandom;
                // Palette selection is an emitter uniform sampled at t=0; keep this final lane
                // as padding so basis attributes remain at offsets 36/39/42.
                buf[k++] = 0f;

                Matrix4x4 basis = ParticleBasis(p, s, direction, orbitalTurn, legacyRoll);
                Vector3 basisX = SafeNormal(Vector3.TransformNormal(Vector3.UnitX, basis), Vector3.UnitX);
                Vector3 basisY = SafeNormal(Vector3.TransformNormal(Vector3.UnitY, basis), Vector3.UnitY);
                Vector3 basisZ = SafeNormal(Vector3.TransformNormal(Vector3.UnitZ, basis), Vector3.UnitZ);
                buf[k++] = basisX.X; buf[k++] = basisX.Y; buf[k++] = basisX.Z;
                buf[k++] = basisY.X; buf[k++] = basisY.Y; buf[k++] = basisY.Z;
                buf[k++] = basisZ.X; buf[k++] = basisZ.Y; buf[k++] = basisZ.Z;
            }
            s.MarkInstancesPrepared(k / InstanceStride);
        }

        private static float ParticleAge01(float age, float lifetime)
            => lifetime > 0f ? Math.Clamp(age / lifetime, 0f, 1f) : 1f;

        private static float PositiveModulo(float value, float span)
        {
            if (span <= 0f) return 0f;
            float wrapped = value % span;
            return wrapped < 0f ? wrapped + span : wrapped;
        }

        internal readonly record struct PreparedNoiseField(
            Vector3 Center,
            float Radius,
            float Delta,
            Vector3 Axes,
            int First,
            int Kicks,
            int Slot);

        private static PreparedNoiseField[] PrepareNoiseFields(
            VfxFieldCollectionDefinition fields,
            EmitterState state,
            float emitterT,
            float now,
            Vector3 fieldOrigin)
        {
            int noiseCount = fields?.Noise?.Count ?? 0;
            if (noiseCount == 0) return Array.Empty<PreparedNoiseField>();

            if (state.NoiseLast.Length != noiseCount)
            {
                state.NoiseLast = new float[noiseCount];
                Array.Fill(state.NoiseLast, float.NaN);
                state.NoiseFired = new int[noiseCount];
            }

            if (state.PreparedNoise.Length != noiseCount)
                state.PreparedNoise = new PreparedNoiseField[noiseCount];
            PreparedNoiseField[] prepared = state.PreparedNoise;
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
                prepared[slot] = new PreparedNoiseField(
                    fieldOrigin + field.Position.Sample(emitterT),
                    field.Radius.Sample(emitterT),
                    field.VelocityDelta.Sample(emitterT),
                    field.AxisFraction,
                    first,
                    kicks,
                    slot);
            }

            return prepared;
        }

        private void ApplyFieldsToNewborns(
            VfxFieldCollectionDefinition fields,
            EmitterState state,
            float emitterT,
            PreparedNoiseField[] preparedNoise,
            Vector3 fieldOrigin,
            int firstNewborn)
        {
            if (fields is null || firstNewborn >= state.Particles.Count) return;

            for (int index = firstNewborn; index < state.Particles.Count; index++)
            {
                Particle particle = state.Particles[index];
                Vector3 drift = state.Def.VelocityOverLife?.Sample(emitterT) ?? Vector3.Zero;
                drift = Vector3.TransformNormal(drift, particle.BirthFrame);
                Vector3 moving = particle.Vel + drift;
                Vector3 kept = particle.Vel;

                // Riot runs the field pass on a newborn with dt=0. Only the unscaled noise
                // impulses and orbital turn can change that birth step, and the delta persists.
                ApplyFields(fields, state, emitterT, preparedNoise, fieldOrigin, particle.Pos, particle.Serial, 0f, ref moving, ref kept);
                particle.Vel = kept;
                state.Particles[index] = particle;
            }
        }

        private void ApplyFields(
            VfxFieldCollectionDefinition fields,
            EmitterState state,
            float emitterT,
            PreparedNoiseField[] preparedNoise,
            Vector3 fieldOrigin,
            Vector3 particlePosition,
            uint serial,
            float dt,
            ref Vector3 moving,
            ref Vector3 kept)
        {
            if (fields is null) return;

            Vector3 before = moving;
            Matrix4x4 localOrientation = FieldLocalOrientation();

            // Riot samples every field at emitter life, not particle life, and applies
            // fields after the particle's own drag in this exact order. Acceleration fields
            // are pre-summed before the one dt multiplication performed by the engine.
            Vector3 accelerationFields = Vector3.Zero;
            foreach (VfxAccelerationField field in fields.Acceleration)
            {
                Vector3 value = field.Acceleration.Sample(emitterT);
                if (field.LocalSpace && state.Def.IsLocalOrientation)
                    value = Vector3.TransformNormal(value, localOrientation);
                accelerationFields += value;
            }
            moving += accelerationFields * dt;

            foreach (VfxAttractionField field in fields.Attraction)
            {
                Vector3 center = fieldOrigin + field.Position.Sample(emitterT);
                Vector3 delta = center - particlePosition;
                float radius = field.Radius.Sample(emitterT);
                float reach = delta.LengthSquared();
                if (reach > radius * radius) continue;
                float strength = field.Acceleration.Sample(emitterT);
                float scale = strength * dt / MathF.Sqrt(MathF.Max(reach, 1f));
                moving += delta * scale;
            }

            foreach (PreparedNoiseField noise in preparedNoise)
            {
                if (noise.Kicks == 0 || Vector3.DistanceSquared(particlePosition, noise.Center) > noise.Radius * noise.Radius)
                    continue;

                for (int kick = 0; kick < noise.Kicks; kick++)
                {
                    Vector3 direction = NoiseDirection(serial, noise.Slot, noise.First + kick);
                    moving += direction * noise.Axes * noise.Delta;
                }
            }

            foreach (VfxDragField field in fields.Drag)
            {
                Vector3 center = fieldOrigin + field.Position.Sample(emitterT);
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
                if (!TryNormalizeExact(axis, out double axisX, out double axisY, out double axisZ)) continue;
                ApplyOrbitalField(ref moving, particlePosition, fieldOrigin, axisX, axisY, axisZ);
            }

            // Field deltas persist in the particle velocity just as LTK's pushInto adds
            // MOVING-PUSHED into KEPT.
            kept += moving - before;
        }

        private static void ApplyOrbitalField(
            ref Vector3 velocity,
            Vector3 particle,
            Vector3 center,
            double axisX,
            double axisY,
            double axisZ)
        {
            double along = velocity.X * axisX + velocity.Y * axisY + velocity.Z * axisZ;
            double px = velocity.X - along * axisX;
            double py = velocity.Y - along * axisY;
            double pz = velocity.Z - along * axisZ;
            double speed = Math.Sqrt(px * px + py * py + pz * pz);
            if (speed <= 0.001d) return;

            double dx = (double)particle.X - center.X;
            double dy = (double)particle.Y - center.Y;
            double dz = (double)particle.Z - center.Z;
            double reach = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (reach == 0d) return;
            dx /= reach;
            dy /= reach;
            dz /= reach;
            if (Math.Abs(1d - (axisX * dx + axisY * dy + axisZ * dz)) <= 1e-4d) return;

            double tx = dy * axisZ - dz * axisY;
            double ty = dz * axisX - dx * axisZ;
            double tz = dx * axisY - dy * axisX;
            double tangentLength = Math.Sqrt(tx * tx + ty * ty + tz * tz);
            if (tangentLength == 0d) return;
            double scale = speed / tangentLength;
            if (tx * px + ty * py + tz * pz < 0d) scale = -scale;
            velocity = new Vector3(
                (float)(along * axisX + tx * scale),
                (float)(along * axisY + ty * scale),
                (float)(along * axisZ + tz * scale));
        }

        private static bool TryNormalizeExact(
            Vector3 value,
            out double x,
            out double y,
            out double z)
        {
            double squared = (double)value.X * value.X + (double)value.Y * value.Y + (double)value.Z * value.Z;
            if (squared == 0d)
            {
                x = 0d;
                y = 0d;
                z = 0d;
                return false;
            }

            double inverse = 1d / Math.Sqrt(squared);
            x = value.X * inverse;
            y = value.Y * inverse;
            z = value.Z * inverse;
            return true;
        }

        private static Vector3 NoiseDirection(uint serial, int slot, int impulse)
        {
            uint first = Mix32(Mix32(serial + 1u) ^ Mix32(unchecked((uint)((slot + 1) * (int)0x9e3779b1 + impulse))));
            uint second = Mix32(first ^ 0x68e31da4u);
            uint third = Mix32(second ^ 0x1b56c4e9u);
            const double span = 4294967296.0;
            float x = (float)(first / span * 2.0 - 1.0);
            float y = (float)(second / span * 2.0 - 1.0);
            float z = (float)(third / span * 2.0 - 1.0);
            double squared = (double)x * x + (double)y * y + (double)z * z;
            if (squared < 1e-12d) return new Vector3(x, y, z);

            double inverse = 1d / Math.Sqrt(squared);
            return new Vector3(
                (float)(x * inverse),
                (float)(y * inverse),
                (float)(z * inverse));
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
