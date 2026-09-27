using System;
using System.Collections.Generic;

namespace AssetsManager.Services.Viewer.Rendering
{
    /// <summary>
    /// A particle renderer whose frame was already prepared (soft-depth grab done). The Studio drives
    /// every participant through the same phases so all colour draws land before any renderer captures
    /// the frame used by distortion, keeping one global particle pass across independent owners.
    /// </summary>
    internal interface IPreparedParticlePass
    {
        IDisposable BeginPreparedRenderBatch();
        void RenderPreparedColorPass();
        void CapturePreparedDistortionFrame();
        void RenderPreparedDistortionPass();
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
                foreach (IPreparedParticlePass pass in passes)
                    batches.Add(pass.BeginPreparedRenderBatch());
                foreach (IPreparedParticlePass pass in passes)
                    pass.RenderPreparedColorPass();
                foreach (IPreparedParticlePass pass in passes)
                    pass.CapturePreparedDistortionFrame();
                foreach (IPreparedParticlePass pass in passes)
                    pass.RenderPreparedDistortionPass();
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
