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

        private readonly List<VfxPlaybackGraphRuntime> _graphs = new();
        private readonly Dictionary<VfxPlaybackGraphRuntime, Matrix4x4> _graphPlacements = new();
        private readonly List<(double Time, uint EffectKey)> _scheduledEffectKills = new();
        private readonly Dictionary<VfxPlaybackGraphRuntime, double> _graphStopTimes = new();
        private readonly Dictionary<VfxPlaybackGraphRuntime, GraphAttachmentInfo> _graphAttachments = new();
        private VfxSystemModel _activeSystem;
        private VfxRigPreset _rigPreset = VfxRigPreset.Still;
        private Vector3? _lastRigOrigin;
        private Matrix4x4 _worldTransform = Matrix4x4.Identity;
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

        public VfxRigPreset RigPreset
        {
            get => _rigPreset;
            set
            {
                _rigPreset = value;
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
                _activeSystem.TotalDuration,
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
            _isPlaying = false;
            _activeSystem = system;
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
            systems ??= new Dictionary<uint, VfxSystemDefinition>();
            resourceMap ??= new Dictionary<uint, uint>();
            _isPlaying = false;
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
                    float eventScale = Math.Max(0.01f, compositionEvent.Event.Scale);
                    Matrix4x4 scaleMatrix = Matrix4x4.CreateScale(eventScale);

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
                        scaleMatrix * _worldTransform,
                        HashCode.Combine(seed, graphIndex++),
                        _logService,
                        ownerSceneContext);

                    eventGraph.SetStartDelay(startSeconds);

                    _graphs.Add(eventGraph);
                    _graphPlacements[eventGraph] = scaleMatrix;

                    _graphAttachments[eventGraph] = new GraphAttachmentInfo
                    {
                        BoneName = null,
                        BoneHash = pair.SourceBoneHash,
                        TargetBoneHash = pair.TargetBoneHash,
                        EffectKey = effectKey,
                        StartTime = startSeconds,
                        IsDetachable = cue.IsDetachable,
                        LocalOffset = Vector3.Zero,
                        BaseTransform = scaleMatrix,
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

            foreach (VfxPlaybackGraphRuntime graph in _graphs)
            {
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
                        Matrix4x4 boneTransform = boneMatrix.Value;
                        if (attachment.IsDetachable && attachment.HasBoneTransform)
                            boneTransform = attachment.BoneTransform;
                        else if (attachment.TargetBoneHash != 0 && boneTransformProvider(null, attachment.TargetBoneHash) is { } target)
                        {
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
                            boneTransform = Matrix4x4.CreateTranslation(attachment.LocalOffset) * boneTransform;
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

        public bool SetEmitterVisibility(int sourceOrder, bool isVisible)
            => _graph?.Root.SetEmitterVisibility(sourceOrder, isVisible) ?? false;

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
                    _activeSystem.HasFiniteDuration,
                    _activeSystem.CurrentTime,
                    _activeSystem.TotalDuration,
                    _graphs.All(graph => graph.IsComplete)))
            {
                if (_activeSystem.HasFiniteDuration)
                    _activeSystem.CurrentTime = _activeSystem.TotalDuration;
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
            double maxDuration = _activeSystem.HasFiniteDuration ? _activeSystem.TotalDuration : 10.0;
            double target = Math.Clamp(seconds, 0, maxDuration);
            _lastRigOrigin = null;
            foreach (VfxPlaybackGraphRuntime graph in _graphs) graph.Reset();
            foreach (var attachment in _graphAttachments.Values) attachment.HasBoneTransform = false;
            _activeSystem.CurrentTime = 0;
            ApplyRigTransform();
            if (_boneTransformSampler != null)
                UpdateBoneTransforms((name, hash) => _boneTransformSampler(0, name, hash));
            AdvanceTo(target);
        }

        public void SetBoneTransformSampler(Func<double, string, uint, Matrix4x4?> sampler)
            => _boneTransformSampler = sampler;

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
                KillGraphsAt(previous);
                _activeSystem.CurrentTime = next;
                ApplyRigTransform();
                if (_boneTransformSampler != null)
                    UpdateBoneTransforms((name, hash) => _boneTransformSampler(next, name, hash));
                else if (_boneTransformProvider != null)
                    UpdateBoneTransforms(_boneTransformProvider);
                foreach (var graph in _graphs) graph.Update((float)(next - previous));
                KillGraphsAt(next);
            }
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
