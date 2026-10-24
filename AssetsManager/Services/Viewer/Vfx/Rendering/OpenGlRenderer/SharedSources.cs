using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Services.Viewer.Vfx.Semantics;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Rendering;

public sealed partial class VfxOpenGlRenderer
{
    private IReadOnlyList<VfxPlaybackRuntime.EmitterState> _activeSources;
    private readonly List<int> _sourceCounts = new();
    private VfxPlaybackRuntime.EmitterState[] _instanceOwners = Array.Empty<VfxPlaybackRuntime.EmitterState>();
    private int _instanceOwnerCount;
    private int[] _sourceParticles = Array.Empty<int>();
    private float[] _ribbonVertices = Array.Empty<float>();

    private void CountParticleDraw(int instances) => LastParticleDrawCounts =
        (LastParticleDrawCounts.Draws + 1, LastParticleDrawCounts.Instances + instances);

    private static bool CompatibleSources(IReadOnlyList<VfxPlaybackRuntime.EmitterState> sources)
    {
        var first = sources[0];
        foreach (var next in sources)
        {
            if ((!ReferenceEquals(next.Def, first.Def) && next.Def != first.Def) ||
                next.Texture != first.Texture || next.TextureMult != first.TextureMult ||
                next.ColorGradientTexture != first.ColorGradientTexture || next.PaletteTexture != first.PaletteTexture ||
                next.ErosionTexture != first.ErosionTexture || next.DistortionTexture != first.DistortionTexture ||
                next.ReflectionTexture != first.ReflectionTexture || next.MeshVao != first.MeshVao ||
                next.MeshHasSkinning != first.MeshHasSkinning || (next.MeshAnimation == null) != (first.MeshAnimation == null) ||
                next.MeshOwnerScale != first.MeshOwnerScale ||
                next.HudLayer != first.HudLayer || next.ProgramTextures.Count != first.ProgramTextures.Count)
                return false;
            foreach (var texture in next.ProgramTextures)
                if (!first.ProgramTextures.TryGetValue(texture.Key, out uint handle) || texture.Value != handle)
                    return false;
        }
        return true;
    }

    private bool _gatheredSorted;

    private int GatherInstances(IReadOnlyList<VfxPlaybackRuntime.EmitterState> sources, Matrix4x4 view)
    {
        _sourceCounts.Clear();
        int held = 0;
        foreach (var source in sources)
        {
            int take = SourceDrawCount(source, held);
            if (source.Def.DrawsAsBeamMesh && !VfxBeamGeometry.TryMeshPlacement(source, out _, out _, out _)) take = 0;
            _sourceCounts.Add(take);
            held += take;
        }
        _gatheredSorted = false;
        // Ribbons keep their pool provenance and build each strand directly; no particle-row scratch is needed.
        if (held == 0 || IsRibbonSource(sources[0])) return held;
        _gatheredSorted = sources[0].Def.DrawsAsQuad && ShouldSortInstances(sources[0].Def, held);
        if (_gatheredSorted) EnsureInstanceSortCapacity(held, held * Stride);
        else if (_groupedInstances.Length < held * Stride)
            Array.Resize(ref _groupedInstances, Math.Max(held * Stride, Math.Max(Stride * 4, _groupedInstances.Length * 2)));
        bool mesh = sources[0].Def.DrawsAsMesh;
        if (mesh && _instanceOwners.Length < held)
        {
            int size = Math.Max(held, Math.Max(4, _instanceOwners.Length * 2));
            Array.Resize(ref _instanceOwners, size);
            Array.Resize(ref _sourceParticles, size);
        }
        int offset = 0;
        for (int from = 0; from < sources.Count; from++)
        {
            var source = sources[from];
            int take = _sourceCounts[from];
            if (take == 0) continue;
            source.PrepareInstances(take).CopyTo(_groupedInstances.AsSpan(offset * Stride));
            for (int index = 0; index < take; index++)
            {
                if (mesh)
                {
                    _instanceOwners[offset + index] = source;
                    _sourceParticles[offset + index] = index;
                }
                else FoldEmitterScroll(source, _groupedInstances.AsSpan((offset + index) * Stride, Stride));
            }
            offset += take;
        }
        if (_gatheredSorted)
            VfxRenderQueue.CopyInstancesBackToFront(_groupedInstances, held, Stride, view,
                _sortedInstances, _instanceDepths, _instanceOrder);
        if (mesh) _instanceOwnerCount = Math.Max(_instanceOwnerCount, held);
        return held;
    }

