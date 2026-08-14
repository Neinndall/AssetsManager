using System;
using System.Collections.Generic;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Semantics
{
    public enum VfxStencilOperationKind
    {
        Disabled,
        WriteReference,
        TestEqual,
        TestNotEqual
    }

    public readonly record struct VfxStencilDescriptor(
        VfxStencilOperationKind Operation,
        bool WritesStencil,
        bool WritesColor);

    /// <summary>Translates authored particle stencil modes into backend-neutral operations.</summary>
    public static class VfxStencilSemantics
    {
        private static readonly VfxStencilDescriptor Disabled = new(
            VfxStencilOperationKind.Disabled,
            WritesStencil: false,
            WritesColor: true);

        public static bool TryGetDescriptor(byte authoredMode, out VfxStencilDescriptor descriptor)
        {
            descriptor = authoredMode switch
            {
                0 => Disabled,
                1 or 4 => new VfxStencilDescriptor(
                    VfxStencilOperationKind.WriteReference,
                    WritesStencil: true,
                    WritesColor: false),
                2 => new VfxStencilDescriptor(
                    VfxStencilOperationKind.TestEqual,
                    WritesStencil: false,
                    WritesColor: true),
                3 => new VfxStencilDescriptor(
                    VfxStencilOperationKind.TestNotEqual,
                    WritesStencil: false,
                    WritesColor: true),
                _ => default
            };
            return authoredMode <= 4;
        }

        public static byte ResolveReference(
            VfxEmitterRenderState renderState,
            IReadOnlyDictionary<uint, byte> referenceIds)
        {
            ArgumentNullException.ThrowIfNull(renderState);
            ArgumentNullException.ThrowIfNull(referenceIds);
            if (renderState.StencilReference != 0)
                return renderState.StencilReference;
            return renderState.StencilReferenceId != 0 &&
                referenceIds.TryGetValue(renderState.StencilReferenceId, out byte reference)
                    ? reference
                    : (byte)0;
        }
    }
}
