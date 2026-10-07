using System;
using System.Collections.Generic;
using AssetsManager.Services.Viewer.Vfx.Semantics;

namespace AssetsManager.Services.Viewer.Rendering
{
    /// <summary>
    /// A particle renderer whose frame was already prepared (soft-depth grab done). The Studio drives
    /// every participant through early distortion, under colour, late distortion and over colour,
    /// keeping captures and draws in the same global order across independent owners.
    /// </summary>
    internal interface IPreparedParticlePass
    {
        void PrepareStencilScene(VfxStencilScene scene) { }
        IDisposable BeginPreparedRenderBatch();
        void CapturePreparedEarlyDistortionFrame() { }
        void RenderPreparedEarlyDistortionPass() { }
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
        internal static void Render(IReadOnlyList<IPreparedParticlePass> passes, List<IDisposable> batches)
        {
            if (passes == null || passes.Count == 0) return;
            batches.Clear();
            try
            {
                var stencilScene = new VfxStencilScene();
                foreach (IPreparedParticlePass pass in passes)
                    pass.PrepareStencilScene(stencilScene);
                foreach (IPreparedParticlePass pass in passes)
                    batches.Add(pass.BeginPreparedRenderBatch());
                foreach (IPreparedParticlePass pass in passes)
                    pass.CapturePreparedEarlyDistortionFrame();
                foreach (IPreparedParticlePass pass in passes)
                    pass.RenderPreparedEarlyDistortionPass();
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
        }
    }
}
