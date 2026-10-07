using System;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Rendering;

/// <summary>Builds flat decal footprints from the shared particle appearance and world basis.</summary>
internal sealed class VfxProjectionGeometry
{
    private static readonly VfxProjectionDefinition DefaultProjection = new();
    private float[] _instances = Array.Empty<float>();
    internal ReadOnlySpan<float> Prepare(VfxEmitterDefinition definition, ReadOnlySpan<float> source)
    {
        if (_instances.Length < source.Length)
            _instances = new float[Math.Max(source.Length, Math.Max(VfxPlaybackRuntime.InstanceStride * 64, _instances.Length * 2))];
        source.CopyTo(_instances);
        bool simple = definition.IsSimpleEmitter;
        for (int at = 0; at < source.Length; at += VfxPlaybackRuntime.InstanceStride)
        {
            Span<float> decal = _instances.AsSpan(at, VfxPlaybackRuntime.InstanceStride);
            decal[4] = simple ? decal[4] : decal[18];
            decal[9] = simple ? decal[9] : MatrixTurn(decal[36], decal[38]);
            if (simple) decal.Slice(5, 4).Fill(1f);
            decal[21] = HeightFade(decal[1], definition.Projection ?? DefaultProjection);
            float speed = new Vector3(decal[12], decal[13], decal[14]).Length();
            decal[19] = Lookup(definition.ColorLookUpTypeX ?? 0, decal[11], speed, decal[34],
                definition.ColorLookUpScales.X, definition.ColorLookUpOffsets.X);
            decal[20] = Lookup(definition.ColorLookUpTypeY ?? 0, decal[11], speed, decal[34],
                definition.ColorLookUpScales.Y, definition.ColorLookUpOffsets.Y);
        }
        return _instances.AsSpan(0, source.Length);
    }

    internal static float MatrixTurn(float m00, float m02)
    {
        float angle = MathF.Atan2(-m00, m02);
        if (angle < 0f) angle += MathF.Tau;
        return angle - 1.5f * MathF.PI;
    }

    internal static float HeightFade(float height, VfxProjectionDefinition projection)
    {
        float gap = MathF.Abs(height);
        return gap <= projection.YRange ? 1f : MathF.Max(0f,
            1f - (gap - projection.YRange) / MathF.Max(projection.Fading, 1e-6f));
    }

    private static float Lookup(int type, float age, float speed, float random, float scale, float offset)
        => (type switch { 1 => age, 2 => speed, 3 => random, _ => 1f }) * scale + (type == 0 ? 0f : offset);
}
