using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Utils.Rendering;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Rendering;

internal sealed record SknVertexInspectionBatch(SceneModel Model, SknVertexInspectionPart Part, Vector4 Color);

/// <summary>Draws SKN inspection points and authored normal directions in the owning viewport's GL context.</summary>
internal sealed class SknVertexInspectionRenderer : IDisposable
{
    private readonly GL _gl;
    private readonly uint _program, _vao, _vbo;
    private readonly List<(SknVertexInspectionBatch Batch, int Start, int Count)> _draws = new();
    private IReadOnlyList<SknVertexInspectionBatch> _data;

    internal SknVertexInspectionRenderer(GL gl)
    {
        _gl = gl;
        _program = GlShaderCompiler.CreateProgram(gl, false, """
            layout(location=0) in vec3 position;
            layout(location=1) in vec3 direction;
            uniform mat4 transform;
            uniform float normalLength;
            uniform float pointSize;
            void main() {
                gl_Position = transform * vec4(position + direction * normalLength, 1.0);
                gl_Position.z -= 0.00001 * gl_Position.w;
                gl_PointSize = pointSize;
            }
            """, """
            uniform vec4 color;
            uniform int points;
            out vec4 outputColor;
            void main() {
                if (points != 0 && length(gl_PointCoord - vec2(0.5)) > 0.5) discard;
                outputColor = color;
            }
            """);
        _vao = gl.GenVertexArray();
        _vbo = gl.GenBuffer();
    }

    internal void Render(IReadOnlyList<SknVertexInspectionBatch> data, Matrix4x4 viewProjection,
        Func<string, bool> visible, bool points, bool normals, bool throughMesh, float pointSize, float normalLength)
    {
        if (data.Count == 0 || !points && !normals)
            return;

        int previousProgram = _gl.GetInteger(GetPName.CurrentProgram);
        int previousVao = _gl.GetInteger(GetPName.VertexArrayBinding);
        int previousBuffer = _gl.GetInteger(GetPName.ArrayBufferBinding);
        int previousDepthFunc = _gl.GetInteger(GetPName.DepthFunc);
        bool depth = _gl.IsEnabled(EnableCap.DepthTest), blend = _gl.IsEnabled(EnableCap.Blend);
        bool programSize = _gl.IsEnabled(EnableCap.ProgramPointSize);
        _gl.GetBoolean(GetPName.DepthWritemask, out bool depthWrite);
        try
        {
            _gl.BindVertexArray(_vao);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
            if (!ReferenceEquals(_data, data))
            {
                var vertices = new List<Vector3>();
                _draws.Clear();
                foreach (var batch in data)
                {
                    int start = vertices.Count / 2;
                    foreach (Vector3 point in batch.Part.Positions)
                    {
                        vertices.Add(point);
                        vertices.Add(Vector3.Zero);
                    }
                    for (int index = 0; index < batch.Part.Positions.Length; index++)
                    {
                        Vector3 point = batch.Part.Positions[index];
                        Vector3 normal = batch.Part.Normals[index];
                        vertices.Add(point); vertices.Add(Vector3.Zero);
                        vertices.Add(point); vertices.Add(normal);
                    }
                    _draws.Add((batch, start, batch.Part.Positions.Length));
                }
                _gl.BufferData(BufferTargetARB.ArrayBuffer, new ReadOnlySpan<Vector3>(vertices.ToArray()), BufferUsageARB.StaticDraw);
                _gl.EnableVertexAttribArray(0);
                _gl.EnableVertexAttribArray(1);
                _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 24, IntPtr.Zero);
                _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 24, new IntPtr(12));
                _data = data;
            }
            _gl.UseProgram(_program);
            _gl.Disable(EnableCap.Blend);
            if (throughMesh) _gl.Disable(EnableCap.DepthTest); else _gl.Enable(EnableCap.DepthTest);
            _gl.DepthFunc(DepthFunction.Lequal);
            _gl.DepthMask(false);
            _gl.Enable(EnableCap.ProgramPointSize);
            _gl.Uniform1(_gl.GetUniformLocation(_program, "pointSize"), Math.Clamp(pointSize, 1f, 16f));
            _gl.Uniform1(_gl.GetUniformLocation(_program, "normalLength"), Math.Clamp(normalLength, 0.01f, 100f));
            foreach (var draw in _draws)
            {
                if (!visible(draw.Batch.Part.Name))
                    continue;
                Matrix4x4 transform = GlMeshRenderer.CreateWorldMatrix(draw.Batch.Model) * viewProjection;
                _gl.UniformMatrix4(_gl.GetUniformLocation(_program, "transform"), 1, false, in transform.M11);
                Vector4 color = draw.Batch.Color;
                _gl.Uniform4(_gl.GetUniformLocation(_program, "color"), color.X, color.Y, color.Z, color.W);
                if (points)
                {
                    _gl.Uniform1(_gl.GetUniformLocation(_program, "points"), 1);
                    _gl.DrawArrays(PrimitiveType.Points, draw.Start, (uint)draw.Count);
                }
                if (normals)
                {
                    _gl.Uniform1(_gl.GetUniformLocation(_program, "points"), 0);
                    _gl.DrawArrays(PrimitiveType.Lines, draw.Start + draw.Count, (uint)(draw.Count * 2));
                }
            }
        }
        finally
        {
            _gl.DepthMask(depthWrite);
            _gl.DepthFunc((DepthFunction)previousDepthFunc);
            if (depth) _gl.Enable(EnableCap.DepthTest); else _gl.Disable(EnableCap.DepthTest);
            if (blend) _gl.Enable(EnableCap.Blend); else _gl.Disable(EnableCap.Blend);
            if (programSize) _gl.Enable(EnableCap.ProgramPointSize); else _gl.Disable(EnableCap.ProgramPointSize);
            _gl.BindVertexArray((uint)previousVao);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, (uint)previousBuffer);
            _gl.UseProgram((uint)previousProgram);
        }
    }

    public void Dispose()
    {
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteProgram(_program);
        _draws.Clear();
        _data = null;
    }
}
