using AssetsManager.Utils.Rendering;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Semantics
{
    /// <summary>
    /// League emitter draw ordering delegator.
    /// RenderPhaseOverride and Importance are metadata, not comparator keys.
    /// </summary>
    internal static class VfxDrawOrderSemantics
    {
        internal static int Compare(
            VfxEmitterDefinition left,
            int leftSourceOrder,
            VfxEmitterDefinition right,
            int rightSourceOrder)
            => VfxDrawOrderUtils.CompareEmitters(left, leftSourceOrder, right, rightSourceOrder);

        internal static int GetBlendRank(int blendMode)
            => VfxDrawOrderUtils.ResolveBlendRank(blendMode);
    }
}
