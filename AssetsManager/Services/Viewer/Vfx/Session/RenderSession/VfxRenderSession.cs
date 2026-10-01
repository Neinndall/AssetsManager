using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Core;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Vfx.Session
{
    /// <summary>
    /// UI-facing playback adapter for the authored VFX graph runtime.
    /// Resource decoding stays on the loader side and all GL uploads happen on
    /// the active viewport context during Render.
    /// </summary>
    public sealed partial class VfxRenderSession : IDisposable, IPreparedParticlePass
    {
        // LTK creates each Animation Clip particle cue and each idle effect with fixed,
        // independent deterministic driver seeds.
        internal const int AnimationClipCueSeed = 7331;
        internal const int IdleEffectSeed = 1337;
        private readonly LogService _logService;
        private readonly VfxLoadingService _loadingService;
        private readonly bool _ownsLoadingService;
        private readonly VfxGpuResourceUploader _gpuResourceUploader = new();
        private readonly TimeProvider _timeProvider;
        private bool _waitForInitialResources;
        private long? _initialResourceWaitStarted;
        internal static readonly TimeSpan InitialResourceWaitLimit = TimeSpan.FromSeconds(4);
        private VfxOpenGlRenderer _renderer;
        private VfxPlaybackGraphRuntime _graph;
        private bool _purgeGpuResourcesBeforeNextFrame;
        private sealed class GraphAttachmentInfo
        {
            public string BoneName { get; set; }
            public uint BoneHash { get; set; }
            public string TargetBoneName { get; set; }
            public uint TargetBoneHash { get; set; }
            public Vector3 LocalOffset { get; set; }
            public Matrix4x4 BaseTransform { get; set; } = Matrix4x4.Identity;
            public Matrix4x4 BoneTransform { get; set; } = Matrix4x4.Identity;
            public bool HasBoneTransform { get; set; }
            public uint EffectKey { get; set; }
            public double StartTime { get; set; }
            public bool IsDetachable { get; set; }
            public bool IsIdleEffect { get; set; }
        }

        private sealed record AttachmentSnapshot(bool HasBoneTransform, Matrix4x4 BoneTransform);
        private sealed record SessionSnapshot(
            double Time,
            Vector3? LastRigOrigin,
            Matrix4x4[] Placements,
            VfxPlaybackGraphRuntime.Snapshot[] Graphs,
            AttachmentSnapshot[] Attachments,
            long Bytes);
        private sealed record Checkpoint(int Mark, SessionSnapshot State)
        {
            public double Time => State.Time;
            public long Bytes => State.Bytes;
        }

        // LTK decision 2.46: one checkpoint mark per quarter second, at most 60 seconds,
        // bounded so heavy particle systems automatically keep a wider stride.
        private const double SeekStep = 1d / 60d;
        private const double MaximumSeekTime = 60d;
        private const double CheckpointInterval = 0.25d;
        private const int MaximumCheckpointMarks = 240;
        private const long CheckpointBudgetBytes = 256L * 1024L * 1024L;
        private readonly Dictionary<int, Checkpoint> _checkpoints = new();
        private long _checkpointBytes;
        internal int CheckpointCount => _checkpoints.Count;
        internal long CheckpointBytes => _checkpointBytes;
        internal double LastSeekRestoreTime { get; private set; }

        private readonly List<VfxPlaybackGraphRuntime> _graphs = new();
        private readonly List<IReadOnlyList<VfxPlaybackRuntime.EmitterState>> _renderSources = new();
        private readonly List<VfxRenderQueueEntry> _renderQueue = new();
        private readonly List<VfxRenderQueueEntry> _shadedRenderQueue = new();
        private readonly List<VfxRenderQueueEntry> _distortionRenderQueue = new();
        private readonly Dictionary<object, int> _renderGraphOrders = new();
        private readonly Dictionary<VfxPlaybackGraphRuntime, Matrix4x4> _graphPlacements = new();
        private readonly List<(double Time, uint EffectKey)> _scheduledEffectKills = new();
        private readonly Dictionary<VfxPlaybackGraphRuntime, double> _graphStopTimes = new();
        private readonly Dictionary<VfxPlaybackGraphRuntime, GraphAttachmentInfo> _graphAttachments = new();
        private readonly Dictionary<VfxPlaybackGraphRuntime, VfxSpellPlaybackStep> _spellSteps = new();
        private VfxSystemModel _activeSystem;
        private VfxRigSettings _rigSettings = VfxRigSettings.ForPreset(VfxRigPreset.Still);
        private double _rigDuration;
        private Vector3? _lastRigOrigin;
        private Matrix4x4 _worldTransform = Matrix4x4.Identity;
        private VfxOwnerSceneContext _ownerSceneContext;
        private float? _pinnedBirthChance;
        private bool _isPlaying;
        private bool _usesStandaloneRig;
        private bool _ready;
        private bool _disposed;
        private uint _viewportWidth;
        private uint _viewportHeight;
        private Matrix4x4 _preparedViewProjection;
        private Matrix4x4 _preparedView;
        private bool _preparedShaded;
        private bool _preparedWireframe;
        private float _preparedWireOpacity;
        private Func<string, uint, Matrix4x4?> _boneTransformProvider;
        private Func<double, string, uint, Matrix4x4?> _boneTransformSampler;

        public VfxRenderSession(
            LogService logService = null,
            VfxLoadingService loadingService = null,
            TimeProvider timeProvider = null)
        {
            _timeProvider = timeProvider ?? TimeProvider.System;
            _logService = logService;
            _loadingService = loadingService ?? new VfxLoadingService();
            _ownsLoadingService = loadingService is null;
        }

        internal int LiveParticleCount
        {
            get
            {
                int count = 0;
                foreach (VfxPlaybackGraphRuntime graph in _graphs)
                {
                    foreach (VfxPlaybackRuntime runtime in graph.Runtimes)
                        count += runtime.LiveParticleCount;
                }
                return count;
            }
        }

        internal int LiveChildSystemCount
            => _graphs.Sum(static graph => graph.LiveChildSystemCount);

        /// <summary>
        /// A session can be composed before the viewport has a GL context; the renderer is created by
        /// Initialize on the render callback before its first frame.
        /// </summary>
        internal bool IsInitialized => _ready;

        public VfxSystemModel ActiveSystem => _activeSystem;
        public IReadOnlyList<VfxPlaybackGraphRuntime> Graphs => _graphs;
        public float? PinnedBirthChance => _pinnedBirthChance;
        public double CurrentTime => _activeSystem?.CurrentTime ?? 0d;
        public double RigDuration => _activeSystem?.Definition is null ? _activeSystem?.TotalDuration ?? 0d : _rigDuration;

        public double PlaybackTime
        {
            get
            {
                double time = _activeSystem?.CurrentTime ?? 0d;
                if (!_usesStandaloneRig ||
                    !_rigSettings.IsLooping ||
                    !(RigDuration > 0d) ||
                    !double.IsFinite(RigDuration))
                {
                    return time;
                }

                double phase = time % RigDuration;
                return phase < 0d ? phase + RigDuration : phase;
            }
        }

        private bool HasFinitePlaybackDuration => double.IsFinite(RigDuration) && RigDuration > 0d;

        public VfxRigPreset RigPreset
        {
            get => _rigSettings.Preset;
            set => RigSettings = _rigSettings.WithPreset(value);
        }

        public VfxRigSettings RigSettings
        {
            get => _rigSettings;
            set
            {
                VfxRigMotionKind previousMotion = _rigSettings.MotionKind;
                _rigSettings = value;
                ClearCheckpoints();
                if (_activeSystem?.Definition is { } definition)
                    _rigDuration = VfxRigMotion.RunLength(value, definition);
                _lastRigOrigin = null;

                // LTK driver.steer preserves a live run while tuning the same motion (including
                // lifecycle changes), but a different motion kind describes a different preview and
                // rewinds it to zero. Keep that distinction so seek/play reach the same seeded run.
                if (_usesStandaloneRig &&
                    _activeSystem != null &&
                    _graphs.Count > 0 &&
                    previousMotion != value.MotionKind)
                {
                    ResetSimulationToStart();
                    return;
                }

                ApplyRigTransform();
            }
        }

        public void Initialize(GL gl, AssetsManager.Utils.AppSettings settings = null)
        {
            _renderer = new VfxOpenGlRenderer();
            _renderer.Initialize(gl, settings);
            _renderer.SetOwnerWorldTransform(_worldTransform);
            _ready = true;
        }

        public void SetVfxSystem(VfxSystemModel system) => SetSystem(system);
        public bool SetEmitterVisibility(int sourceOrder, bool isVisible)
            => _graph?.SetEmitterVisibility(sourceOrder, isVisible) ?? false;

        public void SetAllEmittersVisibility(bool isVisible)
        {
            foreach (VfxPlaybackGraphRuntime graph in _graphs)
                graph.SetAllEmittersVisible(isVisible);
        }

        public int GetEmitterLiveCount(int sourceOrder)
        {
            if (_graph?.Root != null)
            {
                foreach (var emitter in _graph.Root.Emitters)
                {
                    if (emitter.SourceOrder == sourceOrder)
                        return emitter.Particles.Count;
                }
            }
            return 0;
        }

        internal bool TryGetRootEmitterState(
            int sourceOrder,
            out VfxPlaybackRuntime.EmitterState state)
        {
            if (_graph?.Root != null)
            {
                foreach (VfxPlaybackRuntime.EmitterState emitter in _graph.Root.Emitters)
                {
                    if (emitter.SourceOrder == sourceOrder)
                    {
                        state = emitter;
                        return true;
                    }
                }
            }

            state = null;
            return false;
        }

        public void Play()
        {
            _isPlaying = true;
        }

        public void Pause() => _isPlaying = false;

        public void Stop()
        {
            ClearCheckpoints();
            _isPlaying = false;
            _lastRigOrigin = null;
            foreach (VfxPlaybackGraphRuntime graph in _graphs) graph.Reset();
            foreach (var attachment in _graphAttachments.Values) attachment.HasBoneTransform = false;
            if (_activeSystem != null)
            {
                _activeSystem.CurrentTime = 0;
                ApplyRigTransform();
                ApplySpellTransforms(0d);
            }
        }

        public void SetWorldTransform(Vector3 position, float scale)
        {
            float safeScale = Math.Max(0.01f, scale);
            SetWorldTransform(Matrix4x4.CreateScale(safeScale) * Matrix4x4.CreateTranslation(position));
        }

        public void SetWorldTransform(Matrix4x4 transform)
        {
            ClearCheckpoints();
            _worldTransform = transform;
            if (_ready)
                _renderer.SetOwnerWorldTransform(transform);
            if (_spellSteps.Count > 0)
            {
                ApplySpellTransforms(_activeSystem?.CurrentTime ?? 0d);
            }
            else if (_graphAttachments.Count > 0)
            {
                foreach (VfxPlaybackGraphRuntime graph in _graphs)
                {
                    if (!_graphAttachments.ContainsKey(graph))
                    {
                        Matrix4x4 placement = _graphPlacements.GetValueOrDefault(graph, Matrix4x4.Identity);
                        Matrix4x4 authoredWorld = RootAuthoredWorld(graph);
                        Matrix4x4 orientationRoot = Matrix4x4.CreateTranslation(placement.Translation) * authoredWorld;
                        graph.SetTransform(placement * authoredWorld, orientationRoot);
                    }
                }
            }
            else
            {
                ApplyRigTransform();
            }
        }

        public void SetViewportSize(double width, double height)
        {
            _viewportWidth = (uint)Math.Max(0, width);
            _viewportHeight = (uint)Math.Max(0, height);
        }

        /// <summary>The map-only depth planar projections land on, or 0 to keep them on the flat ground.</summary>
        internal void SetTerrainDepth(uint texture, uint width, uint height)
            => _renderer?.SetTerrainDepth(texture, width, height);

        public void SetBoneTransformSampler(Func<double, string, uint, Matrix4x4?> sampler)
        {
            ClearCheckpoints();
            _boneTransformSampler = sampler;
        }

        /// <summary>
        /// Installs newly loaded emission surfaces on one graph. Like driver.setSurfaces,
        /// changing the shared lineage resource replays the run so earlier births use it too.
        /// </summary>
        internal bool SetEmissionSurfaces(
            VfxPlaybackGraphRuntime graph,
            IReadOnlyDictionary<VfxEmitterDefinition, IVfxEmissionSurfaceSampler> surfaces)
        {
            if (graph is null || !_graphs.Contains(graph) || !graph.SetEmissionSurfaces(surfaces))
                return false;
            ReplayAfterLineageResourceChange();
            return true;
        }

        /// <summary>
        /// Installs VFX-mesh joint tables on one graph and deterministically replays the session.
        /// The session owns the replay because it alone can reconstruct rig and attachment motion.
        /// </summary>
        internal bool SetMeshJointProviders(
            VfxPlaybackGraphRuntime graph,
            IReadOnlyDictionary<string, IVfxMeshJointProvider> joints)
        {
            if (graph is null || !_graphs.Contains(graph) || !graph.SetMeshJointProviders(joints))
                return false;
            ReplayAfterLineageResourceChange();
            return true;
        }

        private void ReplayAfterLineageResourceChange()
        {
            ClearCheckpoints();
            if (_activeSystem is null || _activeSystem.CurrentTime <= 0d) return;

            double target = _activeSystem.CurrentTime;
            ResetSimulationToStart();
            AdvanceTo(target, fixedSeekSteps: true);
        }

        /// <summary>
        /// Supplies the owner character's final skinning palette to AttachedMesh emitters.
        /// The same matrices are used by the champion renderer, matching LTK's detached
        /// SkinnedMesh path where particles reuse the live character skeleton.
        /// </summary>
        public void SetOwnerSkinningMatrices(Matrix4x4[] matrices)
        {
            if (!_ready) return;
            _renderer.SetOwnerSkinningMatrices(matrices);
        }

        /// <summary>
        /// Supplies the owner character's currently hidden submeshes. AttachedMesh emitters
        /// consume this live set so Animation Clip visibility events match LTK's CharacterSkinContext.
        /// </summary>
        public void SetOwnerHiddenSubmeshes(IEnumerable<uint> hashes)
        {
            if (!_ready) return;
            _renderer.SetOwnerHiddenSubmeshes(hashes);
        }

    }
}
