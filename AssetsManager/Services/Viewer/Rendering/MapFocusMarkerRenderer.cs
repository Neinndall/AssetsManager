using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Utils.Rendering;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Rendering
{
    /// <summary>
    /// Wireframe sphere used by the MAP outliner to mark the placeable the camera was sent to.
    /// Matches LTK Manager 1.20.0's MapFocus marker: radius 60, 12x8 sphere segments and no depth test.
    /// </summary>
    internal sealed class MapFocusMarkerRenderer : IDisposable
    {
        internal const float Radius = 60f;
        private const int WidthSegments = 12;
        private const int HeightSegments = 8;

        private const string VertexBody = @"
layout(location = 0) in vec3 aPos;
uniform mat4 uViewProjection;
uniform vec3 uCenter;
void main() { gl_Position = uViewProjection * vec4(aPos + uCenter, 1.0); }";

        private const string FragmentBody = @"
out vec4 FragColor;
uniform vec4 uColor;
void main() { FragColor = uColor; }";

        private GL _gl;
        private uint _program;
        private uint _vao;
        private uint _vbo;
        private int _uViewProjection;
        private int _uCenter;
        private int _uColor;
        private int _vertexCount;
        private bool _ready;

        internal void Initialize(GL gl)
        {
            ArgumentNullException.ThrowIfNull(gl);
            if (_ready) return;

            _gl = gl;
            _program = GlShaderCompiler.CreateProgram(
                gl,
                GlShaderCompiler.UsesEmbeddedProfile(gl),
                VertexBody,
                FragmentBody);
            _uViewProjection = gl.GetUniformLocation(_program, "uViewProjection");
            _uCenter = gl.GetUniformLocation(_program, "uCenter");
            _uColor = gl.GetUniformLocation(_program, "uColor");

            float[] vertices = BuildWireSphere();
            _vertexCount = vertices.Length / 3;
            _vao = gl.GenVertexArray();
            gl.BindVertexArray(_vao);
            _vbo = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
            gl.BufferData(BufferTargetARB.ArrayBuffer, new ReadOnlySpan<float>(vertices), BufferUsageARB.StaticDraw);
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), IntPtr.Zero);
            gl.BindVertexArray(0);
            _ready = true;
        }

        internal void Render(Matrix4x4 viewProjection, Vector3 center, Vector4 color)
        {
            if (!_ready || _vertexCount == 0) return;

            bool depthTest = _gl.IsEnabled(EnableCap.DepthTest);
            _gl.Disable(EnableCap.DepthTest);

            try
            {
                _gl.UseProgram(_program);
                Matrix4x4 matrix = viewProjection;
                _gl.UniformMatrix4(_uViewProjection, 1, false, in matrix.M11);
                _gl.Uniform3(_uCenter, center.X, center.Y, center.Z);
                _gl.Uniform4(_uColor, color.X, color.Y, color.Z, color.W);
                _gl.BindVertexArray(_vao);
                _gl.DrawArrays((GLEnum)PrimitiveType.Lines, 0, (uint)_vertexCount);
            }
            finally
            {
                _gl.BindVertexArray(0);
                _gl.UseProgram(0);
                if (depthTest) _gl.Enable(EnableCap.DepthTest); else _gl.Disable(EnableCap.DepthTest);
            }
        }

        internal static float[] BuildWireSphere()
        {
            var vertices = new List<float>();
            var points = new Vector3[HeightSegments + 1, WidthSegments + 1];

            for (int y = 0; y <= HeightSegments; y++)
            {
                float v = y / (float)HeightSegments;
                float phi = v * MathF.PI;
                float sinPhi = MathF.Sin(phi);
                float cosPhi = MathF.Cos(phi);
                for (int x = 0; x <= WidthSegments; x++)
                {
                    float u = x / (float)WidthSegments;
                    float theta = u * MathF.PI * 2f;
                    points[y, x] = new Vector3(
                        -Radius * MathF.Cos(theta) * sinPhi,
                        Radius * cosPhi,
                        Radius * MathF.Sin(theta) * sinPhi);
                }
            }

            for (int y = 0; y < HeightSegments; y++)
            {
                for (int x = 0; x < WidthSegments; x++)
                {
                    Vector3 a = points[y, x];
                    Vector3 b = points[y, x + 1];
                    Vector3 c = points[y + 1, x + 1];
                    Vector3 d = points[y + 1, x];
                    AddEdge(vertices, a, b);
                    AddEdge(vertices, b, c);
                    AddEdge(vertices, c, a);
                    AddEdge(vertices, c, d);
                    AddEdge(vertices, d, a);
                }
            }

            return vertices.ToArray();
        }

        private static void AddEdge(List<float> vertices, Vector3 a, Vector3 b)
        {
            vertices.Add(a.X); vertices.Add(a.Y); vertices.Add(a.Z);
            vertices.Add(b.X); vertices.Add(b.Y); vertices.Add(b.Z);
        }

        public void Dispose()
        {
            if (!_ready) return;
            try
            {
                if (_vbo != 0) _gl?.DeleteBuffer(_vbo);
                if (_vao != 0) _gl?.DeleteVertexArray(_vao);
                if (_program != 0) _gl?.DeleteProgram(_program);
            }
            catch (Silk.NET.Core.Loader.SymbolLoadingException)
            {
            }
            catch (Exception)
            {
            }
            finally
            {
                _vbo = 0;
                _vao = 0;
                _program = 0;
                _vertexCount = 0;
                _ready = false;
            }
        }
    }
}