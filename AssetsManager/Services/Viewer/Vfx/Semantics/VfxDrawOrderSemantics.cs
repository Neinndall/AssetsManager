using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Semantics
{
    /// <summary>
    /// League/LTK emitter draw ordering from drawKind.ts.
    /// RenderPhaseOverride and Importance are metadata, not comparator keys.
    /// </summary>
    internal static class VfxDrawOrderSemantics
    {
        private static readonly int[] BlendRank = { 1, 2, 1, 0, 2, 2, 2, 2, 3 };

        internal static int Compare(
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

            order = GetBlendRank(left?.BlendMode ?? 0).CompareTo(GetBlendRank(right?.BlendMode ?? 0));
            if (order != 0) return order;

            order = (left?.MiscRenderFlags ?? 0).CompareTo(right?.MiscRenderFlags ?? 0);
            if (order != 0) return order;

            return leftSourceOrder.CompareTo(rightSourceOrder);
        }

        internal static int GetBlendRank(int blendMode)
            => blendMode >= 0 && blendMode < BlendRank.Length ? BlendRank[blendMode] : BlendRank[0];
    }
}