    private static bool IsRibbonSource(VfxPlaybackRuntime.EmitterState source) => !source.Def.DrawsAsMesh &&
        source.Def.PrimitiveKind is VfxPrimitiveKind.CameraTrail or VfxPrimitiveKind.ArbitraryTrail or
            VfxPrimitiveKind.Beam or VfxPrimitiveKind.CameraSegmentBeam;

    private int SourceDrawCount(VfxPlaybackRuntime.EmitterState source, int used)
    {
        int count = ResolveEmitterDrawCount(source.Def, used, source.InstanceCount);
        if (source.Def.DrawsAsQuad || source.Def.DrawsAsProjection)
        {
            int capacity = SharedDrawCapacities?.GetValueOrDefault(DrawGroup(source), QuadsPerEmitter) ?? QuadsPerEmitter;
            count = Math.Min(count, Math.Max(0, capacity - used));
        }
        return count;
    }

    private static void FoldEmitterScroll(VfxPlaybackRuntime.EmitterState source, Span<float> row)
    {
        var definition = source.Def;
        var state = definition.RenderState;
        Vector2 first = VfxUvSemantics.PreFlipEmitterScroll(definition.EmitterUvScrollRate, source.RenderTime,
            state?.FlipU ?? false, state?.FlipV ?? false);
        Vector2 second = definition.DrawsAsProjection
            ? definition.TextureMultEmitterUvScrollRate * source.RenderTime
            : VfxUvSemantics.PreFlipEmitterScroll(definition.TextureMultEmitterUvScrollRate, source.RenderTime,
                definition.TextureMultFlipU, definition.TextureMultFlipV);
        first = VfxUvSemantics.Periodic(first, state?.TextureAddressMode ?? 0);
        second = VfxUvSemantics.Periodic(second, definition.TextureMultAddressMode);
        // Preserve the original coordinate: custom samplers can clamp a raw offset outside
        // [0,1]. Only the emitter-clock term used to be folded by its uniform uploader.
        row[19] += first.X; row[20] += first.Y;
        row[29] += second.X; row[30] += second.Y;
    }

    private int BuildSharedRibbon(VfxPlaybackRuntime.EmitterState emitter, Vector3 eye, Vector3 forward, int count, bool beam)
    {
        int written = 0;
        int sourceCount = _activeSources?.Count ?? 1;
        for (int index = 0; index < sourceCount; index++)
        {
            var source = _activeSources?[index] ?? emitter;
            int take = _activeSources != null ? _sourceCounts[index] : count;
            if (take <= 0) continue;
            int vertices = beam ? _beamGeometry.Build(source, eye, take) : _trailGeometry.Build(source, forward, take);
            int required = (written + vertices) * VfxTrailGeometry.VertexStride;
            if (_ribbonVertices.Length < required)
                Array.Resize(ref _ribbonVertices, Math.Max(required, Math.Max(1024, _ribbonVertices.Length * 2)));
            (beam ? _beamGeometry.Vertices : _trailGeometry.Vertices).AsSpan(0, vertices * VfxTrailGeometry.VertexStride)
                .CopyTo(_ribbonVertices.AsSpan(written * VfxTrailGeometry.VertexStride));
            written += vertices;
        }
        return written;
    }
}
