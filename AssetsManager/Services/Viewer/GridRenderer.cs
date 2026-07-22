using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;
using AssetsManager.Utils.Rendering;

namespace AssetsManager.Services.Viewer
{
    /// <summary>Editor ground grid and directional axes.</summary>
    public sealed class GridRenderer : IDisposable
    {
        private GL _gl;
        private uint _vao;
        private uint _vbo;
        private uint _program;
        private int _uMvp;
        private int _uColor;
        private int _gridVertexCount;
        private int _axisOffset;
        private bool _ready;

        private const string VertexBody = @"
layout(location = 0) in vec3 aPos;
uniform mat4 uMvp;
void main() { gl_Position = uMvp * vec4(aPos, 1.0); }";

        private const string FragmentBody = @"
out vec4 FragColor;
uniform vec4 uColor;
void main() { FragColor = uColor; }";

        public void Initialize(GL gl, bool gles, float yHeight = 0f)
        {
            _gl = gl;
            _program = GlShaderCompiler.CreateProgram(gl, gles, VertexBody, FragmentBody);
            _uMvp = _gl.GetUniformLocation(_program, "uMvp");
            _uColor = _gl.GetUniformLocation(_program, "uColor");

            float[] verts = BuildGeometry(20, 100f, yHeight, out _gridVertexCount, out _axisOffset);
            UploadVerts(verts);
            _ready = true;
        }

        private void UploadVerts(float[] verts)
        {
            _vao = _gl.GenVertexArray();
            _gl.BindVertexArray(_vao);
            _vbo = _gl.GenBuffer();
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
            _gl.BufferData(BufferTargetARB.ArrayBuffer, new ReadOnlySpan<float>(verts), BufferUsageARB.StaticDraw);
            _gl.EnableVertexAttribArray(0);
            _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), IntPtr.Zero);
            _gl.BindVertexArray(0);
        }

        public void Render(Matrix4x4 viewProjection)
        {
            if (!_ready) return;

            // System.Numerics is row-major; uploading directly with transpose=false makes GL
            // read it column-major (= the transpose), which is what column-vector GLSL needs.
            var m = viewProjection;
            _gl.UseProgram(_program);
            _gl.UniformMatrix4(_uMvp, 1, false, in m.M11);
            _gl.BindVertexArray(_vao);

            _gl.Uniform4(_uColor, 0.16f, 0.19f, 0.27f, 1f);
            _gl.DrawArrays((GLEnum)PrimitiveType.Lines, 0, (uint)_gridVertexCount);

            _gl.Uniform4(_uColor, 0.21f, 0.89f, 0.76f, 1f);
            _gl.DrawArrays((GLEnum)PrimitiveType.Lines, _axisOffset, 2);
            _gl.Uniform4(_uColor, 0.42f, 0.36f, 0.90f, 1f);
            _gl.DrawArrays((GLEnum)PrimitiveType.Lines, _axisOffset + 2, 2);

            _gl.BindVertexArray(0);
            _gl.UseProgram(0);
        }

        private static float[] BuildGeometry(int halfCells, float cell, float yHeight, out int gridVerts, out int axisOffset)
        {
            var list = new List<float>();
            float ext = halfCells * cell;
            for (int i = -halfCells; i <= halfCells; i++)
            {
                float p = i * cell;
                list.AddRange(new[] { p, yHeight, -ext, p, yHeight, ext });
                list.AddRange(new[] { -ext, yHeight, p, ext, yHeight, p });
            }
            gridVerts = list.Count / 3;
            axisOffset = gridVerts;

            list.AddRange(new[] { -ext, yHeight + 0.1f, 0f, ext, yHeight + 0.1f, 0f }); // X axis
            list.AddRange(new[] { 0f, yHeight + 0.1f, -ext, 0f, yHeight + 0.1f, ext }); // Z axis
            return list.ToArray();
        }

        public void Dispose()
        {
            if (!_ready) return;
            _gl.DeleteBuffer(_vbo);
            _gl.DeleteVertexArray(_vao);
            _gl.DeleteProgram(_program);
            _ready = false;
        }
    }
}
