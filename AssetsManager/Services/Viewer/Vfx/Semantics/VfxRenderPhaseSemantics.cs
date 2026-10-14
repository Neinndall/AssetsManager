using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Semantics;

internal readonly record struct VfxRenderPhases(bool Under, bool Over, bool EarlyDistortion, bool LateDistortion)
{
    internal bool Draws => Under || Over || EarlyDistortion || LateDistortion;
    internal bool Distorts => EarlyDistortion || LateDistortion;
}

internal static class VfxRenderPhaseSemantics
{
    internal static VfxRenderPhases Resolve(VfxEmitterDefinition emitter, bool hudLayer = false)
    {
        if (emitter is null) return default;
        if (emitter.IsSimpleEmitter && emitter.PrimitiveKind is not
            (VfxPrimitiveKind.CameraQuad or VfxPrimitiveKind.ArbitraryQuad or VfxPrimitiveKind.Mesh or
             VfxPrimitiveKind.PlanarProjection or VfxPrimitiveKind.AttachedMesh)) return default;
        hudLayer |= emitter.IsHudLayer;
        bool warps = emitter.Distortion is not null && !emitter.HasResolvedCustomMaterial && !emitter.DrawsAsProjection;
        int phase = emitter.RenderState?.RenderPhase ?? VfxAuthoredDefaults.RenderPhaseOverride;
        switch (phase)
        {
            case 0: case 5: return new(true, false, false, false);
            case 4: return new(false, true, false, false);
            case 6: return hudLayer ? new(false, true, false, false) : default;
            case 2: return warps ? new(false, false, true, false) : default;
            case 3: return warps ? new(false, false, false, true) : default;
            case 7: break;
            default: return default;
        }
        if (hudLayer) return new(false, true, false, false);
        if (emitter.IsGroundLayer) return new(true, false, false, false);
        int mode = emitter.Distortion?.Mode ?? 0;
        if (warps && mode != 0) return new(false, false, (mode & 2) != 0, (mode & 1) != 0);
        return (emitter.RenderState?.RenderPass ?? 0) < 0
            ? new(true, false, false, false) : new(false, true, false, false);
    }
}
