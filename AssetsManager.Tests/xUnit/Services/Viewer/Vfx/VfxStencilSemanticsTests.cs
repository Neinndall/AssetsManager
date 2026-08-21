using System.Collections.Generic;
using AssetsManager.Services.Viewer.Vfx.Semantics;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx
{
    public sealed class VfxStencilSemanticsTests
    {
        [Theory]
        [InlineData(0, VfxStencilOperationKind.Disabled, false, true)]
        [InlineData(1, VfxStencilOperationKind.WriteReference, true, false)]
        [InlineData(2, VfxStencilOperationKind.TestEqual, false, true)]
        [InlineData(3, VfxStencilOperationKind.TestNotEqual, false, true)]
        [InlineData(4, VfxStencilOperationKind.WriteReference, true, false)]
        public void MapsVerifiedAuthoredModes(
            byte mode,
            VfxStencilOperationKind operation,
            bool writesStencil,
            bool writesColor)
        {
            Assert.True(VfxStencilSemantics.TryGetDescriptor(mode, out VfxStencilDescriptor descriptor));
            Assert.Equal(operation, descriptor.Operation);
            Assert.Equal(writesStencil, descriptor.WritesStencil);
            Assert.Equal(writesColor, descriptor.WritesColor);
        }

        [Fact]
        public void RejectsUnknownAuthoredMode()
        {
            Assert.False(VfxStencilSemantics.TryGetDescriptor(5, out _));
        }

        [Fact]
        public void ExplicitReferenceTakesPriorityOverReferenceId()
        {
            var state = State(stencilReference: 12, stencilReferenceId: 0x12345678);
            var ids = new Dictionary<uint, byte> { [0x12345678] = 9 };

            Assert.Equal(12, VfxStencilSemantics.ResolveReference(state, ids));
        }

        [Fact]
        public void ReferenceIdUsesAllocatedReference()
        {
            var state = State(stencilReference: 0, stencilReferenceId: 0x12345678);
            var ids = new Dictionary<uint, byte> { [0x12345678] = 9 };

            Assert.Equal(9, VfxStencilSemantics.ResolveReference(state, ids));
        }

        private static VfxEmitterRenderState State(byte stencilReference, uint stencilReferenceId)
            => new(
                RenderPass: 0,
                AlphaReference: 0,
                TextureAddressMode: 0,
                ClampUvScroll: false,
                FlipU: false,
                FlipV: false,
                DisableBackfaceCull: false,
                StencilMode: 2,
                StencilReference: stencilReference,
                StencilReferenceId: stencilReferenceId);
    }
}
