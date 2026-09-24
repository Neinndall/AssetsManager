using System;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Resources;
using AssetsManager.Utils.Rendering;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Rendering
{
    /// <summary>
    /// Draws the active Studio sky cube. The source can be the generic AssetsManager environment or
    /// an authored MAP cubemap; all GPU creation/replacement stays deferred to Render while the GL context is current.
    /// </summary>
    internal sealed class SkyRenderer : IDisposable
    {
        private static readonly float[] CubeVertices =
        {
            -1f,-1f,-1f,  1f,-1f,-1f,  1f, 1f,-1f,   1f, 1f,-1f, -1f, 1f,-1f, -1f,-1f,-1f,
             1f,-1f,-1f,  1f,-1f, 1f,  1f, 1f, 1f,   1f, 1f, 1f,  1f, 1f,-1f,  1f,-1f,-1f,
             1f,-1f, 1f, -1f,-1f, 1f, -1f, 1f, 1f,  -1f, 1f, 1f,  1f, 1f, 1f,  1f,-1f, 1f,
            -1f,-1f, 1f, -1f,-1f,-1f,-1f, 1f,-1f,  -1f, 1f,-1f,-1f, 1f, 1f, -1f,-1f, 1f,
            -1f, 1f,-1f,  1f, 1f,-1f,  1f, 1f, 1f,   1f, 1f, 1f, -1f, 1f, 1f, -1f, 1f,-1f,
            -1f,-1f, 1f,  1f,-1f, 1f,  1f,-1f,-1f,   1f,-1f,-1f,-1f,-1f,-1f,-1f,-1f, 1f
        };

        private const string VertexShader = @"
layout(location = 0) in vec3 aPosition;
uniform mat4 uViewProjection;
out vec3 vDirection;
void main() {
    vDirection = aPosition;
    vec4 clip = uViewProjection * vec4(aPosition, 1.0);
    gl_Position = clip.xyww;
}";

        private const string FragmentShader = @"
in vec3 vDirection;
out vec4 fragColor;
uniform samplerCube uSky;
void main() {
    fragColor = vec4(texture(uSky, normalize(vDirection)).rgb, 1.0);
}";

        private GL _gl;
        private uint _program;
        private uint _vao;
        private uint _vbo;
        private uint _texture;
        private int _uViewProjection;
        private int _uSky;
        private VfxCubeMapData _pendingCube;
        private bool _replacePending;
        private bool _ready;

        internal void Initialize(GL gl)
        {
            ArgumentNullException.ThrowIfNull(gl);
            if (_ready) return;

            _gl = gl;
            bool gles = GlShaderCompiler.UsesEmbeddedProfile(gl);
            try
            {
                _program = GlShaderCompiler.CreateProgram(gl, gles, VertexShader, FragmentShader);
                _uViewProjection = gl.GetUniformLocation(_program, "uViewProjection");
                _uSky = gl.GetUniformLocation(_program, "uSky");

                _vao = gl.GenVertexArray();
                _vbo = gl.GenBuffer();
                gl.BindVertexArray(_vao);
                gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
                gl.BufferData(BufferTargetARB.ArrayBuffer, new ReadOnlySpan<float>(CubeVertices), BufferUsageARB.StaticDraw);
                gl.EnableVertexAttribArray(0);
                gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), IntPtr.Zero);
                gl.BindVertexArray(0);
                gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);

                gl.UseProgram(_program);
                gl.Uniform1(_uSky, 0);
                gl.UseProgram(0);
                _ready = true;
            }
            catch
            {
                DisposeResources();
                throw;
            }
        }

        internal void SetCube(VfxCubeMapData cube)
        {
            _pendingCube = cube;
            _replacePending = true;
        }

        internal void Render(Matrix4x4 view, Matrix4x4 projection)
        {
            if (!_ready) return;
            ApplyPendingCube();
            if (_texture == 0) return;

            Matrix4x4 skyView = view;
            skyView.M41 = 0f;
            skyView.M42 = 0f;
            skyView.M43 = 0f;
            Matrix4x4 viewProjection = skyView * projection;

            bool depthEnabled = _gl.IsEnabled(EnableCap.DepthTest);
            bool cullEnabled = _gl.IsEnabled(EnableCap.CullFace);
            _gl.DepthMask(false);
            _gl.Disable(EnableCap.DepthTest);
            _gl.Disable(EnableCap.CullFace);
            try
            {
                _gl.UseProgram(_program);
                _gl.UniformMatrix4(_uViewProjection, 1, false, in viewProjection.M11);
                _gl.ActiveTexture(TextureUnit.Texture0);
                _gl.BindTexture(TextureTarget.TextureCubeMap, _texture);
                _gl.BindVertexArray(_vao);
                _gl.DrawArrays(PrimitiveType.Triangles, 0, 36);
            }
            finally
            {
                _gl.BindVertexArray(0);
                _gl.BindTexture(TextureTarget.TextureCubeMap, 0);
                _gl.UseProgram(0);
                if (cullEnabled) _gl.Enable(EnableCap.CullFace);
                if (depthEnabled) _gl.Enable(EnableCap.DepthTest);
                _gl.DepthMask(true);
            }
        }

        private void ApplyPendingCube()
        {
            if (!_replacePending) return;
            _replacePending = false;
            if (_texture != 0)
            {
                _gl.DeleteTexture(_texture);
                _texture = 0;
            }

            VfxCubeMapData cube = _pendingCube;
            _pendingCube = null;
            if (cube?.IsValid != true) return;

            _texture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.TextureCubeMap, _texture);
            for (int face = 0; face < 6; face++)
            {
                TextureTarget target = (TextureTarget)((int)TextureTarget.TextureCubeMapPositiveX + face);
                _gl.TexImage2D(
                    target,
                    0,
                    InternalFormat.Srgb8Alpha8,
                    (uint)cube.Width,
                    (uint)cube.Height,
                    0,
                    PixelFormat.Rgba,
                    PixelType.UnsignedByte,
                    new ReadOnlySpan<byte>(cube.Faces[face]));
            }
            _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
            _gl.BindTexture(TextureTarget.TextureCubeMap, 0);
        }

        public void Dispose()
        {
            DisposeResources();
            GC.SuppressFinalize(this);
        }

        private void DisposeResources()
        {
            if (_gl != null)
            {
                if (_texture != 0) _gl.DeleteTexture(_texture);
                if (_vbo != 0) _gl.DeleteBuffer(_vbo);
                if (_vao != 0) _gl.DeleteVertexArray(_vao);
                if (_program != 0) _gl.DeleteProgram(_program);
            }

            _texture = 0;
            _vbo = 0;
            _vao = 0;
            _program = 0;
            _pendingCube = null;
            _replacePending = false;
            _ready = false;
        }
    }
}
