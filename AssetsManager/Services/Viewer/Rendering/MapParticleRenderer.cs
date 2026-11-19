using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Services.Viewer.Runtime;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Services.Viewer.Vfx.Session;
using AssetsManager.Services.Viewer.Vfx.Semantics;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Rendering
{
    /// <summary>
    /// Scene-scoped renderer for MAP VFX. One OpenGL renderer and one GPU resource uploader are
    /// shared by every particle placement while the simulation graphs remain independent.
    /// </summary>
    internal sealed class MapParticleRenderer : IDisposable, IPreparedParticlePass
    {
        private readonly VfxGpuResourceUploader _uploader = new();
        private readonly List<IReadOnlyList<VfxPlaybackRuntime.EmitterState>> _sources = new();
        private readonly List<VfxRenderQueueEntry> _queue = new();
        private readonly List<VfxRenderQueueEntry> _shaded = new();
        private readonly List<VfxRenderQueueEntry> _ground = new();
        private readonly List<VfxRenderQueueEntry> _distortion = new();
        private readonly List<VfxRenderQueueEntry> _earlyDistortion = new();
        private readonly List<VfxRenderQueueEntry> _postColor = new();
        private readonly List<VfxPlaybackGraphRuntime> _graphs = new();
        private readonly Dictionary<object, int> _graphOrders = new();
        private readonly Dictionary<object, object> _drawGroups = new();
        private readonly Dictionary<object, int> _drawCapacities = new();
        private VfxOpenGlRenderer _renderer;
        private Matrix4x4 _preparedViewProjection;
        private Matrix4x4 _preparedView;
        private uint _preparedViewportWidth;
        private uint _preparedViewportHeight;
        private bool _preparedShaded;
        private VfxOpenGlRenderer.VfxPreparedSceneInputs _preparedInputs;
        private bool _preparedWireframe;
        private float _preparedWireOpacity;
        private bool _ready;
        private VfxStencilScene _preparedStencilScene;
        internal VfxEffectsQuality EffectsQuality { get; set; } = VfxEffectsQuality.VeryHigh;
        bool IPreparedParticlePass.HasPreparedGroundPass => _preparedShaded && _ground.Exists(
            entry => entry.Emitter.IsVisible && !entry.Emitter.Def.Disabled && entry.Emitter.InstanceCount > 0);
        bool IPreparedParticlePass.HasPreparedEarlyDistortion => _preparedShaded && _earlyDistortion.Count > 0;
        bool IPreparedParticlePass.NeedsPreparedSceneCapture => _preparedShaded && (_preparedInputs.NeedsDepth || _preparedInputs.NeedsInitialColor);
        bool IPreparedParticlePass.NeedsPreparedPostGroundColorCapture => _preparedShaded && _preparedInputs.NeedsPostGroundColor;
        bool IPreparedParticlePass.NeedsPreparedPostColorCapture => _preparedShaded && _preparedInputs.NeedsPostColor;
        public void CapturePreparedSceneInputs()
            => _renderer.CapturePreparedInitialInputs(_preparedViewportWidth, _preparedViewportHeight, _preparedShaded, in _preparedInputs);
        public void CapturePreparedPostGroundColorInputs()
        {
            if (_preparedShaded && _preparedInputs.NeedsPostGroundColor)
                _renderer.CaptureScene(_preparedViewportWidth, _preparedViewportHeight, true, false);
        }
        public void CapturePreparedPostColorInputs()
        {
            if (_preparedShaded && _preparedInputs.NeedsPostColor)
                _renderer.CaptureScene(_preparedViewportWidth, _preparedViewportHeight, true, false);
        }
        bool IPreparedParticlePass.HasPreparedShadowCasters => _preparedShaded && _queue.Exists(e =>
            e.Emitter.IsVisible && !e.Emitter.Def.Disabled && e.Emitter.InstanceCount > 0 && VfxRenderPhaseSemantics.CastsShadow(e.Emitter.Def));
        void IPreparedParticlePass.RenderPreparedShadowPass(Matrix4x4 lightViewProjection, Matrix4x4 eyeView)
        {
            if (_preparedShaded) _renderer.RenderShadows(_queue, lightViewProjection, eyeView);
        }
        internal bool CacheDrawBindings { set => _renderer.CacheDrawBindings = value; }
        internal (int Programs, int UniformBuffers) LastDrawBindingCounts => _renderer.LastDrawBindingCounts;

        internal void Initialize(GL gl, AssetsManager.Utils.AppSettings settings = null)
        {
            ArgumentNullException.ThrowIfNull(gl);
            if (_ready) return;
            _renderer = new VfxOpenGlRenderer();
            _renderer.Initialize(gl, settings);
            _ready = true;
        }

        internal void Render(
            IReadOnlyList<MapParticleRuntime> runtimes,
            Matrix4x4 viewProjection,
            Matrix4x4 view,
            uint viewportWidth,
            uint viewportHeight,
            StudioViewMode viewMode = StudioViewMode.Lit,
            bool wireOverlay = false)
        {
            if (!PrepareRenderFrame(runtimes, viewProjection, view, viewportWidth, viewportHeight, viewMode, wireOverlay))
                return;

            using IDisposable renderBatch = BeginPreparedRenderBatch();
            CapturePreparedSceneInputs();
            CapturePreparedEarlyDistortionFrame();
            RenderPreparedEarlyDistortionPass();
            RenderPreparedGroundPass();
            CapturePreparedPostGroundColorInputs();
            RenderPreparedColorPass();
            CapturePreparedDistortionFrame();
            RenderPreparedDistortionPass();
            CapturePreparedPostColorInputs();
            RenderPreparedPostColorPass();
        }

        internal void SetSun(MapSunData sun)
        {
            if (_renderer != null) _renderer.Sun = sun;
        }

        internal void SetTerrainDepth(uint texture, uint width, uint height)
            => _renderer?.SetTerrainDepth(texture, width, height);

        internal bool PrepareRenderFrame(
            IReadOnlyList<MapParticleRuntime> runtimes,
            Matrix4x4 viewProjection,
            Matrix4x4 view,
            uint viewportWidth,
            uint viewportHeight,
            StudioViewMode viewMode = StudioViewMode.Lit,
            bool wireOverlay = false)
        {
            _preparedStencilScene = null;
            if (!_ready) return false;
            if (runtimes == null || runtimes.Count == 0)
            {
                _renderer.InvalidateSceneCaptures();
                return false;
            }

            _sources.Clear();
            _graphs.Clear();
            _drawGroups.Clear();
            _drawCapacities.Clear();
            foreach (MapParticleRuntime mapRuntime in runtimes)
            {
                VfxPlaybackGraphRuntime graph = mapRuntime?.Graph;
                if (graph == null) continue;
                if (graph.EffectsQuality != EffectsQuality)
                {
                    float time = graph.Root.CurrentTime;
                    graph.SetEffectsQuality(EffectsQuality);
                    graph.Reset();
                    for (float t = 0; t < time; t += 1f / 60f) graph.Update(MathF.Min(1f / 60f, time - t));
                }
                _graphs.Add(graph);
                _drawGroups[graph] = mapRuntime.DrawGroupKey;
                _drawCapacities[mapRuntime.DrawGroupKey] = mapRuntime.SharedDrawCapacity;
                foreach (VfxPlaybackRuntime runtime in graph.Runtimes)
                    _sources.Add(runtime.Emitters);
            }

            if (_sources.Count == 0)
            {
                _renderer.InvalidateSceneCaptures();
                return false;
            }

            _uploader.UploadPendingResources(_graphs, _renderer);
            _renderer.SharedDrawGroups = _drawGroups;
            _renderer.SharedDrawCapacities = _drawCapacities;
            VfxRenderQueue.BuildInto(_sources, _queue, _graphOrders, _drawGroups);

            _preparedView = MapParticleSemantics.ViewportView(view);
            _preparedViewProjection = MapParticleSemantics.ViewportViewProjection(viewProjection);
            _preparedViewportWidth = viewportWidth;
            _preparedViewportHeight = viewportHeight;
            var previewPasses = VfxRenderSession.ResolvePreviewPasses(viewMode, wireOverlay, _renderer.SupportsWireframe);
            _preparedShaded = previewPasses.Shaded;
            _preparedWireframe = previewPasses.Wireframe;
            _preparedWireOpacity = previewPasses.WireOpacity;

            _preparedInputs = _renderer.BuildPreparedPhaseQueues(
                _queue,
                _ground,
                _shaded,
                _earlyDistortion,
                _distortion,
                _postColor);
            _renderer.SyncPreparedSceneCaptures(_preparedShaded, in _preparedInputs);
            return _queue.Count > 0;
        }

        public void PrepareStencilScene(VfxStencilScene scene)
        {
            foreach (var graph in _graphs) graph.AddStencilClaims(scene, _preparedShaded);
            _preparedStencilScene = scene;
        }

        public IDisposable BeginPreparedRenderBatch()
        {
            if (_preparedStencilScene == null) PrepareStencilScene(new VfxStencilScene());
            VfxStencilScene scene = _preparedStencilScene;
            _preparedStencilScene = null;
            return _renderer.BeginRenderBatch(scene);
        }

        public void CapturePreparedEarlyDistortionFrame()
        {
            if (_preparedShaded && _earlyDistortion.Count > 0)
                _renderer.CaptureScene(_preparedViewportWidth, _preparedViewportHeight, true, false);
        }

        public void RenderPreparedEarlyDistortionPass()
        {
            if (_preparedShaded && _earlyDistortion.Count > 0)
                _renderer.Render(_earlyDistortion, _preparedViewProjection, _preparedView);
        }

        public void RenderPreparedPostColorPass()
        {
            if (_preparedShaded && _postColor.Count > 0)
                _renderer.Render(_postColor, _preparedViewProjection, _preparedView);
            // Wire twins draw over both colour phases and the late warp.
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

        public void RenderPreparedColorPass()
        {
            if (_preparedShaded && _shaded.Count > 0)
                _renderer.Render(_shaded, _preparedViewProjection, _preparedView);
        }

        public void RenderPreparedGroundPass()
        {
            if (_preparedShaded && _ground.Count > 0)
                _renderer.Render(_ground, _preparedViewProjection, _preparedView);
        }

        internal bool HasPreparedDistortionPass => _preparedShaded && _distortion.Count > 0;

        public void CapturePreparedDistortionFrame()
        {
            if (HasPreparedDistortionPass)
                _renderer.CaptureScene(_preparedViewportWidth, _preparedViewportHeight, true, false);
        }

        public void RenderPreparedDistortionPass()
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
            _ground.Clear();
            _distortion.Clear();
            _earlyDistortion.Clear();
            _postColor.Clear();
            _graphs.Clear();
            _graphOrders.Clear();
            _drawGroups.Clear();
            _drawCapacities.Clear();
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
            _ground.Clear();
            _distortion.Clear();
            _earlyDistortion.Clear();
            _postColor.Clear();
            _graphs.Clear();
            _graphOrders.Clear();
            _drawGroups.Clear();
            _drawCapacities.Clear();
            _ready = false;
        }
    }
}
