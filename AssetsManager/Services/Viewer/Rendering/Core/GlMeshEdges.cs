using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Rendering.Core;

/// <summary>Unique triangle edges per independently visible range, sharing the surface's vertex streams.</summary>
internal sealed class GlMeshEdges : IDisposable
{
    internal readonly record struct Range(int Start, int Count, float Spacing);
    private readonly GL _gl;
    private readonly uint[] _triangles;
    private readonly Vector3[] _positions;
    private readonly Dictionary<(int, int), (uint Buffer, Range Range)> _ranges = new();

    internal GlMeshEdges(GL gl, uint[] triangles, Vector3[] positions)
    { _gl = gl; _triangles = triangles; _positions = positions; }

    internal static (uint[] Indices, float Spacing) Build(uint[] triangles, Vector3[] positions, int start, int count)
    {
        if (start < 0 || count < 0 || start > triangles.Length - count) throw new ArgumentOutOfRangeException(nameof(start));
        var seen = new HashSet<(uint, uint)>();
        var indices = new List<uint>();
        double total = 0;
        int measured = 0;
        void Add(uint a, uint b)
        {
            if (a == b) return;
            if (a > b) (a, b) = (b, a);
            if (!seen.Add((a, b))) return;
            indices.Add(a); indices.Add(b);
            if (a < positions.Length && b < positions.Length)
            { total += Vector3.Distance(positions[a], positions[b]); measured++; }
        }
        for (int i = start; i + 2 < start + count; i += 3)
        { Add(triangles[i], triangles[i + 1]); Add(triangles[i + 1], triangles[i + 2]); Add(triangles[i + 2], triangles[i]); }
        return (indices.ToArray(), measured == 0 ? 0 : (float)(total / measured));
    }

    internal unsafe void Draw(uint program, uint surfaceBuffer, int start, int count)
    {
        var key = (start, count);
        if (!_ranges.TryGetValue(key, out var held))
        {
            var built = Build(_triangles, _positions, start, count);
            uint buffer = _gl.GenBuffer();
            _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, buffer);
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, new ReadOnlySpan<uint>(built.Indices), BufferUsageARB.StaticDraw);
            held = (buffer, new Range(0, built.Indices.Length, built.Spacing));
            _ranges.Add(key, held);
        }
        _gl.Uniform1(_gl.GetUniformLocation(program, "uEdgeSpacing"), held.Range.Spacing);
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, held.Buffer);
        _gl.DrawElements(PrimitiveType.Lines, (uint)held.Range.Count, DrawElementsType.UnsignedInt, (void*)0);
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, surfaceBuffer);
    }

    internal static void Frame(GL gl, uint program, Matrix4x4 view, Matrix4x4 projection, bool overlay)
    {
        gl.UniformMatrix4(gl.GetUniformLocation(program, "uEdgeView"), 1, false, in view.M11);
        gl.UniformMatrix4(gl.GetUniformLocation(program, "uEdgeProjection"), 1, false, in projection.M11);
        gl.Uniform1(gl.GetUniformLocation(program, "uEdgeOverlay"), overlay ? 1 : 0);
    }

    internal const string Vertex = @"
uniform mat4 uEdgeView;
uniform mat4 uEdgeProjection;
uniform float uEdgeSpacing;
uniform int uEdgeOverlay;
uniform int uWireframePass;
out float vEdgeFade;
void edgePosition(vec4 worldPosition, inout vec4 clipPosition) {
    vEdgeFade = 1.0;
    if (uWireframePass == 0 || uEdgeOverlay == 0) return;
    vec4 viewPosition = uEdgeView * worldPosition;
    vec4 pulled = uEdgeProjection * vec4(viewPosition.xyz * 0.999, 1.0);
    clipPosition.z = pulled.z / pulled.w * clipPosition.w;
    float distance = abs(uEdgeProjection[3][3]) > 0.5 ? 1.0 : max(-viewPosition.z, 0.001);
    float pixels = uEdgeSpacing * uEdgeProjection[1][1] * 540.0 / distance;
    vEdgeFade = mix(0.2, 1.0, smoothstep(2.0, 10.0, pixels));
}
";

    public void Dispose()
    {
        foreach (var held in _ranges.Values) _gl.DeleteBuffer(held.Buffer);
        _ranges.Clear();
    }
}
