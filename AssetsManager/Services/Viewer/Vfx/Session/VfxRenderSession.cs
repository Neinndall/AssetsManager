using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Core;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using AssetsManager.Services.Viewer.Vfx.Resources;
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
    public sealed class VfxRenderSession : IDisposable
    {
        // LTK creates each Animation Clip particle cue and each idle effect with fixed,
        // independent deterministic driver seeds.
        internal const int AnimationClipCueSeed = 7331;
        internal const int IdleEffectSeed = 1337;
        private readonly LogService _logService;
        private readonly VfxLoadingService _loadingService;
        private readonly bool _ownsLoadingService;
        private readonly VfxGpuResourceUploader _gpuResourceUploader = new();
        private VfxOpenGlRenderer _renderer;
        private VfxPlaybackGraphRuntime _graph;
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
            VfxLoadingService loadingService = null)
        {
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

        public VfxSystemModel ActiveSystem => _activeSystem;
        public IReadOnlyList<VfxPlaybackGraphRuntime> Graphs => _graphs;
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
            set => RigSettings = VfxRigSettings.ForPreset(value);
        }

        public VfxRigSettings RigSettings
        {
            get => _rigSettings;
            set
            {
                bool motionChanged = value.MotionKind != _rigSettings.MotionKind;
                _rigSettings = value;
                ClearCheckpoints();
                if (_activeSystem?.Definition is { } definition)
                    _rigDuration = VfxRigMotion.RunLength(value, definition);
                _lastRigOrigin = null;

                if (motionChanged && _activeSystem != null)
                    ResetSimulationToStart();
                else
                    ApplyRigTransform();
            }
        }

        public void ApplyRigTransform()
        {
            if (_graphs.Count == 0 || _graphAttachments.Count > 0 || _spellSteps.Count > 0 || _activeSystem == null) return;

            var step = VfxRigMotion.Evaluate(
                _rigSettings,
                _activeSystem.CurrentTime,
                RigDuration,
                _lastRigOrigin);

            _lastRigOrigin = step.Origin;

            foreach (var graph in _graphs)
            {
                _graphPlacements[graph] = step.Transform;
                Matrix4x4 authoredWorld = RootAuthoredWorld(graph);
                Matrix4x4 orientationRoot = Matrix4x4.CreateTranslation(step.Origin) * authoredWorld;
                graph.SetTransform(step.Transform * authoredWorld, orientationRoot);
                graph.SetTarget(Vector3.Transform(step.Target, authoredWorld));
                graph.IsStopped = step.IsStopped;
            }
        }

        private void ApplySpellTransforms(double time)
        {
            foreach ((VfxPlaybackGraphRuntime graph, VfxSpellPlaybackStep step) in _spellSteps)
            {
                Matrix4x4 placement = SpellTransformAt(step, time);
                _graphPlacements[graph] = placement;
                Matrix4x4 authoredWorld = RootAuthoredWorld(graph);
                Matrix4x4 orientationRoot = Matrix4x4.CreateTranslation(placement.Translation) * authoredWorld;
                graph.SetTransform(placement * authoredWorld, orientationRoot);
                graph.SetTarget(Vector3.Transform(step.To, authoredWorld));
                graph.IsStopped = time >= step.StopTime;
                graph.SetJointTransformProvider(null);
            }
        }

        internal static Matrix4x4 SpellTransformAt(VfxSpellPlaybackStep step, double time)
        {
            if (step is null) return Matrix4x4.Identity;
            return step.Motion == VfxSpellPlaybackMotion.Path
                ? VfxRigMotion.PathTransform(step.From, step.To, time, step.StartTime, step.StopTime)
                : Matrix4x4.CreateTranslation(step.To);
        }

        public void Initialize(GL gl)
        {
            _renderer = new VfxOpenGlRenderer();
            _renderer.Initialize(gl);
            _renderer.SetOwnerWorldTransform(_worldTransform);
            _ready = true;
        }

        public void SetVfxSystem(VfxSystemModel system) => SetSystem(system);
        public void SetSystem(VfxSystemModel system)
        {
            ClearCheckpoints();
            _isPlaying = false;
            _usesStandaloneRig = system != null;
            _activeSystem = system;
            _ownerSceneContext = system?.OwnerSceneContext;
            _rigDuration = system?.Definition is { } definition
                ? VfxRigMotion.RunLength(_rigSettings, definition)
                : system?.TotalDuration ?? 0d;
            _graph = null;
            _graphs.Clear();
            _graphPlacements.Clear();
            _scheduledEffectKills.Clear();
            _graphStopTimes.Clear();
            _graphAttachments.Clear();
            _spellSteps.Clear();
            _lastRigOrigin = null;
            _boneTransformProvider = null;
            _boneTransformSampler = null;
            if (_ready)
            {
                _renderer.SetOwnerSkinningMatrices(null);
                _renderer.SetOwnerHiddenSubmeshes(_ownerSceneContext?.InitialHiddenSubmeshHashes);
            }
            if (system != null)
            {
                system.CurrentTime = 0;
            }

            if (_ready)
            {
                _renderer.ClearTextures();
                _gpuResourceUploader.Clear();
            }

            if (system?.Definition != null)
            {
                _graph = _loadingService.PreparePlaybackGraph(
                    system.Definition,
                    system.SystemCatalog,
                    system.ResourceMap,
                    system.SearchDirectory,
                    _worldTransform,
                    system.PlaybackSeed,
                    _logService,
                    system.OwnerSceneContext);
                _graphs.Add(_graph);
                _graphPlacements[_graph] = Matrix4x4.Identity;
                ApplyRigTransform();
            }
        }

        /// <summary>
        /// Rebuilds only the standalone simulation around an edited definition while preserving
        /// playback time, rig settings, seed and GPU caches. This mirrors LTK's driver.swap + seek
        /// authoring path without reloading unchanged textures or meshes.
        /// </summary>
        public bool SwapStandaloneDefinition(VfxSystemDefinition definition)
        {
            if (!_usesStandaloneRig ||
                _activeSystem?.Definition == null ||
                definition == null ||
                _activeSystem.Definition.PathHash != definition.PathHash)
            {
                return false;
            }

            double restoreTime = _activeSystem.CurrentTime;
            bool restorePlaying = _isPlaying;
            ClearCheckpoints();
            _isPlaying = false;

            var catalog = new Dictionary<uint, VfxSystemDefinition>(
                _activeSystem.SystemCatalog ?? new Dictionary<uint, VfxSystemDefinition>());
            catalog[definition.PathHash] = definition;
            _activeSystem.Definition = definition;
            _activeSystem.SystemCatalog = catalog;
            _activeSystem.TotalDuration = VfxDurationCalculator.SystemSpan(definition);
            _rigDuration = VfxRigMotion.RunLength(_rigSettings, definition);

            _graph = null;
            _graphs.Clear();
            _graphPlacements.Clear();
            _scheduledEffectKills.Clear();
            _graphStopTimes.Clear();
            _graphAttachments.Clear();
            _spellSteps.Clear();
            _lastRigOrigin = null;

            _graph = _loadingService.PreparePlaybackGraph(
                definition,
                _activeSystem.SystemCatalog,
                _activeSystem.ResourceMap,
                _activeSystem.SearchDirectory,
                _worldTransform,
                _activeSystem.PlaybackSeed,
                _logService,
                _activeSystem.OwnerSceneContext);
            _graphs.Add(_graph);
            _graphPlacements[_graph] = Matrix4x4.Identity;
            _activeSystem.CurrentTime = 0d;
            ApplyRigTransform();
            Seek(restoreTime);
            _isPlaying = restorePlaying;
            return true;
        }

        public bool SetAbilityComposition(
            VfxAbilityComposition composition,
            IReadOnlyDictionary<uint, VfxSystemDefinition> systems,
            IReadOnlyDictionary<uint, uint> resourceMap,
            string searchDirectory,
            int seed,
            VfxOwnerSceneContext ownerSceneContext = null)
            => SetAnimationSession(
                composition,
                null,
                systems,
                resourceMap,
                searchDirectory,
                seed,
                0,
                ownerSceneContext);

        public bool SetSpellSession(
            IReadOnlyList<VfxSpellPlaybackStep> steps,
            IReadOnlyDictionary<uint, VfxSystemDefinition> systems,
            IReadOnlyDictionary<uint, uint> resourceMap,
            string searchDirectory,
            double animationDuration,
            VfxOwnerSceneContext ownerSceneContext = null)
        {
            ClearCheckpoints();
            steps ??= Array.Empty<VfxSpellPlaybackStep>();
            systems ??= new Dictionary<uint, VfxSystemDefinition>();
            resourceMap ??= new Dictionary<uint, uint>();
            _isPlaying = false;
            _usesStandaloneRig = false;
            _ownerSceneContext = ownerSceneContext;
            _graph = null;
            _graphs.Clear();
            _graphPlacements.Clear();
            _scheduledEffectKills.Clear();
            _graphStopTimes.Clear();
            _graphAttachments.Clear();
            _spellSteps.Clear();
            _lastRigOrigin = null;
            _boneTransformProvider = null;
            _boneTransformSampler = null;

            if (_ready)
            {
                _renderer.SetOwnerSkinningMatrices(null);
                _renderer.SetOwnerHiddenSubmeshes(_ownerSceneContext?.InitialHiddenSubmeshHashes);
                _renderer.ClearTextures();
                _gpuResourceUploader.Clear();
            }

            double duration = Math.Max(0.1d, animationDuration);
            foreach (VfxSpellPlaybackStep step in steps)
            {
                if (step?.System is null) continue;
                VfxPlaybackGraphRuntime graph = _loadingService.PreparePlaybackGraph(
                    step.System,
                    systems,
                    resourceMap,
                    searchDirectory,
                    _worldTransform,
                    step.Seed,
                    _logService,
                    ownerSceneContext);
                graph.SetStartDelay((float)Math.Max(0d, step.StartTime));
                _graphs.Add(graph);
                _graphPlacements[graph] = Matrix4x4.Identity;
                _spellSteps[graph] = step;
                if (step.StopTime > step.StartTime)
                    _graphStopTimes[graph] = step.StopTime;
                _graph ??= graph;

                double active = Math.Max(0d, step.StopTime - step.StartTime);
                duration = Math.Max(
                    duration,
                    step.StopTime + VfxDurationCalculator.LingerTail(step.System, active));
            }

            _activeSystem = new VfxSystemModel
            {
                Name = "Spell Preview",
                SystemCatalog = systems,
                ResourceMap = resourceMap,
                SearchDirectory = searchDirectory,
                OwnerSceneContext = ownerSceneContext,
                TotalDuration = Math.Min(Math.Max(0.1d, duration), 60d),
                Speed = 1.0
            };
            ApplySpellTransforms(0d);
            return steps.Count > 0 || animationDuration > 0d;
        }

        public bool SetAnimationSession(
            VfxAbilityComposition composition,
            IReadOnlyList<VfxIdleEffectDefinition> idleEffects,
            IReadOnlyDictionary<uint, VfxSystemDefinition> systems,
            IReadOnlyDictionary<uint, uint> resourceMap,
            string searchDirectory,
            int seed,
            double animationDuration,
            VfxOwnerSceneContext ownerSceneContext = null)
        {
            ClearCheckpoints();
            systems ??= new Dictionary<uint, VfxSystemDefinition>();
            resourceMap ??= new Dictionary<uint, uint>();
            _isPlaying = false;
            _usesStandaloneRig = false;
            _ownerSceneContext = ownerSceneContext;
            if (_ready)
            {
                _renderer.SetOwnerSkinningMatrices(null);
                _renderer.SetOwnerHiddenSubmeshes(_ownerSceneContext?.InitialHiddenSubmeshHashes);
            }
            _graph = null;
            _graphs.Clear();
            _graphPlacements.Clear();
            _scheduledEffectKills.Clear();
            _graphStopTimes.Clear();
            _graphAttachments.Clear();
            _spellSteps.Clear();

            if (_ready)
            {
                _renderer.ClearTextures();
                _gpuResourceUploader.Clear();
            }

            double duration = Math.Max(0.1, animationDuration);

            // 1. Instantiate Idle Effects (continuous character-anchored auras)
            if (idleEffects != null)
            {
                foreach (VfxIdleEffectDefinition idle in idleEffects)
                {
                    // CharacterIdleEffect.effectKey is a ResourceResolver key, never a direct
                    // VfxSystemDefinition object id. LTK drops an idle whose key is unmapped or
                    // maps outside the systems in reach instead of guessing by hash/name.
                    if (idle.EffectKey == 0 ||
                        !resourceMap.TryGetValue(idle.EffectKey, out uint mappedHash) ||
                        mappedHash == 0 ||
                        !systems.TryGetValue(mappedHash, out VfxSystemDefinition idleDef))
                    {
                        continue;
                    }

                    var idleGraph = _loadingService.PreparePlaybackGraph(
                        idleDef,
                        systems,
                        resourceMap,
                        searchDirectory,
                        _worldTransform,
                        IdleEffectSeed,
                        _logService,
                        ownerSceneContext);

                    _graphs.Add(idleGraph);
                    _graphPlacements[idleGraph] = Matrix4x4.Identity;
                    _graphAttachments[idleGraph] = new GraphAttachmentInfo
                    {
                        BoneName = idle.BoneName,
                        BoneHash = idle.BoneNameHash,
                        TargetBoneName = idle.TargetBoneName,
                        TargetBoneHash = idle.TargetBoneNameHash,
                        EffectKey = idle.EffectKey,
                        LocalOffset = idle.Position,
                        BaseTransform = Matrix4x4.Identity,
                        IsIdleEffect = true
                    };
                    _graph ??= idleGraph;
                }
            }

            // 2. Instantiate Animation Composition Events (cued particle events)
            if (composition != null)
            {
                foreach (VfxCompositionEvent compositionEvent in composition.Events)
                {
                    var cue = compositionEvent.Event;
                    float startSeconds = Math.Max(0f, cue.StartFrame * composition.TickDuration);
                    uint effectKey = compositionEvent.UsesEnemyEffect ? cue.EnemyEffectKey : cue.EffectKey;
                    if (cue.IsKillEvent)
                    {
                        _scheduledEffectKills.Add((startSeconds, effectKey));
                        continue;
                    }
                    if (compositionEvent.System is null) continue;

                    // LTK keeps ParticleEventData.scale in the parsed event metadata but its
                    // Animation Clip viewport does not apply it to the spawned VFX system.
                    // Keep playback tied to the cue rig only; skinScale is handled separately.
                    var attachments = cue.Attachments is { Count: > 0 }
                        ? cue.Attachments
                        : new[] { new VfxParticleEventAttachment(0, 0) };
                    foreach (var pair in attachments)
                    {
                    var eventGraph = _loadingService.PreparePlaybackGraph(
                        compositionEvent.System,
                        systems,
                        resourceMap,
                        searchDirectory,
                        _worldTransform,
                        AnimationClipCueSeed,
                        _logService,
                        ownerSceneContext);

                    eventGraph.SetStartDelay(startSeconds);

                    _graphs.Add(eventGraph);
                    _graphPlacements[eventGraph] = Matrix4x4.Identity;

                    _graphAttachments[eventGraph] = new GraphAttachmentInfo
                    {
                        BoneName = null,
                        BoneHash = pair.SourceBoneHash,
                        TargetBoneHash = pair.TargetBoneHash,
                        EffectKey = effectKey,
                        StartTime = startSeconds,
                        // LTK's Animation Clip viewport keeps a cue riding its source joint;
                        // ParticleEventData's detachable field is not part of that playback contract.
                        IsDetachable = false,
                        LocalOffset = Vector3.Zero,
                        BaseTransform = Matrix4x4.Identity,
                        IsIdleEffect = false
                    };

                    if (cue.EndFrame > cue.StartFrame)
                    {
                        _graphStopTimes[eventGraph] = cue.EndFrame * composition.TickDuration;
                    }

                    _graph ??= eventGraph;
                    }

                    double effectDuration = VfxDurationCalculator.Calculate(
                        compositionEvent.System,
                        systems,
                        resourceMap);
                    if (double.IsFinite(effectDuration))
                        duration = Math.Max(duration, startSeconds + effectDuration);
                }

                if (composition.EndFrame > composition.StartFrame)
                    duration = Math.Max(duration, (composition.EndFrame - composition.StartFrame) * composition.TickDuration);
                foreach (VfxCompositionEvent compositionEvent in composition.Events)
                {
                    if (compositionEvent.Event.EndFrame >= compositionEvent.Event.StartFrame)
                        duration = Math.Max(
                            duration,
                            (compositionEvent.Event.EndFrame - composition.StartFrame) * composition.TickDuration);
                }
            }

            string sequenceName = composition != null
                ? (!string.IsNullOrEmpty(composition.ClipName) ? composition.ClipName : $"0x{composition.SequencePathHash:X8}")
                : "Animation";

            _activeSystem = new VfxSystemModel
            {
                Name = $"Session {sequenceName}",
                SystemCatalog = systems,
                ResourceMap = resourceMap,
                SearchDirectory = searchDirectory,
                OwnerSceneContext = ownerSceneContext,
                PlaybackSeed = seed,
                TotalDuration = Math.Max(0.1, duration),
                Speed = 1.0
            };

            return _graphs.Count > 0;
        }

        public void UpdateBoneTransforms(Func<string, uint, Matrix4x4?> boneTransformProvider)
        {
            _boneTransformProvider = boneTransformProvider;
            if (_graphs.Count == 0) return;

            Func<string, Matrix4x4?> jointProvider = boneTransformProvider is null
                ? null
                : boneName => boneTransformProvider(boneName, 0) is { } raw
                    ? PrepareBoneTransform(raw)
                    : null;

            foreach (VfxPlaybackGraphRuntime graph in _graphs)
            {
                if (_spellSteps.ContainsKey(graph))
                {
                    // Ability projectile/impact rigs are independent scene rigs in LTK; they do
                    // not inherit the owner's live joint table even while the champion animates.
                    graph.SetJointTransformProvider(null);
                    continue;
                }

                // boneToSpawnAt children use the same live skeleton as clip/idle attachments.
                graph.SetJointTransformProvider(jointProvider);

                if (_graphAttachments.TryGetValue(graph, out var attachment) && boneTransformProvider != null)
                {
                    Matrix4x4? boneMatrix = ResolveAttachmentBone(
                        boneTransformProvider,
                        attachment.BoneName,
                        attachment.BoneHash);
                    Matrix4x4? targetBoneMatrix = ResolveAttachmentBone(
                        boneTransformProvider,
                        attachment.TargetBoneName,
                        attachment.TargetBoneHash);

                    if (boneMatrix.HasValue)
                    {
                        Matrix4x4 boneTransform = PrepareBoneAnchorTransform(
                            boneMatrix.Value,
                            attachment.LocalOffset,
                            CurrentSkinScale);
                        if (attachment.IsDetachable && attachment.HasBoneTransform)
                            boneTransform = attachment.BoneTransform;
                        if (attachment.IsDetachable && _activeSystem?.CurrentTime >= attachment.StartTime)
                        {
                            attachment.BoneTransform = boneTransform;
                            attachment.HasBoneTransform = true;
                        }

                        Matrix4x4 authoredWorld = RootAuthoredWorld(graph);
                        Matrix4x4 orientationRoot =
                            attachment.BaseTransform * Matrix4x4.CreateTranslation(boneTransform.Translation) * authoredWorld;
                        graph.SetTransform(
                            attachment.BaseTransform * boneTransform * authoredWorld,
                            orientationRoot);

                        Vector3 target = targetBoneMatrix.HasValue
                            ? Vector3.Transform(PrepareBoneTransform(targetBoneMatrix.Value).Translation, authoredWorld)
                            : Vector3.Transform(
                                boneTransform.Translation + new Vector3(VfxRigMotion.TargetReach, 0f, 0f),
                                authoredWorld);
                        graph.SetTarget(target);
                        continue;
                    }

                    // LTK's jointAnchor(slot=-1) is the skeleton origin with an identity basis.
                    // A valid target joint still aims beams even when the source joint is missing.
                    Matrix4x4 fallbackPlacement = IdleFallbackTransform(
                        attachment.BaseTransform,
                        attachment.LocalOffset,
                        CurrentSkinScale,
                        Matrix4x4.Identity);
                    Matrix4x4 fallbackWorld = RootAuthoredWorld(graph);
                    Matrix4x4 fallbackOrientationRoot =
                        Matrix4x4.CreateTranslation(fallbackPlacement.Translation) * fallbackWorld;
                    graph.SetTransform(fallbackPlacement * fallbackWorld, fallbackOrientationRoot);
                    Vector3 fallbackTarget = targetBoneMatrix.HasValue
                        ? Vector3.Transform(PrepareBoneTransform(targetBoneMatrix.Value).Translation, fallbackWorld)
                        : Vector3.Transform(
                            fallbackPlacement.Translation + new Vector3(VfxRigMotion.TargetReach, 0f, 0f),
                            fallbackWorld);
                    graph.SetTarget(fallbackTarget);
                    continue;
                }

                if (_graphAttachments.TryGetValue(graph, out attachment) && attachment.IsIdleEffect)
                {
                    // LTK keeps an idle effect at the skeleton origin when its authored bone
                    // cannot be resolved, while still applying the authored local position.
                    Matrix4x4 fallbackPlacement = IdleFallbackTransform(
                        attachment.BaseTransform,
                        attachment.LocalOffset,
                        CurrentSkinScale,
                        Matrix4x4.Identity);
                    Matrix4x4 authoredWorld = RootAuthoredWorld(graph);
                    Matrix4x4 orientationRoot =
                        Matrix4x4.CreateTranslation(fallbackPlacement.Translation) * authoredWorld;
                    graph.SetTransform(fallbackPlacement * authoredWorld, orientationRoot);
                    continue;
                }

                if (_graphPlacements.TryGetValue(graph, out var basePlacement))
                {
                    Matrix4x4 authoredWorld = RootAuthoredWorld(graph);
                    Matrix4x4 orientationRoot = Matrix4x4.CreateTranslation(basePlacement.Translation) * authoredWorld;
                    graph.SetTransform(basePlacement * authoredWorld, orientationRoot);
                }
            }
        }

        private static Matrix4x4? ResolveAttachmentBone(
            Func<string, uint, Matrix4x4?> provider,
            string name,
            uint hash)
        {
            if (provider == null) return null;
            if (!string.IsNullOrEmpty(name) && provider(name, hash) is { } named)
                return named;
            return hash != 0 ? provider(null, hash) : null;
        }

        private float CurrentSkinScale
            => _ownerSceneContext is { SkinScale: > 0f } context && float.IsFinite(context.SkinScale)
                ? context.SkinScale
                : 1f;

        private Matrix4x4 RootAuthoredWorld(VfxPlaybackGraphRuntime graph)
            => graph.Root.Definition.Transform.GetValueOrDefault(Matrix4x4.Identity) * _worldTransform;

        internal static Matrix4x4 IdleFallbackTransform(
            Matrix4x4 baseTransform,
            Vector3 localOffset,
            float skinScale,
            Matrix4x4 worldTransform)
        {
            float scale = skinScale > 0f && float.IsFinite(skinScale) ? skinScale : 1f;
            return baseTransform * Matrix4x4.CreateTranslation(localOffset * scale) * worldTransform;
        }

        private Matrix4x4 PrepareBoneTransform(Matrix4x4 transform)
            => PrepareBoneAnchorTransform(transform, Vector3.Zero, CurrentSkinScale);

        internal static Matrix4x4 PrepareBoneAnchorTransform(
            Matrix4x4 transform,
            Vector3 localOffset,
            float skinScale)
        {
            static Vector3 Normal(Vector3 value, Vector3 fallback)
                => value.LengthSquared() > 1e-8f ? Vector3.Normalize(value) : fallback;

            float scale = skinScale > 0f && float.IsFinite(skinScale) ? skinScale : 1f;
            Vector3 right = Normal(Vector3.TransformNormal(Vector3.UnitX, transform), Vector3.UnitX);
            Vector3 up = Normal(Vector3.TransformNormal(Vector3.UnitY, transform), Vector3.UnitY);
            Vector3 forward = Normal(Vector3.TransformNormal(Vector3.UnitZ, transform), Vector3.UnitZ);
            // LTK's jointAnchor transforms the authored offset by the joint's complete
            // posed frame, then applies the character skin scale to the resulting origin.
            Vector3 translation = Vector3.Transform(localOffset, transform) * scale;
            return new Matrix4x4(
                right.X, right.Y, right.Z, 0f,
                up.X, up.Y, up.Z, 0f,
                forward.X, forward.Y, forward.Z, 0f,
                translation.X, translation.Y, translation.Z, 1f);
        }

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

        public void Update(float deltaTime)
        {
            if (!_isPlaying || _activeSystem == null) return;
            float frameTime = NormalizeFrameTime(deltaTime);
            if (frameTime <= 0f) return;

            float speed = NormalizePlaybackSpeed(_activeSystem.Speed);
            float elapsed = frameTime * speed;

            AdvanceTo(_activeSystem.CurrentTime + elapsed, fixedSeekSteps: false);

            if (_usesStandaloneRig && _rigSettings.IsLooping)
                return;

            if (ShouldFinishPlayback(
                    HasFinitePlaybackDuration,
                    _activeSystem.CurrentTime,
                    RigDuration,
                    _graphs.All(graph => graph.IsComplete)))
            {
                if (HasFinitePlaybackDuration)
                    _activeSystem.CurrentTime = RigDuration;
                _isPlaying = false;
            }
        }

        internal static bool ShouldFinishPlayback(
            bool hasFiniteDuration,
            double currentTime,
            double totalDuration,
            bool graphIsComplete)
            => hasFiniteDuration
                ? currentTime >= totalDuration
                : graphIsComplete;

        internal static float NormalizeFrameTime(float deltaTime)
        {
            if (!float.IsFinite(deltaTime) || deltaTime <= 0f) return 0f;
            return Math.Min(deltaTime, 0.1f);
        }

        internal static float NormalizePlaybackSpeed(double speed)
        {
            if (!double.IsFinite(speed)) return 1f;
            return (float)Math.Clamp(speed, 0.05d, 2d);
        }

        public void Seek(double seconds)
        {
            if (_activeSystem == null || !double.IsFinite(seconds)) return;
            SeekExact(QuantizeLtkSeek(seconds));
        }

        internal static double QuantizeLtkSeek(double seconds)
        {
            if (!double.IsFinite(seconds)) return 0d;
            double wanted = Math.Clamp(seconds, 0d, MaximumSeekTime);
            // JavaScript Math.round is half-up for the non-negative seek domain.
            double steps = Math.Floor((wanted / SeekStep) + 0.5d);
            return steps * SeekStep;
        }

        private void SeekExact(double target)
        {
            if (_activeSystem == null || !double.IsFinite(target)) return;
            target = Math.Max(0d, target);

            if (target + 1e-9 >= _activeSystem.CurrentTime)
            {
                LastSeekRestoreTime = _activeSystem.CurrentTime;
                AdvanceTo(target, fixedSeekSteps: true);
                return;
            }

            if (!TryRestoreCheckpoint(target))
            {
                LastSeekRestoreTime = 0d;
                ResetSimulationToStart();
            }
            AdvanceTo(target, fixedSeekSteps: true);
        }

        private void ResetSimulationToStart()
        {
            _lastRigOrigin = null;
            foreach (VfxPlaybackGraphRuntime graph in _graphs) graph.Reset();
            foreach (var attachment in _graphAttachments.Values) attachment.HasBoneTransform = false;
            _activeSystem.CurrentTime = 0;
            ApplyRigTransform();
            ApplySpellTransforms(0d);
            if (_boneTransformSampler != null)
                UpdateBoneTransforms((name, hash) => _boneTransformSampler(0, name, hash));
        }

        /// <summary>
        /// Advances an animation-driven VFX session to the canonical clip time without
        /// running an independent playback clock. Backward jumps rebuild deterministically.
        /// </summary>
        public void SynchronizeTo(double seconds)
        {
            if (_activeSystem == null || !double.IsFinite(seconds)) return;
            double target = Math.Max(0d, seconds);
            if (target + 1e-9 < _activeSystem.CurrentTime)
            {
                // Animation-follow seeks use the scene clock itself rather than the VFX
                // transport's 60 Hz scrub quantization.
                SeekExact(target);
                return;
            }
            AdvanceTo(target, fixedSeekSteps: false);
        }

        public void SetBoneTransformSampler(Func<double, string, uint, Matrix4x4?> sampler)
        {
            ClearCheckpoints();
            _boneTransformSampler = sampler;
        }

        /// <summary>
        /// Installs newly loaded emission surfaces on one graph. Like LTK's driver.setSurfaces,
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

        private void AdvanceTo(double target, bool fixedSeekSteps)
        {
            if (!double.IsFinite(target)) return;
            while (_activeSystem.CurrentTime < target)
            {
                double previous = _activeSystem.CurrentTime;
                // LTK plays one variable step per rendered frame. Only seek/replay advances in
                // fixed 1/60 s slices; checkpoints themselves never subdivide the physics step.
                double next = fixedSeekSteps
                    ? Math.Min(target, previous + SeekStep)
                    : target;
                foreach (var kill in _scheduledEffectKills)
                    if (kill.Time > previous && kill.Time < next) next = kill.Time;
                foreach (double stopTime in _graphStopTimes.Values)
                    if (stopTime > previous && stopTime < next) next = stopTime;
                foreach (GraphAttachmentInfo attachment in _graphAttachments.Values)
                    if (attachment.StartTime > previous && attachment.StartTime < next) next = attachment.StartTime;

                KillGraphsAt(previous);

                // LTK does not split a variable frame at the rig boundary. It detects a wrap
                // from the end phase, resets to phase zero, then runs this frame's whole dt on
                // the new pass. This also applies during the fixed 1/60 seek replay.
                bool rigWrapped = DidRigWrap(previous, next);
                if (rigWrapped)
                    ReplayRigLoopAtStart();

                _activeSystem.CurrentTime = next;
                ApplyRigTransform();
                ApplySpellTransforms(next);
                if (_boneTransformSampler != null)
                    UpdateBoneTransforms((name, hash) => _boneTransformSampler(next, name, hash));
                else if (_boneTransformProvider != null)
                    UpdateBoneTransforms(_boneTransformProvider);
                foreach (var graph in _graphs) graph.Update((float)(next - previous));
                KillGraphsAt(next);
                TryCaptureCheckpoint(next);
            }
        }

        private bool DidRigWrap(double previous, double next)
        {
            if (!_usesStandaloneRig ||
                !_rigSettings.IsLooping ||
                !(RigDuration > 0d) ||
                !double.IsFinite(RigDuration))
            {
                return false;
            }

            float previousPhase = VfxRigMotion.Evaluate(_rigSettings, previous, RigDuration).Phase;
            float nextPhase = VfxRigMotion.Evaluate(_rigSettings, next, RigDuration).Phase;
            return nextPhase < previousPhase;
        }

        private void ReplayRigLoopAtStart()
        {
            VfxRigStep start = VfxRigMotion.Evaluate(_rigSettings, 0d, RigDuration);
            _lastRigOrigin = start.Origin;

            foreach (VfxPlaybackGraphRuntime graph in _graphs)
            {
                _graphPlacements[graph] = start.Transform;
                Matrix4x4 authoredWorld = RootAuthoredWorld(graph);
                Matrix4x4 startTransform = start.Transform * authoredWorld;
                Matrix4x4 orientationRoot = Matrix4x4.CreateTranslation(start.Origin) * authoredWorld;
                graph.ReplayLoop(startTransform, orientationRoot);
                graph.SetTarget(Vector3.Transform(start.Target, authoredWorld));
                graph.IsStopped = start.IsStopped;
            }
        }

        private void ClearCheckpoints()
        {
            _checkpoints.Clear();
            _checkpointBytes = 0;
            LastSeekRestoreTime = 0d;
        }

        private SessionSnapshot CaptureSessionSnapshot()
        {
            var placements = new Matrix4x4[_graphs.Count];
            var graphs = new VfxPlaybackGraphRuntime.Snapshot[_graphs.Count];
            var attachments = new AttachmentSnapshot[_graphs.Count];
            long bytes = 256;

            for (int index = 0; index < _graphs.Count; index++)
            {
                VfxPlaybackGraphRuntime graph = _graphs[index];
                placements[index] = _graphPlacements.GetValueOrDefault(graph, Matrix4x4.Identity);
                VfxPlaybackGraphRuntime.Snapshot graphState = graph.CaptureSnapshot();
                graphs[index] = graphState;
                bytes += 128L + graphState.Bytes;

                if (_graphAttachments.TryGetValue(graph, out GraphAttachmentInfo attachment))
                {
                    attachments[index] = new AttachmentSnapshot(
                        attachment.HasBoneTransform,
                        attachment.BoneTransform);
                    bytes += 80;
                }
            }

            return new SessionSnapshot(
                _activeSystem.CurrentTime,
                _lastRigOrigin,
                placements,
                graphs,
                attachments,
                bytes);
        }

        private void RestoreSessionSnapshot(SessionSnapshot snapshot)
        {
            if (snapshot.Graphs.Length != _graphs.Count)
                throw new InvalidOperationException("VFX session checkpoint graph layout no longer matches the active session.");

            _activeSystem.CurrentTime = snapshot.Time;
            _lastRigOrigin = snapshot.LastRigOrigin;
            for (int index = 0; index < _graphs.Count; index++)
            {
                VfxPlaybackGraphRuntime graph = _graphs[index];
                _graphPlacements[graph] = snapshot.Placements[index];
                graph.RestoreSnapshot(snapshot.Graphs[index]);

                AttachmentSnapshot savedAttachment = snapshot.Attachments[index];
                if (savedAttachment is not null && _graphAttachments.TryGetValue(graph, out GraphAttachmentInfo attachment))
                {
                    attachment.HasBoneTransform = savedAttachment.HasBoneTransform;
                    attachment.BoneTransform = savedAttachment.BoneTransform;
                }
            }
        }

        private void TryCaptureCheckpoint(double time)
        {
            // LTK stores the first state that crosses each quarter-second mark; it does not
            // force the simulation to land exactly on the mark just to make a checkpoint.
            int mark = (int)Math.Floor(time / CheckpointInterval + 1e-9);
            if (mark < 1 || mark > MaximumCheckpointMarks || _checkpoints.ContainsKey(mark)) return;

            SessionSnapshot state = CaptureSessionSnapshot();
            long size = state.Bytes;
            if (size <= 0 || size > CheckpointBudgetBytes) return;

            int remaining = MaximumCheckpointMarks - mark + 1;
            int stride = 1;
            while (Math.Ceiling(remaining / (double)stride) * size > CheckpointBudgetBytes)
                stride *= 2;
            if (mark % stride != 0) return;

            if (_checkpointBytes + size > CheckpointBudgetBytes)
                ThinCheckpoints(size);
            if (_checkpointBytes + size > CheckpointBudgetBytes) return;

            var checkpoint = new Checkpoint(mark, state);
            _checkpoints[mark] = checkpoint;
            _checkpointBytes += checkpoint.Bytes;
        }

        private void ThinCheckpoints(long requiredBytes)
        {
            for (int stride = 2; _checkpointBytes + requiredBytes > CheckpointBudgetBytes && stride <= 512; stride *= 2)
            {
                foreach (int mark in _checkpoints.Keys.Where(mark => mark % stride != 0).ToArray())
                {
                    _checkpointBytes -= _checkpoints[mark].Bytes;
                    _checkpoints.Remove(mark);
                }
            }
        }

        private bool TryRestoreCheckpoint(double target)
        {
            int mark = Math.Min(MaximumCheckpointMarks, (int)Math.Floor(target / CheckpointInterval + 1e-9));
            for (; mark >= 1; mark--)
            {
                if (!_checkpoints.TryGetValue(mark, out Checkpoint checkpoint)) continue;
                // A variable playback frame may have crossed this mark after the exact target.
                // LTK also rejects checkpoints whose recorded seek step lies beyond the request.
                if (checkpoint.Time > target + 1e-9) continue;
                RestoreSessionSnapshot(checkpoint.State);
                LastSeekRestoreTime = checkpoint.Time;
                return true;
            }
            return false;
        }

        private void KillGraphsAt(double seconds)
        {
            foreach (var (graph, stopTime) in _graphStopTimes)
            {
                if (seconds >= stopTime) graph.IsStopped = true;
            }
            foreach (var kill in _scheduledEffectKills)
                if (seconds >= kill.Time)
                    foreach (var (graph, attachment) in _graphAttachments)
                        if (attachment.EffectKey == kill.EffectKey && attachment.StartTime <= kill.Time)
                            graph.Kill();
        }

        public void Render(
            Matrix4x4 viewProjection,
            Matrix4x4 view,
            VfxPreviewViewMode viewMode = VfxPreviewViewMode.Lit,
            bool wireOverlay = false)
        {
            if (!PrepareRenderFrame(viewProjection, view, viewMode, wireOverlay)) return;

            using IDisposable renderBatch = BeginPreparedRenderBatch();
            RenderPreparedColorPass();
            CapturePreparedDistortionFrame();
            RenderPreparedDistortionPass();
        }

        internal bool PrepareRenderFrame(
            Matrix4x4 viewProjection,
            Matrix4x4 view,
            VfxPreviewViewMode viewMode = VfxPreviewViewMode.Lit,
            bool wireOverlay = false)
        {
            if (!_ready || _graphs.Count == 0)
                return false;

            _gpuResourceUploader.UploadPendingResources(_graphs, _renderer);
            _renderSources.Clear();
            bool needsSoftParticles = false;
            foreach (VfxPlaybackGraphRuntime graph in _graphs)
            {
                foreach (VfxPlaybackRuntime runtime in graph.Runtimes)
                {
                    IReadOnlyList<VfxPlaybackRuntime.EmitterState> emitters = runtime.Emitters;
                    _renderSources.Add(emitters);
                    if (needsSoftParticles) continue;

                    foreach (VfxPlaybackRuntime.EmitterState emitter in emitters)
                    {
                        if (!emitter.IsVisible || !VfxOpenGlRenderer.ShouldUseSoftParticles(emitter.Def, true))
                            continue;
                        needsSoftParticles = true;
                        break;
                    }
                }
            }

            _renderer.CaptureScene(
                _viewportWidth,
                _viewportHeight,
                false,
                needsSoftParticles);
            VfxRenderQueue.BuildInto(_renderSources, _renderQueue, _renderGraphOrders);

            var previewPasses = ResolvePreviewPasses(viewMode, wireOverlay, _renderer.SupportsWireframe);
            _preparedViewProjection = viewProjection;
            _preparedView = view;
            _preparedShaded = previewPasses.Shaded;
            _preparedWireframe = previewPasses.Wireframe;
            _preparedWireOpacity = previewPasses.WireOpacity;

            _shadedRenderQueue.Clear();
            _distortionRenderQueue.Clear();
            foreach (VfxRenderQueueEntry entry in _renderQueue)
            {
                if (entry.Emitter.Def.DrawsAsDistortion)
                    _distortionRenderQueue.Add(entry);
                else
                    _shadedRenderQueue.Add(entry);
            }
            return _renderQueue.Count > 0;
        }

        internal IDisposable BeginPreparedRenderBatch() => _renderer.BeginRenderBatch();

        internal void RenderPreparedColorPass()
        {
            if (_preparedShaded && _shadedRenderQueue.Count > 0)
                _renderer.Render(_shadedRenderQueue, _preparedViewProjection, _preparedView);

            // LTK keeps every wire twin on the particle colour layer, including the twin of a
            // distorting solid. Overlay wires therefore belong in the captured frame and the
            // distortion layer is drawn over them afterwards.
            if (_preparedWireframe && _renderQueue.Count > 0)
            {
                _renderer.Render(
                    _renderQueue,
                    _preparedViewProjection,
                    _preparedView,
                    wireframePass: true,
                    wireframeOpacity: _preparedWireOpacity);
            }
        }

        internal bool HasPreparedDistortionPass =>
            _preparedShaded && _distortionRenderQueue.Count > 0;

        internal void CapturePreparedDistortionFrame()
        {
            if (HasPreparedDistortionPass)
                _renderer.CaptureScene(_viewportWidth, _viewportHeight, true, false);
        }

        internal void RenderPreparedDistortionPass()
        {
            if (HasPreparedDistortionPass)
                _renderer.Render(_distortionRenderQueue, _preparedViewProjection, _preparedView);
        }

        internal static (bool Shaded, bool Wireframe, float WireOpacity) ResolvePreviewPasses(
            VfxPreviewViewMode mode,
            bool wireOverlay,
            bool supportsWireframe)
        {
            bool wireframeOnly = mode == VfxPreviewViewMode.Wireframe;
            bool overlayAllowed = mode == VfxPreviewViewMode.Lit || mode == VfxPreviewViewMode.Untextured;
            bool shaded = !wireframeOnly || !supportsWireframe;
            bool wireframe = supportsWireframe && (wireframeOnly || (wireOverlay && overlayAllowed));
            float opacity = wireframeOnly ? 1f : 0.35f;
            return (shaded, wireframe, opacity);
        }

        internal static float WireframeOpacity(VfxPreviewViewMode mode, bool wireOverlay = false)
            => ResolvePreviewPasses(mode, wireOverlay, supportsWireframe: true).WireOpacity;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_ready)
            {
                _renderer.Dispose();
                _ready = false;
            }
            _gpuResourceUploader.Clear();
            if (_ownsLoadingService)
                _loadingService.Dispose();
            _graph = null;
            _graphs.Clear();
            _renderSources.Clear();
            _renderQueue.Clear();
            _shadedRenderQueue.Clear();
            _distortionRenderQueue.Clear();
            _renderGraphOrders.Clear();
            _graphPlacements.Clear();
            _scheduledEffectKills.Clear();
            _graphStopTimes.Clear();
            _graphAttachments.Clear();
            _spellSteps.Clear();
            _activeSystem = null;
        }
    }
}
