using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Core;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Vfx
{
    /// <summary>
    /// UI-facing playback adapter for the authored VFX graph runtime.
    /// Resource decoding stays on the loader side and all GL uploads happen on
    /// the active viewport context during Render.
    /// </summary>
    public sealed class VfxRenderSession : IDisposable
    {
        private readonly LogService _logService;
        private readonly VfxLoadingService _loadingService = new();
        private readonly Dictionary<BitmapSource, uint> _textureCache = new();
        private VfxOpenGlRenderer _renderer;
        private VfxPlaybackGraphRuntime _graph;
        private VfxSystemModel _activeSystem;
        private Matrix4x4 _worldTransform = Matrix4x4.Identity;
        private bool _isPlaying;
        private bool _ready;
        private uint _viewportWidth;
        private uint _viewportHeight;

        public VfxRenderSession(LogService logService = null)
        {
            _logService = logService;
        }

        internal int LiveParticleCount
        {
            get
            {
                int count = 0;
                if (_graph == null) return count;
                foreach (VfxPlaybackRuntime runtime in _graph.Runtimes)
                {
                    count += runtime.LiveParticleCount;
                }
                return count;
            }
        }

        public VfxSystemModel ActiveSystem => _activeSystem;

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
                    _logService);
            }
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
            _graph?.Reset();
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
            _graph?.SetTransform(_worldTransform);
        }

        public void SetViewportSize(double width, double height)
        {
            _viewportWidth = (uint)Math.Max(0, width);
            _viewportHeight = (uint)Math.Max(0, height);
        }

        public void Update(float deltaTime)
        {
            if (!_isPlaying || _graph == null || _activeSystem == null) return;
            float speed = (float)Math.Clamp(_activeSystem.Speed, 0.25, 2.0);
            float elapsed = deltaTime * speed;
            _graph.Update(elapsed);
            _activeSystem.CurrentTime += elapsed;

            if (ShouldRestartPlayback(
                    _activeSystem.HasFiniteDuration,
                    _activeSystem.CurrentTime,
                    _activeSystem.TotalDuration,
                    _graph.IsComplete))
            {
                _graph.Reset();
                _activeSystem.CurrentTime = 0;
            }
        }

        internal static bool ShouldRestartPlayback(
            bool hasFiniteDuration,
            double currentTime,
            double totalDuration,
            bool graphIsComplete)
            => hasFiniteDuration
                ? currentTime >= totalDuration
                : graphIsComplete;

        public void Seek(double seconds)
        {
            if (_graph == null || _activeSystem == null) return;
            double maxDuration = _activeSystem.HasFiniteDuration ? _activeSystem.TotalDuration : 10.0;
            double target = Math.Clamp(seconds, 0, maxDuration);
            _graph.Reset();
            double simulated = 0;
            const float step = 1f / 60f;
            while (simulated + step < target)
            {
                _graph.Update(step);
                simulated += step;
            }
            float remainder = (float)(target - simulated);
            if (remainder > 0) _graph.Update(remainder);
            _activeSystem.CurrentTime = target;
        }

        public void Render(Matrix4x4 viewProjection, Matrix4x4 view)
        {
            if (!_ready || _graph == null) return;

            UploadPendingResources();
            IEnumerable<VfxPlaybackRuntime.EmitterState> emitters =
                _graph.Runtimes.SelectMany(runtime => runtime.Emitters).Where(emitter => emitter.IsVisible);
            _renderer.CaptureScene(
                _viewportWidth,
                _viewportHeight,
                emitters.Any(emitter => emitter.Def.Distortion != null),
                emitters.Any(emitter => VfxOpenGlRenderer.ShouldUseSoftParticles(emitter.Def, true)));
            foreach (VfxPlaybackRuntime runtime in _graph.Runtimes)
            {
                _renderer.Render(runtime, viewProjection, view);
            }
        }

        private void UploadPendingResources()
        {
            foreach (VfxPlaybackRuntime runtime in _graph.Runtimes)
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
            if (_ready)
            {
                _renderer.Dispose();
                _ready = false;
            }
            _textureCache.Clear();
            _loadingService.ClearCaches();
            _graph = null;
            _activeSystem = null;
        }
    }
}
