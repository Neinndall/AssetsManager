using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Services.Viewer.Runtime;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using AssetsManager.Services.Viewer.Vfx.Runtime;
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
            uint viewportHeight)
        {
            if (!_ready || runtimes == null || runtimes.Count == 0)
                return;

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
                return;

            _uploader.UploadPendingResources(_graphs, _renderer);
            _renderer.CaptureScene(viewportWidth, viewportHeight, false, needsSoftParticles);
            VfxRenderQueue.BuildInto(_sources, _queue, _graphOrders);

            Matrix4x4 engineView = MapParticleSemantics.ViewportView(view);
            Matrix4x4 engineViewProjection = MapParticleSemantics.ViewportViewProjection(viewProjection);

            _shaded.Clear();
            _distortion.Clear();
            foreach (VfxRenderQueueEntry entry in _queue)
            {
                if (entry.Emitter.Def.Distortion != null)
                    _distortion.Add(entry);
                else
                    _shaded.Add(entry);
            }

            if (_shaded.Count > 0)
                _renderer.Render(_shaded, engineViewProjection, engineView);

            if (_distortion.Count > 0)
            {
                _renderer.CaptureScene(viewportWidth, viewportHeight, true, false);
                _renderer.Render(_distortion, engineViewProjection, engineView);
            }
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
