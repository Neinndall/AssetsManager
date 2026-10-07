using System.Collections.Generic;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Semantics;

/// <summary>Resolves particle masks across every owner and drawing phase of one scene frame.</summary>
internal sealed class VfxStencilScene
{
    private readonly Dictionary<uint, byte> _names = new();
    private readonly HashSet<byte> _written = new();
    private readonly Dictionary<VfxEmitterDefinition, (byte Reference, bool Live)> _claims =
        new(System.Collections.Generic.ReferenceEqualityComparer.Instance);

    internal void Add(VfxEmitterDefinition emitter, bool live)
    {
        var state = emitter.RenderState ?? VfxEmitterRenderState.Default;
        if (emitter.IsSimpleEmitter || emitter.HasResolvedCustomMaterial || state.StencilMode == 0 ||
            !VfxStencilSemantics.TryGetDescriptor(state.StencilMode, out var descriptor)) return;
        if (!emitter.IsMeshPrimitive && !emitter.DrawsAsQuad && !emitter.DrawsAsTrail &&
            !emitter.DrawsAsBeam && !emitter.DrawsAsProjection) return;

        if (state.StencilReferenceId != 0 && !_names.ContainsKey(state.StencilReferenceId))
            _names.Add(state.StencilReferenceId, (byte)((63 - _names.Count) & VfxStencilSemantics.Mask));
        byte reference = VfxStencilSemantics.ResolveReference(state, _names);
        if (_claims.TryGetValue(emitter, out var previous)) live |= previous.Live;
        _claims[emitter] = (reference, live);
        // A mounted, visible writer counts even before its first particle exists.
        if (live && descriptor.WritesStencil) _written.Add(reference);
    }

    internal bool TryGetState(VfxEmitterDefinition emitter, out VfxStencilDescriptor descriptor, out byte reference)
    {
        descriptor = default;
        reference = 0;
        if (!_claims.TryGetValue(emitter, out var claim) || !claim.Live) return false;
        reference = claim.Reference;
        VfxStencilSemantics.TryGetDescriptor(emitter.RenderState.StencilMode, out descriptor);
        // Gameplay or material writers can be outside a preview. Only apply tests that
        // some texel can pass given the cleared buffer and the local visible writers.
        return descriptor.WritesStencil || (descriptor.Operation == VfxStencilOperationKind.TestEqual
            ? reference == 0 || _written.Contains(reference)
            : reference != 0 || _written.Count > 0);
    }
}
