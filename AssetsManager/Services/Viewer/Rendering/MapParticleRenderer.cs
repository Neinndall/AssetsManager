using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Services.Viewer.Runtime;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Services.Viewer.Vfx.Session;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Rendering
{
    /// <summary>
    /// Scene-scoped renderer for MAP VFX. One OpenGL renderer and one GPU resource uploader are
    /// shared by every particle placement while the simulation graphs remain independent.
    /// </summary>
    internal sealed class MapParticleRenderer : IDisposable
    {
        private readonly VfxGpuResourceUploader _uploader = new();
        private readonly List<IReadOnlyList<VfxPlaybackRuntime.EmitterState>> _sources = new();
        private readonly List<VfxRenderQueueEntry> _queue = new();
        private readonly List<VfxRenderQueueEntry> _shaded = new();
        private readonly List<VfxRenderQueueEntry> _distortion = new();
        private readonly List<VfxPlaybackGraphRuntime> _graphs = new();
        private readonly Dictionary<object, int> _graphOrders = new();
        private VfxOpenGlRenderer _renderer;
        private Matrix4x4 _preparedViewProjection;
        private Matrix4x4 _preparedView;
        private uint _preparedViewportWidth;
        private uint _preparedViewportHeight;
        private bool _preparedShaded;
        private bool _preparedWireframe;
        private float _preparedWireOpacity;
        private bool _ready;

        internal void Initialize(GL gl)
        {
            ArgumentNullException.ThrowIfNull(gl);
            if (_ready) return;
            _renderer = new VfxOpenGlRenderer();
            _renderer.Initialize(gl);
            _ready = true;
        }

        internal void Render(
            IReadOnlyList<MapParticleRuntime> runtimes,
            Matrix4x4 viewProjection,
            Matrix4x4 view,
            uint viewportWidth,
            uint viewportHeight,
            VfxPreviewViewMode viewMode = VfxPreviewViewMode.Lit,
            bool wireOverlay = false)
        {
            if (!PrepareRenderFrame(runtimes, viewProjection, view, viewportWidth, viewportHeight, viewMode, wireOverlay))
                return;

            using IDisposable renderBatch = BeginPreparedRenderBatch();
            RenderPreparedColorPass();
            CapturePreparedDistortionFrame();
            RenderPreparedDistortionPass();
        }

        internal bool PrepareRenderFrame(
            IReadOnlyList<MapParticleRuntime> runtimes,
            Matrix4x4 viewProjection,
            Matrix4x4 view,
            uint viewportWidth,
            uint viewportHeight,
            VfxPreviewViewMode viewMode = VfxPreviewViewMode.Lit,
            bool wireOverlay = false)
        {
            if (!_ready || runtimes == null || runtimes.Count == 0)
                return false;

            _sources.Clear();
            _graphs.Clear();
            bool needsSoftParticles = false;
            foreach (MapParticleRuntime mapRuntime in runtimes)
            {
                VfxPlaybackGraphRuntime graph = mapRuntime?.Graph;
                if (graph == null) continue;
                _graphs.Add(graph);
                foreach (VfxPlaybackRuntime runtime in graph.Runtimes)
                {
                    IReadOnlyList<VfxPlaybackRuntime.EmitterState> emitters = runtime.Emitters;
                    _sources.Add(emitters);
                    if (needsSoftParticles) continue;
                    foreach (VfxPlaybackRuntime.EmitterState emitter in emitters)
                    {
                        if (!emitter.IsVisible ||
                            !VfxOpenGlRenderer.ShouldUseSoftParticles(emitter.Def, true))
                        {
                            continue;
                        }
                        needsSoftParticles = true;
                        break;
                    }
                }
            }

            if (_sources.Count == 0)
                return false;

            _uploader.UploadPendingResources(_graphs, _renderer);
            _renderer.CaptureScene(viewportWidth, viewportHeight, false, needsSoftParticles);
            VfxRenderQueue.BuildInto(_sources, _queue, _graphOrders);

            _preparedView = MapParticleSemantics.ViewportView(view);
            _preparedViewProjection = MapParticleSemantics.ViewportViewProjection(viewProjection);
            _preparedViewportWidth = viewportWidth;
            _preparedViewportHeight = viewportHeight;
            var previewPasses = VfxRenderSession.ResolvePreviewPasses(viewMode, wireOverlay, _renderer.SupportsWireframe);
            _preparedShaded = previewPasses.Shaded;
            _preparedWireframe = previewPasses.Wireframe;
            _preparedWireOpacity = previewPasses.WireOpacity;

            _shaded.Clear();
            _distortion.Clear();
            foreach (VfxRenderQueueEntry entry in _queue)
            {
                if (entry.Emitter.Def.DrawsAsDistortion)
                    _distortion.Add(entry);
                else
                    _shaded.Add(entry);
            }
            return _queue.Count > 0;
        }

        internal IDisposable BeginPreparedRenderBatch() => _renderer.BeginRenderBatch();

        internal void RenderPreparedColorPass()
        {
            if (_preparedShaded && _shaded.Count > 0)
                _renderer.Render(_shaded, _preparedViewProjection, _preparedView);
            if (_preparedWireframe && _queue.Count > 0)
            {
                _renderer.Render(
                    _queue,
                    _preparedViewProjection,
                    _preparedView,
                    wireframePass: true,
                    wireframeOpacity: _preparedWireOpacity);
            }
        }

        internal bool HasPreparedDistortionPass => _preparedShaded && _distortion.Count > 0;

        internal void CapturePreparedDistortionFrame()
        {
            if (HasPreparedDistortionPass)
                _renderer.CaptureScene(_preparedViewportWidth, _preparedViewportHeight, true, false);
        }

        internal void RenderPreparedDistortionPass()
        {
            if (HasPreparedDistortionPass)
                _renderer.Render(_distortion, _preparedViewProjection, _preparedView);
        }

        internal void Clear()
        {
            if (!_ready) return;
            _renderer.ClearTextures();
            _uploader.Clear();
            _sources.Clear();
            _queue.Clear();
            _shaded.Clear();
            _distortion.Clear();
            _graphs.Clear();
            _graphOrders.Clear();
        }

        public void Dispose()
        {
            if (!_ready) return;
            _renderer.Dispose();
            _renderer = null;
            _uploader.Clear();
            _sources.Clear();
            _queue.Clear();
            _shaded.Clear();
            _distortion.Clear();
            _graphs.Clear();
            _graphOrders.Clear();
            _ready = false;
        }
    }
}
