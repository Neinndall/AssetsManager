using System;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Utils.Rendering
{
    /// <summary>
    /// Utility functions for sorting and establishing draw ordering among visual effect emitters.
    /// </summary>
    public static class VfxDrawOrderUtils
    {
        private static readonly int[] BlendModeRanks = { 1, 2, 1, 0, 2, 2, 2, 2, 3 };

        /// <summary>
        /// Retrieves the sorting rank associated with a given blend mode.
        /// </summary>
        public static int ResolveBlendRank(int blendMode)
        {
            return blendMode >= 0 && blendMode < BlendModeRanks.Length
                ? BlendModeRanks[blendMode]
                : BlendModeRanks[0];
        }

        /// <summary>
        /// Determines the drawing order of two emitters based on ground layering, pass priority,
        /// blend family, render flags, and authoring sequence.
        /// </summary>
        public static int CompareEmitters(
            VfxEmitterDefinition left,
            int leftSourceOrder,
            VfxEmitterDefinition right,
            int rightSourceOrder)
        {
            bool leftGround = left?.IsGroundLayer == true;
            bool rightGround = right?.IsGroundLayer == true;
            if (leftGround != rightGround)
                return leftGround ? -1 : 1;

            VfxEmitterRenderState leftState = left?.RenderState ?? VfxEmitterRenderState.Default;
            VfxEmitterRenderState rightState = right?.RenderState ?? VfxEmitterRenderState.Default;

            int order = leftState.RenderPass.CompareTo(rightState.RenderPass);
            if (order != 0) return order;

            order = ResolveBlendRank(left?.BlendMode ?? 0).CompareTo(ResolveBlendRank(right?.BlendMode ?? 0));
            if (order != 0) return order;

            order = (left?.MiscRenderFlags ?? 0).CompareTo(right?.MiscRenderFlags ?? 0);
            if (order != 0) return order;

            return leftSourceOrder.CompareTo(rightSourceOrder);
        }
    }
}
