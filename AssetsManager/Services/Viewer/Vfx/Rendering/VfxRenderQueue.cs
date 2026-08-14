using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Rendering
{
    /// <summary>One drawable emitter plus its stable position in the composed effect graph.</summary>
    public sealed record VfxRenderQueueEntry(
        VfxPlaybackRuntime.EmitterState Emitter,
        int RuntimeOrder,
        int QueueOrder,
        float ViewDepth);

    /// <summary>
    /// Builds one backend-neutral render queue across roots, children, and ability events.
    /// Authored phase/pass ordering is preserved before optional back-to-front emitter sorting.
    /// </summary>
    public static class VfxRenderQueue
    {
        public static IReadOnlyList<VfxRenderQueueEntry> Build(
            IEnumerable<IReadOnlyList<VfxPlaybackRuntime.EmitterState>> runtimes,
            Matrix4x4 view)
        {
            ArgumentNullException.ThrowIfNull(runtimes);
            var entries = new List<VfxRenderQueueEntry>();
            int runtimeOrder = 0;
            int queueOrder = 0;
            foreach (IReadOnlyList<VfxPlaybackRuntime.EmitterState> emitters in runtimes)
            {
                foreach (VfxPlaybackRuntime.EmitterState emitter in emitters)
                {
                    Vector3 viewPosition = Vector3.Transform(emitter.BasePos, view);
                    entries.Add(new VfxRenderQueueEntry(
                        emitter,
                        runtimeOrder,
                        queueOrder++,
                        viewPosition.Z));
                }
                runtimeOrder++;
            }

            entries.Sort(Compare);
            return entries;
        }

        private static int Compare(VfxRenderQueueEntry left, VfxRenderQueueEntry right)
        {
            VfxEmitterRenderState leftState = left.Emitter.Def.RenderState ?? VfxEmitterRenderState.Default;
            VfxEmitterRenderState rightState = right.Emitter.Def.RenderState ?? VfxEmitterRenderState.Default;

            int order = leftState.RenderPhase.CompareTo(rightState.RenderPhase);
            if (order != 0) return order;
            order = leftState.RenderPass.CompareTo(rightState.RenderPass);
            if (order != 0) return order;

            if (leftState.SortEmittersByPosition && rightState.SortEmittersByPosition)
            {
                // OpenGL camera space looks down -Z: the more negative value is farther away.
                order = left.ViewDepth.CompareTo(right.ViewDepth);
                if (order != 0) return order;
            }

            order = left.Emitter.Def.Importance.CompareTo(right.Emitter.Def.Importance);
            return order != 0 ? order : left.QueueOrder.CompareTo(right.QueueOrder);
        }

        /// <summary>
        /// Copies packed instances in camera back-to-front order without mutating simulation state.
        /// Transparent blending requires particle order even when authored emitter order is fixed.
        /// </summary>
        internal static void CopyInstancesBackToFront(
            float[] source,
            int instanceCount,
            int stride,
            Matrix4x4 view,
            float[] destination,
            float[] depthScratch,
            int[] orderScratch)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(destination);
            ArgumentNullException.ThrowIfNull(depthScratch);
            ArgumentNullException.ThrowIfNull(orderScratch);
            if (instanceCount < 0 || stride < 3 || source.Length < instanceCount * stride)
                throw new ArgumentOutOfRangeException(nameof(instanceCount));
            if (destination.Length < instanceCount * stride ||
                depthScratch.Length < instanceCount ||
                orderScratch.Length < instanceCount)
                throw new ArgumentException("Instance sorting buffers are too small.");

            for (int index = 0; index < instanceCount; index++)
            {
                int offset = index * stride;
                float viewDepth = Vector3.Transform(
                    new Vector3(source[offset], source[offset + 1], source[offset + 2]),
                    view).Z;
                depthScratch[index] = float.IsFinite(viewDepth) ? viewDepth : 0f;
                orderScratch[index] = index;
            }

            // OpenGL view space looks down -Z, so ascending Z is back-to-front.
            Array.Sort(depthScratch, orderScratch, 0, instanceCount);
            for (int destinationIndex = 0; destinationIndex < instanceCount; destinationIndex++)
            {
                Array.Copy(
                    source,
                    orderScratch[destinationIndex] * stride,
                    destination,
                    destinationIndex * stride,
                    stride);
            }
        }
    }
}
