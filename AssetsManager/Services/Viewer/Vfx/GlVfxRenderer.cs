using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
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
    public sealed class GlVfxRenderer : IDisposable
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
        private SceneModel _attachedMeshSource;
        private AttachedMeshBinding _attachedMeshBinding;

        private sealed record AttachedMeshSegment(MeshGeometry3D Geometry, int VertexOffset, int VertexCount);

        private sealed class AttachedMeshBinding
        {
            public required float[] Positions { get; init; }
            public required float[] Uvs { get; init; }
            public required uint[] Indices { get; init; }
            public required IReadOnlyList<AttachedMeshSegment> Segments { get; init; }

            public bool UpdatePositions()
            {
                foreach (AttachedMeshSegment segment in Segments)
                {
                    if (segment.Geometry.Positions.Count != segment.VertexCount) return false;
                    for (int index = 0; index < segment.VertexCount; index++)
                    {
                        Point3D position = segment.Geometry.Positions[index];
                        int target = (segment.VertexOffset + index) * 3;
                        Positions[target] = (float)position.X;
                        Positions[target + 1] = (float)position.Y;
                        Positions[target + 2] = (float)position.Z;
                    }
                }
                return true;
            }
        }

        public GlVfxRenderer(LogService logService = null)
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
                    HashCode.Combine(system.Definition.PathHash, system.Name),
                    _logService);
            }
        }

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

        public void SetAttachedMeshSource(SceneModel model)
        {
            if (ReferenceEquals(_attachedMeshSource, model)) return;
            _attachedMeshSource = model;
            _attachedMeshBinding = null;
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
            _renderer.CaptureScene(_viewportWidth, _viewportHeight);
            foreach (VfxPlaybackRuntime runtime in _graph.Runtimes)
            {
                _renderer.Render(runtime, viewProjection, view);
            }
        }

        private void UploadPendingResources()
        {
            SyncAttachedMeshes();
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

                    if (emitter.PendingMesh is { } mesh)
                    {
                        _renderer.UploadEmitterMesh(emitter, mesh.Positions, mesh.Uvs, mesh.Indices);
                        emitter.PendingMesh = null;
                    }
                    if (emitter.MeshAnimation != null && emitter.MeshVao != 0)
                    {
                        float[] positions = emitter.MeshAnimation.Evaluate(emitter.EmitterAge);
                        _renderer.UpdateEmitterMeshPositions(emitter, positions);
                    }
                }
            }
        }

        private void SyncAttachedMeshes()
        {
            if (_attachedMeshSource?.Parts == null) return;
            VfxPlaybackRuntime.EmitterState[] attachedEmitters = _graph.Runtimes
                .SelectMany(runtime => runtime.Emitters)
                .Where(emitter => emitter.Def.PrimitiveKind == VfxPrimitiveKind.AttachedMesh)
                .ToArray();
            if (attachedEmitters.Length == 0) return;

            _attachedMeshBinding ??= CreateAttachedMeshBinding(_attachedMeshSource);
            if (_attachedMeshBinding == null || !_attachedMeshBinding.UpdatePositions()) return;

            Matrix4x4 world = CreateSceneWorldTransform(_attachedMeshSource);
            foreach (VfxPlaybackRuntime.EmitterState emitter in attachedEmitters)
            {
                emitter.UsesExternalAttachedMesh = true;
                emitter.AttachedMeshWorld = world;
                if (emitter.MeshVao == 0)
                {
                    _renderer.UploadEmitterMesh(
                        emitter,
                        _attachedMeshBinding.Positions,
                        _attachedMeshBinding.Uvs,
                        _attachedMeshBinding.Indices);
                }
                else
                {
                    _renderer.UpdateEmitterMeshPositions(emitter, _attachedMeshBinding.Positions);
                }
            }
        }

        private static AttachedMeshBinding CreateAttachedMeshBinding(SceneModel model)
        {
            var parts = model.Parts
                .Where(part => part.IsVisible && part.Geometry?.Geometry is MeshGeometry3D)
                .Select(part => (MeshGeometry3D)part.Geometry.Geometry)
                .Where(geometry => geometry.Positions.Count > 0 && geometry.TriangleIndices.Count > 0)
                .ToArray();
            if (parts.Length == 0) return null;

            int vertexCount = parts.Sum(geometry => geometry.Positions.Count);
            int indexCount = parts.Sum(geometry => geometry.TriangleIndices.Count);
            var positions = new float[vertexCount * 3];
            var uvs = new float[vertexCount * 2];
            var indices = new uint[indexCount];
            var segments = new List<AttachedMeshSegment>(parts.Length);
            int vertexOffset = 0;
            int indexOffset = 0;

            foreach (MeshGeometry3D geometry in parts)
            {
                int partVertexCount = geometry.Positions.Count;
                segments.Add(new AttachedMeshSegment(geometry, vertexOffset, partVertexCount));
                for (int index = 0; index < partVertexCount; index++)
                {
                    if (index >= geometry.TextureCoordinates.Count) continue;
                    System.Windows.Point uv = geometry.TextureCoordinates[index];
                    int target = (vertexOffset + index) * 2;
                    uvs[target] = (float)uv.X;
                    uvs[target + 1] = (float)uv.Y;
                }
                for (int index = 0; index < geometry.TriangleIndices.Count; index++)
                    indices[indexOffset + index] = (uint)(vertexOffset + geometry.TriangleIndices[index]);

                vertexOffset += partVertexCount;
                indexOffset += geometry.TriangleIndices.Count;
            }

            var binding = new AttachedMeshBinding
            {
                Positions = positions,
                Uvs = uvs,
                Indices = indices,
                Segments = segments,
            };
            binding.UpdatePositions();
            return binding;
        }

        internal static Matrix4x4 CreateSceneWorldTransform(SceneModel model)
        {
            float pitch = (float)(model.RotationX * (Math.PI / 180.0));
            float yaw = (float)(model.RotationY * (Math.PI / 180.0));
            float roll = (float)(model.RotationZ * (Math.PI / 180.0));
            return Matrix4x4.CreateScale((float)model.Scale) *
                   Matrix4x4.CreateFromYawPitchRoll(yaw, pitch, roll) *
                   Matrix4x4.CreateTranslation(
                       (float)model.PositionX,
                       (float)model.PositionY,
                       (float)model.PositionZ);
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
            _attachedMeshSource = null;
            _attachedMeshBinding = null;
        }
    }
}
