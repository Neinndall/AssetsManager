using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Session
{
    public sealed partial class VfxRenderSession
    {
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

        private void QueueGpuResourcePurge()
            => _purgeGpuResourcesBeforeNextFrame = true;

        /// <summary>
        /// Performs queued GL resource teardown. Call only from the viewport render callback while
        /// its OpenGL context is current. This also handles a cleared/empty session that has no graph
        /// left to enter PrepareRenderFrame.
        /// </summary>
        internal void ProcessPendingGpuState()
        {
            if (!_ready || !_purgeGpuResourcesBeforeNextFrame) return;
            _renderer.ClearTextures();
            _gpuResourceUploader.Clear();
            _purgeGpuResourcesBeforeNextFrame = false;
        }

        /// <summary>Why the emitter draws with the stock program, or null when it uses the game's.</summary>
        internal string GameParticleFallback(VfxEmitterDefinition emitter, bool mesh) =>
            _renderer == null ? "Renderer not initialized." : _renderer.GameParticleFallback(emitter, mesh);

        internal void SetSun(MapSunData sun)
        {
            if (_renderer != null) _renderer.Sun = sun;
        }

        internal bool PrepareRenderFrame(
            Matrix4x4 viewProjection,
            Matrix4x4 view,
            VfxPreviewViewMode viewMode = VfxPreviewViewMode.Lit,
            bool wireOverlay = false)
        {
            ProcessPendingGpuState();
            if (!_ready || _graphs.Count == 0)
                return false;

            _gpuResourceUploader.UploadPendingResources(_graphs, _renderer);
            _renderSources.Clear();
            bool needsSoftParticles = false;
            bool needsSceneColor = false;
            foreach (VfxPlaybackGraphRuntime graph in _graphs)
            {
                foreach (VfxPlaybackRuntime runtime in graph.Runtimes)
                {
                    IReadOnlyList<VfxPlaybackRuntime.EmitterState> emitters = runtime.Emitters;
                    _renderSources.Add(emitters);
                    if (needsSoftParticles && needsSceneColor) continue;

                    foreach (VfxPlaybackRuntime.EmitterState emitter in emitters)
                    {
                        if (!emitter.IsVisible || emitter.InstanceCount == 0) continue;
                        var inputs = _renderer.SceneInputsFor(emitter.Def);
                        needsSoftParticles |= inputs.Depth;
                        needsSceneColor |= inputs.Color;
                    }
                }
            }

            _renderer.CaptureScene(
                _viewportWidth,
                _viewportHeight,
                needsSceneColor,
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

        public IDisposable BeginPreparedRenderBatch() => _renderer.BeginRenderBatch();

        public void RenderPreparedColorPass()
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

        public void CapturePreparedDistortionFrame()
        {
            if (HasPreparedDistortionPass)
                _renderer.CaptureScene(_viewportWidth, _viewportHeight, true, false);
        }

        public void RenderPreparedDistortionPass()
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
