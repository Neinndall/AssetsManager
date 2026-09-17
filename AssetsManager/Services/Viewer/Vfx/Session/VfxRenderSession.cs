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
        // LTK creates each Animation Clip particle cue with the same deterministic driver seed.
        // Keep this separate from standalone/idle VFX seeds: cue playback is its own pipeline.
        internal const int AnimationClipCueSeed = 7331;
        private readonly LogService _logService;
        private readonly VfxLoadingService _loadingService;
        private readonly bool _ownsLoadingService;
        private readonly Dictionary<BitmapSource, uint> _textureCache = new();
        private VfxOpenGlRenderer _renderer;
        private VfxPlaybackGraphRuntime _graph;
        private sealed class GraphAttachmentInfo
        {
            public string BoneName { get; set; }
            public uint BoneHash { get; set; }
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
        private const double CheckpointInterval = 0.25d;
        private const int MaximumCheckpointMarks = 240;
        private const long CheckpointBudgetBytes = 256L * 1024L * 1024L;
        private readonly Dictionary<int, Checkpoint> _checkpoints = new();
        private long _checkpointBytes;
        internal int CheckpointCount => _checkpoints.Count;
        internal long CheckpointBytes => _checkpointBytes;
        internal double LastSeekRestoreTime { get; private set; }

        private readonly List<VfxPlaybackGraphRuntime> _graphs = new();
        private readonly Dictionary<VfxPlaybackGraphRuntime, Matrix4x4> _graphPlacements = new();
        private readonly List<(double Time, uint EffectKey)> _scheduledEffectKills = new();
        private readonly Dictionary<VfxPlaybackGraphRuntime, double> _graphStopTimes = new();
        private readonly Dictionary<VfxPlaybackGraphRuntime, GraphAttachmentInfo> _graphAttachments = new();
        private VfxSystemModel _activeSystem;
        private VfxRigPreset _rigPreset = VfxRigPreset.Still;
        private double _rigDuration;
        private Vector3? _lastRigOrigin;
        private Matrix4x4 _worldTransform = Matrix4x4.Identity;
        private VfxOwnerSceneContext _ownerSceneContext;
        private bool _isPlaying;
        private bool _ready;
        private bool _disposed;
        private uint _viewportWidth;
        private uint _viewportHeight;
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

        public VfxSystemModel ActiveSystem => _activeSystem;
        public IReadOnlyList<VfxPlaybackGraphRuntime> Graphs => _graphs;
        public double CurrentTime => _activeSystem?.CurrentTime ?? 0d;
        public double RigDuration => _activeSystem?.Definition is null ? _activeSystem?.TotalDuration ?? 0d : _rigDuration;
        private bool HasFinitePlaybackDuration => double.IsFinite(RigDuration) && RigDuration > 0d;

        public VfxRigPreset RigPreset
        {
            get => _rigPreset;
            set
            {
                _rigPreset = value;
                ClearCheckpoints();
                if (_activeSystem?.Definition is { } definition)
                    _rigDuration = VfxRigMotion.RunLength(value, definition);
                _lastRigOrigin = null;
                ApplyRigTransform();
            }
        }

        public void ApplyRigTransform()
        {
            if (_graphs.Count == 0 || _graphAttachments.Count > 0 || _activeSystem == null) return;

            var step = VfxRigMotion.Evaluate(
                _rigPreset,
                _activeSystem.CurrentTime,
                RigDuration,
                _lastRigOrigin);

            _lastRigOrigin = step.Origin;

            foreach (var graph in _graphs)
            {
                _graphPlacements[graph] = step.Transform;
                Matrix4x4 orientationRoot = Matrix4x4.CreateTranslation(step.Origin) * _worldTransform;
                graph.SetTransform(step.Transform * _worldTransform, orientationRoot);
                graph.SetTarget(Vector3.Transform(step.Target, _worldTransform));
                graph.IsStopped = step.IsStopped;
            }
        }

        public void Initialize(GL gl)
        {
            _renderer = new VfxOpenGlRenderer();
            _renderer.Initialize(gl);
            _ready = true;
        }

        public void SetVfxSystem(VfxSystemModel system) => SetSystem(system);
        public void SetSystem(VfxSystemModel system)
        {
            ClearCheckpoints();
            _isPlaying = false;
            _activeSystem = system;
            _ownerSceneContext = system?.OwnerSceneContext;
            _rigDuration = system?.Definition is { } definition
                ? VfxRigMotion.RunLength(_rigPreset, definition)
                : system?.TotalDuration ?? 0d;
            _graph = null;
            _graphs.Clear();
            _graphPlacements.Clear();
            _scheduledEffectKills.Clear();
            _graphStopTimes.Clear();
            _graphAttachments.Clear();
            _lastRigOrigin = null;
            _boneTransformProvider = null;
            _boneTransformSampler = null;
            if (system != null)
            {
                system.CurrentTime = 0;
            }

            if (_ready)
            {
                _renderer.ClearTextures();
                _textureCache.Clear();
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
            _ownerSceneContext = ownerSceneContext;
            _graph = null;
            _graphs.Clear();
            _graphPlacements.Clear();
            _scheduledEffectKills.Clear();
            _graphStopTimes.Clear();
            _graphAttachments.Clear();

            if (_ready)
            {
                _renderer.ClearTextures();
                _textureCache.Clear();
            }

            var systemsByName = systems
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Value.Name))
                .GroupBy(pair => pair.Value.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.OrdinalIgnoreCase);

            double duration = Math.Max(0.1, animationDuration);
            int graphIndex = 0;

            // 1. Instantiate Idle Effects (continuous character-anchored auras)
            if (idleEffects != null)
            {
                foreach (VfxIdleEffectDefinition idle in idleEffects)
                {
                    VfxSystemDefinition idleDef = null;
                    if (idle.EffectKey != 0 && systems.TryGetValue(idle.EffectKey, out var sys))
                    {
                        idleDef = sys;
                    }
                    else if (resourceMap.TryGetValue(idle.EffectKey, out uint mappedHash) && systems.TryGetValue(mappedHash, out sys))
                    {
                        idleDef = sys;
                    }
                    else if (!string.IsNullOrEmpty(idle.EffectName) && systemsByName.TryGetValue(idle.EffectName, out sys))
                    {
                        idleDef = sys;
                    }

                    if (idleDef == null) continue;

                    var idleGraph = _loadingService.PreparePlaybackGraph(
                        idleDef,
                        systems,
                        resourceMap,
                        searchDirectory,
                        _worldTransform,
                        HashCode.Combine(seed, graphIndex++),
                        _logService,
                        ownerSceneContext);

                    _graphs.Add(idleGraph);
                    _graphPlacements[idleGraph] = Matrix4x4.Identity;
                    _graphAttachments[idleGraph] = new GraphAttachmentInfo
                    {
                        BoneName = idle.BoneName,
                        BoneHash = idle.BoneNameHash,
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
                // boneToSpawnAt children use the same live skeleton as clip/idle attachments.
                graph.SetJointTransformProvider(jointProvider);

                if (_graphAttachments.TryGetValue(graph, out var attachment) && boneTransformProvider != null)
                {
                    Matrix4x4? boneMatrix = null;
                    if (!string.IsNullOrEmpty(attachment.BoneName))
                    {
                        boneMatrix = boneTransformProvider(attachment.BoneName, attachment.BoneHash);
                    }
                    if (!boneMatrix.HasValue && attachment.BoneHash != 0)
                    {
                        boneMatrix = boneTransformProvider(null, attachment.BoneHash);
                    }

                    if (boneMatrix.HasValue)
                    {
                        Matrix4x4 boneTransform = PrepareBoneTransform(boneMatrix.Value);
                        if (attachment.IsDetachable && attachment.HasBoneTransform)
                            boneTransform = attachment.BoneTransform;
                        else if (attachment.TargetBoneHash != 0 &&
                                 boneTransformProvider(null, attachment.TargetBoneHash) is { } rawTarget)
                        {
                            Matrix4x4 target = PrepareBoneTransform(rawTarget);
                            Vector3 forward = target.Translation - boneTransform.Translation;
                            if (forward.LengthSquared() > 1e-8f)
                            {
                                forward = Vector3.Normalize(forward);
                                Vector3 up = Math.Abs(Vector3.Dot(forward, Vector3.UnitY)) > 0.999f
                                    ? Vector3.UnitX : Vector3.UnitY;
                                boneTransform = Matrix4x4.CreateWorld(boneTransform.Translation, -forward, up);
                            }
                        }
                        if (attachment.IsDetachable && _activeSystem?.CurrentTime >= attachment.StartTime)
                        {
                            attachment.BoneTransform = boneTransform;
                            attachment.HasBoneTransform = true;
                        }
                        if (attachment.LocalOffset != Vector3.Zero)
                        {
                            Vector3 scaledOffset = attachment.LocalOffset * CurrentSkinScale;
                            boneTransform = Matrix4x4.CreateTranslation(scaledOffset) * boneTransform;
                        }
                        Matrix4x4 orientationRoot =
                            attachment.BaseTransform * Matrix4x4.CreateTranslation(boneTransform.Translation) * _worldTransform;
                        graph.SetTransform(
                            attachment.BaseTransform * boneTransform * _worldTransform,
                            orientationRoot);
                        continue;
                    }
                }

                if (_graphPlacements.TryGetValue(graph, out var basePlacement))
                {
                    Matrix4x4 orientationRoot = Matrix4x4.CreateTranslation(basePlacement.Translation) * _worldTransform;
                    graph.SetTransform(basePlacement * _worldTransform, orientationRoot);
                }
            }
        }

        private float CurrentSkinScale
            => _ownerSceneContext is { SkinScale: > 0f } context && float.IsFinite(context.SkinScale)
                ? context.SkinScale
                : 1f;

        private Matrix4x4 PrepareBoneTransform(Matrix4x4 transform)
        {
            static Vector3 Normal(Vector3 value, Vector3 fallback)
                => value.LengthSquared() > 1e-8f ? Vector3.Normalize(value) : fallback;

            Vector3 right = Normal(Vector3.TransformNormal(Vector3.UnitX, transform), Vector3.UnitX);
            Vector3 up = Normal(Vector3.TransformNormal(Vector3.UnitY, transform), Vector3.UnitY);
            Vector3 forward = Normal(Vector3.TransformNormal(Vector3.UnitZ, transform), Vector3.UnitZ);
            Vector3 translation = transform.Translation * CurrentSkinScale;
            return new Matrix4x4(
                right.X, right.Y, right.Z, 0f,
                up.X, up.Y, up.Z, 0f,
                forward.X, forward.Y, forward.Z, 0f,
                translation.X, translation.Y, translation.Z, 1f);
        }

        public bool SetEmitterVisibility(int sourceOrder, bool isVisible)
            => _graph?.Root.SetEmitterVisibility(sourceOrder, isVisible) ?? false;

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
            if (_graphAttachments.Count > 0)
            {
                foreach (VfxPlaybackGraphRuntime graph in _graphs)
                {
                    if (!_graphAttachments.ContainsKey(graph))
                    {
                        Matrix4x4 placement = _graphPlacements.GetValueOrDefault(graph, Matrix4x4.Identity);
                        Matrix4x4 orientationRoot = Matrix4x4.CreateTranslation(placement.Translation) * _worldTransform;
                        graph.SetTransform(placement * _worldTransform, orientationRoot);
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
            float speed = (float)Math.Clamp(_activeSystem.Speed, 0.25, 2.0);
            float elapsed = deltaTime * speed;

            AdvanceTo(_activeSystem.CurrentTime + elapsed);

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

        public void Seek(double seconds)
        {
            if (_activeSystem == null) return;
            double maxDuration = HasFinitePlaybackDuration ? RigDuration : 10.0;
            double target = Math.Clamp(seconds, 0, maxDuration);

            if (target + 1e-9 >= _activeSystem.CurrentTime)
            {
                LastSeekRestoreTime = _activeSystem.CurrentTime;
                AdvanceTo(target);
                return;
            }

            if (!TryRestoreCheckpoint(target))
            {
                LastSeekRestoreTime = 0d;
                ResetSimulationToStart();
            }
            AdvanceTo(target);
        }

        private void ResetSimulationToStart()
        {
            _lastRigOrigin = null;
            foreach (VfxPlaybackGraphRuntime graph in _graphs) graph.Reset();
            foreach (var attachment in _graphAttachments.Values) attachment.HasBoneTransform = false;
            _activeSystem.CurrentTime = 0;
            ApplyRigTransform();
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
                Seek(target);
                return;
            }
            AdvanceTo(target);
        }

        public void SetBoneTransformSampler(Func<double, string, uint, Matrix4x4?> sampler)
        {
            ClearCheckpoints();
            _boneTransformSampler = sampler;
        }

        private void AdvanceTo(double target)
        {
            if (!double.IsFinite(target)) return;
            while (_activeSystem.CurrentTime < target)
            {
                double previous = _activeSystem.CurrentTime;
                double next = Math.Min(target, previous + 1d / 60d);
                foreach (var kill in _scheduledEffectKills)
                    if (kill.Time > previous && kill.Time < next) next = kill.Time;
                foreach (double stopTime in _graphStopTimes.Values)
                    if (stopTime > previous && stopTime < next) next = stopTime;
                foreach (GraphAttachmentInfo attachment in _graphAttachments.Values)
                    if (attachment.StartTime > previous && attachment.StartTime < next) next = attachment.StartTime;

                // Land exactly on quarter-second marks so a checkpoint never captures a state
                // from just before/after the time it represents.
                double checkpointBoundary = NextCheckpointBoundary(previous);
                if (checkpointBoundary > previous + 1e-9 && checkpointBoundary < next - 1e-9)
                    next = checkpointBoundary;

                KillGraphsAt(previous);
                _activeSystem.CurrentTime = next;
                ApplyRigTransform();
                if (_boneTransformSampler != null)
                    UpdateBoneTransforms((name, hash) => _boneTransformSampler(next, name, hash));
                else if (_boneTransformProvider != null)
                    UpdateBoneTransforms(_boneTransformProvider);
                foreach (var graph in _graphs) graph.Update((float)(next - previous));
                KillGraphsAt(next);
                TryCaptureCheckpoint(next);
            }
        }

        private double NextCheckpointBoundary(double previous)
        {
            int nextMark = (int)Math.Floor(previous / CheckpointInterval + 1e-9) + 1;
            return nextMark is >= 1 and <= MaximumCheckpointMarks
                ? nextMark * CheckpointInterval
                : double.PositiveInfinity;
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
            int mark = (int)Math.Round(time / CheckpointInterval);
            if (mark < 1 || mark > MaximumCheckpointMarks || _checkpoints.ContainsKey(mark)) return;
            double exact = mark * CheckpointInterval;
            if (Math.Abs(time - exact) > 1e-8) return;

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

        public void Render(Matrix4x4 viewProjection, Matrix4x4 view)
        {
            if (!_ready || _graphs.Count == 0) return;

            UploadPendingResources();
            IEnumerable<VfxPlaybackRuntime.EmitterState> emitters =
                _graphs.SelectMany(graph => graph.Runtimes).SelectMany(runtime => runtime.Emitters).Where(emitter => emitter.IsVisible);
            _renderer.CaptureScene(
                _viewportWidth,
                _viewportHeight,
                false,
                emitters.Any(emitter => VfxOpenGlRenderer.ShouldUseSoftParticles(emitter.Def, true)));
            IReadOnlyList<VfxRenderQueueEntry> renderQueue = VfxRenderQueue.Build(
                _graphs.SelectMany(graph => graph.Runtimes).Select(runtime => runtime.Emitters),
                view);
            _renderer.Render(renderQueue.Where(entry => entry.Emitter.Def.Distortion == null).ToArray(), viewProjection, view, renderQueue);
            var distortionQueue = renderQueue.Where(entry => entry.Emitter.Def.Distortion != null).ToArray();
            if (distortionQueue.Length > 0)
            {
                _renderer.CaptureScene(_viewportWidth, _viewportHeight, true, false);
                _renderer.Render(distortionQueue, viewProjection, view, renderQueue);
            }
        }

        private void UploadPendingResources()
        {
            foreach (VfxPlaybackRuntime runtime in _graphs.SelectMany(graph => graph.Runtimes))
            {
                foreach (VfxPlaybackRuntime.EmitterState emitter in runtime.Emitters)
                {
                    UploadTexture(ref emitter.PendingTexture, texture =>
                    {
                        emitter.Texture = texture;
                        if (emitter.PendingTexture is BitmapSource bitmap)
                        {
                            emitter.TextureWidth = bitmap.PixelWidth;
                            emitter.TextureHeight = bitmap.PixelHeight;
                        }
                    });
                    UploadTexture(ref emitter.PendingTextureMult, texture =>
                    {
                        emitter.TextureMult = texture;
                        if (emitter.PendingTextureMult is BitmapSource bitmap)
                        {
                            emitter.TextureMultWidth = bitmap.PixelWidth;
                            emitter.TextureMultHeight = bitmap.PixelHeight;
                        }
                    });
                    UploadTexture(ref emitter.PendingDistortionTexture, texture => emitter.DistortionTexture = texture);
                    UploadTexture(ref emitter.PendingErosionTexture, texture => emitter.ErosionTexture = texture);
                    UploadTexture(ref emitter.PendingReflectionTexture, texture => emitter.ReflectionTexture = texture);
                    UploadTexture(ref emitter.PendingColorGradient, texture =>
                        emitter.ColorGradientTexture = texture);
                    UploadTexture(ref emitter.PendingPaletteTexture, texture =>
                        emitter.PaletteTexture = texture);

                    if (emitter.PendingMesh is { } mesh)
                    {
                        _renderer.UploadEmitterMesh(emitter, mesh.Positions, mesh.Uvs, mesh.Colors, mesh.Indices);
                        emitter.PendingMesh = null;
                    }
                }
            }
        }

        private void UploadTexture(ref object pending, Action<uint> assign)
        {
            if (pending is not BitmapSource bitmap) return;
            if (!_textureCache.TryGetValue(bitmap, out uint texture))
            {
                texture = UploadBitmap(bitmap);
                _textureCache[bitmap] = texture;
            }
            assign(texture);
            pending = null;
        }

        private uint UploadBitmap(BitmapSource bitmap)
        {
            if (bitmap.Format != PixelFormats.Bgra32)
            {
                var converted = new FormatConvertedBitmap();
                converted.BeginInit();
                converted.Source = bitmap;
                converted.DestinationFormat = PixelFormats.Bgra32;
                converted.EndInit();
                bitmap = converted;
            }

            int width = bitmap.PixelWidth;
            int height = bitmap.PixelHeight;
            int stride = width * 4;
            var pixels = new byte[height * stride];
            bitmap.CopyPixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
            return _renderer.UploadTexture(pixels, width, height);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_ready)
            {
                _renderer.Dispose();
                _ready = false;
            }
            _textureCache.Clear();
            if (_ownsLoadingService)
                _loadingService.Dispose();
            _graph = null;
            _graphs.Clear();
            _graphPlacements.Clear();
            _scheduledEffectKills.Clear();
            _graphStopTimes.Clear();
            _activeSystem = null;
        }
    }
}
