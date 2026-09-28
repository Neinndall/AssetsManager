using System;
using AssetsManager.Utils.Rendering;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Rendering
{
    /// <summary>Draws a transparency-reference backdrop without changing scene depth or stencil.</summary>
    internal sealed class CheckerboardBackgroundRenderer : IDisposable
    {
        private const string VertexShader = @"
void main() {
    vec2 corner = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
    gl_Position = vec4(corner * 2.0 - 1.0, 0.0, 1.0);
}";

        private const string FragmentShader = @"
uniform float uCellSize;
out vec4 fragColor;
void main() {
    vec2 cell = floor(gl_FragCoord.xy / uCellSize);
    float shade = mix(0.18, 0.26, mod(cell.x + cell.y, 2.0));
    fragColor = vec4(vec3(shade), 1.0);
}";

        private readonly GL _gl;
        private uint _program;
        private uint _vao;
        private readonly int _cellSize;

        internal CheckerboardBackgroundRenderer(GL gl)
        {
            _gl = gl ?? throw new ArgumentNullException(nameof(gl));
            _program = GlShaderCompiler.CreateProgram(gl, GlShaderCompiler.UsesEmbeddedProfile(gl),
                VertexShader, FragmentShader);
            _vao = gl.GenVertexArray();
            _cellSize = gl.GetUniformLocation(_program, "uCellSize");
        }

        internal void Render(float cellSize)
        {
            if (_program == 0) return;
            _gl.GetInteger(GLEnum.CurrentProgram, out int program);
            _gl.GetInteger(GLEnum.VertexArrayBinding, out int vao);
            _gl.GetInteger(GLEnum.DepthWritemask, out int depthWrite);
            bool depth = _gl.IsEnabled(EnableCap.DepthTest);
            bool blend = _gl.IsEnabled(EnableCap.Blend);
            bool cull = _gl.IsEnabled(EnableCap.CullFace);
            bool stencil = _gl.IsEnabled(EnableCap.StencilTest);
            bool scissor = _gl.IsEnabled(EnableCap.ScissorTest);
            try
            {
                _gl.Disable(EnableCap.DepthTest);
                _gl.DepthMask(false);
                _gl.Disable(EnableCap.Blend);
                _gl.Disable(EnableCap.CullFace);
                _gl.Disable(EnableCap.StencilTest);
                _gl.Disable(EnableCap.ScissorTest);
                _gl.UseProgram(_program);
                _gl.Uniform1(_cellSize, Math.Max(1f, cellSize));
                _gl.BindVertexArray(_vao);
                _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
            }
            finally
            {
                Restore(EnableCap.DepthTest, depth);
                _gl.DepthMask(depthWrite != 0);
                Restore(EnableCap.Blend, blend);
                Restore(EnableCap.CullFace, cull);
                Restore(EnableCap.StencilTest, stencil);
                Restore(EnableCap.ScissorTest, scissor);
                _gl.BindVertexArray((uint)vao);
                _gl.UseProgram((uint)program);
            }
        }

        private void Restore(EnableCap cap, bool enabled)
        {
            if (enabled) _gl.Enable(cap); else _gl.Disable(cap);
        }

        public void Dispose()
        {
            if (_vao != 0) _gl.DeleteVertexArray(_vao);
            if (_program != 0) _gl.DeleteProgram(_program);
            _vao = _program = 0;
        }
    }
}
