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
        int GraphOrder,
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
            var graphOrders = new Dictionary<object, int>();
            int nextGraphOrder = 0;
            int queueOrder = 0;
            foreach (IReadOnlyList<VfxPlaybackRuntime.EmitterState> emitters in runtimes)
            {
                object runtimeKey = emitters.Count > 0
                    ? emitters[0].RenderGraphKey ?? emitters
                    : emitters;
                if (!graphOrders.TryGetValue(runtimeKey, out int graphOrder))
                {
                    graphOrder = nextGraphOrder++;
                    graphOrders[runtimeKey] = graphOrder;
                }

                foreach (VfxPlaybackRuntime.EmitterState emitter in emitters)
                {
                    Vector3 viewPosition = Vector3.Transform(emitter.BasePos, view);
                    entries.Add(new VfxRenderQueueEntry(
                        emitter,
                        graphOrder,
                        queueOrder++,
                        viewPosition.Z));
                }
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

            // Separate top-level graph instances keep their authored insertion order. Inside a
            // graph, LTK ranks definitions from the static definition tree, not from child birth time.
            int order = left.GraphOrder.CompareTo(right.GraphOrder);
            if (order != 0) return order;

            order = left.Emitter.RenderRank.CompareTo(right.Emitter.RenderRank);
            if (order != 0) return order;

            order = VfxDrawOrderSemantics.Compare(
                left.Emitter.Def,
                left.Emitter.SourceOrder,
                right.Emitter.Def,
                right.Emitter.SourceOrder);
            return order != 0 ? order : left.QueueOrder.CompareTo(right.QueueOrder);
        }

        /// <summary>
        /// Gathers one quad definition's live sources into its shared LTK budget, then sorts the
        /// combined particle set once. This mirrors Quads.tsx rather than sorting each child pool separately.
        /// </summary>
        internal static int CopyQuadSourcesBackToFront(
            IReadOnlyList<VfxPlaybackRuntime.EmitterState> sources,
            int capacity,
            int stride,
            Matrix4x4 view,
            float[] groupedScratch,
            float[] destination,
            float[] depthScratch,
            int[] orderScratch)
        {
            ArgumentNullException.ThrowIfNull(sources);
            ArgumentNullException.ThrowIfNull(groupedScratch);
            if (capacity <= 0 || stride < 3) return 0;
            if (groupedScratch.Length < capacity * stride)
                throw new ArgumentException("Grouped instance buffer is too small.", nameof(groupedScratch));

            int held = 0;
            foreach (VfxPlaybackRuntime.EmitterState source in sources)
            {
                if (source is null || source.InstanceCount <= 0) continue;
                int take = Math.Min(source.InstanceCount, capacity - held);
                if (take <= 0) break;
                if (source.Instances.Length < take * stride)
                    throw new ArgumentException("A source instance buffer is shorter than its declared count.", nameof(sources));
                Array.Copy(source.Instances, 0, groupedScratch, held * stride, take * stride);
                held += take;
                if (held >= capacity) break;
            }

            if (held == 0) return 0;
            CopyInstancesBackToFront(
                groupedScratch,
                held,
                stride,
                view,
                destination,
                depthScratch,
                orderScratch);
            return held;
        }

        /// <summary>
        /// Copies packed quad instances in LTK camera back-to-front order without mutating simulation state.
        /// Quads rank by squared distance from the eye, not by view-space Z, across all live sources.
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

            Vector3 eye = Matrix4x4.Invert(view, out Matrix4x4 inverseView)
                ? inverseView.Translation
                : Vector3.Zero;
            for (int index = 0; index < instanceCount; index++)
            {
                int offset = index * stride;
                Vector3 position = new(source[offset], source[offset + 1], source[offset + 2]);
                float distanceSquared = Vector3.DistanceSquared(position, eye);
                depthScratch[index] = float.IsFinite(distanceSquared) ? distanceSquared : 0f;
                orderScratch[index] = index;
            }

            // LTK Quads.tsx sorts farthest first by squared eye distance.
            Array.Sort(depthScratch, orderScratch, 0, instanceCount);
            for (int destinationIndex = 0; destinationIndex < instanceCount; destinationIndex++)
            {
                int sourceIndex = orderScratch[instanceCount - 1 - destinationIndex];
                Array.Copy(
                    source,
                    sourceIndex * stride,
                    destination,
                    destinationIndex * stride,
                    stride);
            }
        }
    }
}
