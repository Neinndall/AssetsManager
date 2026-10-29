using System;
using System.Collections.Generic;
using AssetsManager.Services.Viewer.Vfx.Semantics;

namespace AssetsManager.Services.Viewer.Rendering
{
    /// <summary>
    /// Prepared scene particles with ground colour before characters and shared scene captures for later phases.
    /// </summary>
    internal interface IPreparedParticlePass
    {
        bool HasPreparedShadowCasters => false;
        bool HasPreparedGroundPass => false;
        bool HasPreparedEarlyDistortion => false;
        bool NeedsPreparedSceneCapture => false;
        void CapturePreparedSceneInputs() { }
        void RenderPreparedShadowPass(System.Numerics.Matrix4x4 lightViewProjection, System.Numerics.Matrix4x4 eyeView) { }
        void PrepareStencilScene(VfxStencilScene scene) { }
        IDisposable BeginPreparedRenderBatch();
        void CapturePreparedEarlyDistortionFrame() { }
        void RenderPreparedEarlyDistortionPass() { }
        void RenderPreparedGroundPass() { }
        void RenderPreparedColorPass();
        void CapturePreparedDistortionFrame();
        void RenderPreparedDistortionPass();
        void RenderPreparedPostColorPass() { }
    }

    internal static class PreparedParticlePasses
    {
        /// <summary>
        /// Runs every prepared owner phase by phase and closes their GL state batches in reverse order.
        /// <paramref name="batches"/> is caller-owned scratch storage reused across frames.
        /// </summary>
        internal static void Render(IReadOnlyList<IPreparedParticlePass> passes, List<IDisposable> batches,
            Action renderCharacters = null, Action<Action> renderSceneDepth = null, bool captureSceneBeforeGround = false)
        {
            if (passes == null || passes.Count == 0)
            {
                renderCharacters?.Invoke();
                return;
            }

            var stencilScene = new VfxStencilScene();
            bool ground = false;
            bool earlyDistortion = false;
            bool needsScene = captureSceneBeforeGround;
            foreach (IPreparedParticlePass pass in passes)
            {
                pass.PrepareStencilScene(stencilScene);
                ground |= pass.HasPreparedGroundPass;
                earlyDistortion |= pass.HasPreparedEarlyDistortion;
                needsScene |= pass.NeedsPreparedSceneCapture;
            }

            bool groundBeforeCharacters = ground && !earlyDistortion;
            bool depthPrepared = groundBeforeCharacters && needsScene && renderSceneDepth != null;
            if (depthPrepared) renderSceneDepth(CaptureDepth);

            // End particle state scopes before another renderer binds character programs and textures.
            foreach (IPreparedParticlePass pass in passes)
            {
                if (!groundBeforeCharacters || !pass.HasPreparedGroundPass) continue;
                using IDisposable groundBatch = pass.BeginPreparedRenderBatch();
                pass.RenderPreparedGroundPass();
            }
            renderCharacters?.Invoke();

            batches.Clear();
            try
            {
                foreach (IPreparedParticlePass pass in passes)
                    pass.PrepareStencilScene(stencilScene);
                foreach (IPreparedParticlePass pass in passes)
                    batches.Add(pass.BeginPreparedRenderBatch());
                if (!depthPrepared) CaptureDepth();
                foreach (IPreparedParticlePass pass in passes)
                    pass.CapturePreparedEarlyDistortionFrame();
                foreach (IPreparedParticlePass pass in passes)
                    pass.RenderPreparedEarlyDistortionPass();
                if (earlyDistortion)
                    foreach (IPreparedParticlePass pass in passes)
                        pass.RenderPreparedGroundPass();
                foreach (IPreparedParticlePass pass in passes)
                    pass.RenderPreparedColorPass();
                foreach (IPreparedParticlePass pass in passes)
                    pass.CapturePreparedDistortionFrame();
                foreach (IPreparedParticlePass pass in passes)
                    pass.RenderPreparedDistortionPass();
                foreach (IPreparedParticlePass pass in passes)
                    pass.RenderPreparedPostColorPass();
            }
            finally
            {
                for (int index = batches.Count - 1; index >= 0; index--)
                    batches[index]?.Dispose();
                batches.Clear();
            }

            void CaptureDepth()
            {
                foreach (IPreparedParticlePass pass in passes)
                    pass.CapturePreparedSceneInputs();
            }
        }
    }
}
