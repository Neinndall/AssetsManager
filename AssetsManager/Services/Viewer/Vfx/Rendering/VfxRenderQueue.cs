using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Services.Viewer.Vfx.Semantics;
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
            // 1. Ground layer emitters render before default emitters (Riot / LTK drawKind.ts)
            bool leftGround = left.Emitter.Def.IsGroundLayer;
            bool rightGround = right.Emitter.Def.IsGroundLayer;
            if (leftGround != rightGround)
                return leftGround ? -1 : 1;

            // LTK ranks emitters inside each VFX system, then appends child systems after
            // the system that owns them. Keep that hierarchy before comparing authored keys.
            int order = left.RuntimeOrder.CompareTo(right.RuntimeOrder);
            if (order != 0) return order;

            order = VfxDrawOrderSemantics.Compare(
                left.Emitter.Def,
                left.Emitter.SourceOrder,
                right.Emitter.Def,
                right.Emitter.SourceOrder);
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
