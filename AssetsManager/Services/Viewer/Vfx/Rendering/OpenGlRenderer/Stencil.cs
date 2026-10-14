using AssetsManager.Services.Viewer.Vfx.Semantics;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Vfx.Rendering;

public sealed partial class VfxOpenGlRenderer
{
    private void ApplyEmitterStencilState(VfxEmitterDefinition emitter, VfxStencilScene scene, bool wireframe)
    {
        if (wireframe || !scene.TryGetState(emitter, out var descriptor, out byte reference))
        {
            _gl.Disable(EnableCap.StencilTest);
            return;
        }

        _gl.Enable(EnableCap.StencilTest);
        _gl.StencilFunc(descriptor.Operation switch
        {
            VfxStencilOperationKind.TestEqual => StencilFunction.Equal,
            VfxStencilOperationKind.TestNotEqual => StencilFunction.Notequal,
            _ => StencilFunction.Always
        }, reference, VfxStencilSemantics.Mask);
        _gl.StencilMask(VfxStencilSemantics.Mask);
        _gl.StencilOp(StencilOp.Keep, StencilOp.Keep,
            descriptor.WritesStencil ? StencilOp.Replace : StencilOp.Keep);
    }
}
