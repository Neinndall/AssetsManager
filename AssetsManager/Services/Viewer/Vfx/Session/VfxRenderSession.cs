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
        private readonly List<VfxPlaybackGraphRuntime> _graphs = new();
        private readonly Dictionary<VfxPlaybackGraphRuntime, Matrix4x4> _graphPlacements = new();
        private readonly Dictionary<VfxPlaybackGraphRuntime, double> _graphKillTimes = new();
        private VfxSystemModel _activeSystem;
        private Matrix4x4 _worldTransform = Matrix4x4.Identity;
        private bool _isPlaying;
        private bool _ready;
        private bool _disposed;
        private uint _viewportWidth;
        private uint _viewportHeight;

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
        public bool IsPlaying =>
            _isPlaying &&
            _activeSystem != null &&
            _graphs.Any(graph => graph.Runtimes.Count > 0);

        public void Initialize(GL gl)
        {
            _renderer = new VfxOpenGlRenderer();
            _renderer.Initialize(gl);
            _ready = true;
        }

        public void SetVfxSystem(VfxSystemModel system)
        {
            _isPlaying = false;
            _activeSystem = system;
            _graph = null;
            _graphs.Clear();
            _graphPlacements.Clear();
            _graphKillTimes.Clear();
            if (system != null) system.CurrentTime = 0;

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
            }
        }

        public bool SetAbilityComposition(
            VfxAbilityComposition composition,
            IReadOnlyDictionary<uint, VfxSystemDefinition> systems,
            IReadOnlyDictionary<uint, uint> resourceMap,
            string searchDirectory,
            int seed,
            VfxOwnerSceneContext ownerSceneContext = null)
        {
            ArgumentNullException.ThrowIfNull(composition);
            systems ??= new Dictionary<uint, VfxSystemDefinition>();
            resourceMap ??= new Dictionary<uint, uint>();
            _isPlaying = false;
            _graph = null;
            _graphs.Clear();
            _graphPlacements.Clear();
            _graphKillTimes.Clear();

            if (_ready)
            {
                _renderer.ClearTextures();
                _textureCache.Clear();
            }

            double duration = 0;
            int eventIndex = 0;
            foreach (VfxCompositionEvent compositionEvent in composition.Events)
            {
                if (compositionEvent.System is null) continue;
                float eventScale = Math.Max(0.01f, compositionEvent.Event.Scale);
                var graph = _loadingService.PreparePlaybackGraph(
                    compositionEvent.System,
                    systems,
                    resourceMap,
                    searchDirectory,
                    Matrix4x4.CreateScale(eventScale) * _worldTransform,
                    HashCode.Combine(seed, eventIndex++),
                    _logService,
                    ownerSceneContext);
                float startSeconds = Math.Max(
                    0f,
                    (compositionEvent.Event.StartFrame - composition.StartFrame) * composition.TickDuration);
                graph.SetStartDelay(startSeconds);
                _graphs.Add(graph);
                _graphPlacements[graph] = Matrix4x4.CreateScale(eventScale);
                if (compositionEvent.Event.IsKillEvent &&
                    compositionEvent.Event.EndFrame >= compositionEvent.Event.StartFrame)
                {
                    _graphKillTimes[graph] = Math.Max(
                        startSeconds,
                        (compositionEvent.Event.EndFrame - composition.StartFrame) * composition.TickDuration);
                }
                _graph ??= graph;

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

            _activeSystem = new VfxSystemModel
            {
                Name = $"Ability 0x{composition.SequencePathHash:X8}",
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

        public bool SetEmitterVisibility(int sourceOrder, bool isVisible)
            => _graph?.Root.SetEmitterVisibility(sourceOrder, isVisible) ?? false;

        public void Play()
        {
            _isPlaying = true;
        }

        public void Pause() => _isPlaying = false;

        public void Stop()
        {
            _isPlaying = false;
            foreach (VfxPlaybackGraphRuntime graph in _graphs) graph.Reset();
            if (_activeSystem != null) _activeSystem.CurrentTime = 0;
        }

        public void SetWorldTransform(Vector3 position, float scale)
        {
            float safeScale = Math.Max(0.01f, scale);
            SetWorldTransform(Matrix4x4.CreateScale(safeScale) * Matrix4x4.CreateTranslation(position));
        }

        public void SetWorldTransform(Matrix4x4 transform)
        {
            _worldTransform = transform;
            foreach (VfxPlaybackGraphRuntime graph in _graphs)
                graph.SetTransform(_graphPlacements.GetValueOrDefault(graph, Matrix4x4.Identity) * _worldTransform);
        }

        public void SetViewportSize(double width, double height)
        {
            _viewportWidth = (uint)Math.Max(0, width);
            _viewportHeight = (uint)Math.Max(0, height);
        }

        public void Update(float deltaTime)
        {
            if (!_isPlaying || _graphs.Count == 0 || _activeSystem == null) return;
            float speed = (float)Math.Clamp(_activeSystem.Speed, 0.25, 2.0);
            float elapsed = deltaTime * speed;
            foreach (VfxPlaybackGraphRuntime graph in _graphs) graph.Update(elapsed);
            _activeSystem.CurrentTime += elapsed;
            KillGraphsAt(_activeSystem.CurrentTime);

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
            if (_graphs.Count == 0 || _activeSystem == null) return;
            double maxDuration = _activeSystem.HasFiniteDuration ? _activeSystem.TotalDuration : 10.0;
            double target = Math.Clamp(seconds, 0, maxDuration);
            foreach (VfxPlaybackGraphRuntime graph in _graphs) graph.Reset();
            double simulated = 0;
            const float step = 1f / 60f;
            while (simulated + step < target)
            {
                foreach (VfxPlaybackGraphRuntime graph in _graphs) graph.Update(step);
                simulated += step;
                KillGraphsAt(simulated);
            }
            float remainder = (float)(target - simulated);
            if (remainder > 0)
            {
                foreach (VfxPlaybackGraphRuntime graph in _graphs) graph.Update(remainder);
                KillGraphsAt(target);
            }
            _activeSystem.CurrentTime = target;
        }

        private void KillGraphsAt(double seconds)
        {
            foreach (var (graph, killTime) in _graphKillTimes)
            {
                if (seconds >= killTime)
                    graph.Kill();
            }
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
                emitters.Any(emitter => emitter.Def.Distortion != null),
                emitters.Any(emitter => VfxOpenGlRenderer.ShouldUseSoftParticles(emitter.Def, true)));
            IReadOnlyList<VfxRenderQueueEntry> renderQueue = VfxRenderQueue.Build(
                _graphs.SelectMany(graph => graph.Runtimes).Select(runtime => runtime.Emitters),
                view);
            _renderer.Render(renderQueue, viewProjection, view);
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
            _graphKillTimes.Clear();
            _activeSystem = null;
        }
    }
}
