using System;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Viewer.Vfx.Session;
using AssetsManager.Utils.Rendering;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Vfx.Rendering
{
    /// <summary>Draws the VFX preview ground using the same Rift floor texture as the main Viewer.</summary>
    internal sealed class VfxPreviewGroundRenderer : IDisposable
    {
        internal const float GroundDrop = -0.5f;
        internal const float GroundSize = VfxRigMotion.ChampionHeight * 16f;

        private const int GroundStride = 5 * sizeof(float);

        private GL _gl;
        private uint _program;
        private uint _vao;
        private uint _vbo;
        private uint _texture;
        private int _viewProjection;
        private int _groundTexture;
        private int _textured;
        private bool _ready;

        private const string GroundVertexShader = @"
layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec2 aUv;
uniform mat4 uViewProjection;
out vec2 vUv;
void main() {
    gl_Position = uViewProjection * vec4(aPosition, 1.0);
    vUv = aUv;
}";

        private const string GroundFragmentShader = @"
in vec2 vUv;
out vec4 fragColor;
uniform sampler2D uTexture;
uniform int uTextured;
void main() {
    fragColor = uTextured != 0
        ? texture(uTexture, vUv)
        : vec4(0.105, 0.115, 0.135, 1.0);
}";

        internal void Initialize(GL gl, BitmapSource groundTexture)
        {
            ArgumentNullException.ThrowIfNull(gl);
            if (_ready) return;

            _gl = gl;
            bool gles = GlShaderCompiler.UsesEmbeddedProfile(gl);
            _program = GlShaderCompiler.CreateProgram(_gl, gles, GroundVertexShader, GroundFragmentShader);
            _viewProjection = _gl.GetUniformLocation(_program, "uViewProjection");
            _groundTexture = _gl.GetUniformLocation(_program, "uTexture");
            _textured = _gl.GetUniformLocation(_program, "uTextured");

            float half = GroundSize * 0.5f;
            float[] vertices =
            {
                -half, GroundDrop, -half, 0f, 1f,
                 half, GroundDrop, -half, 1f, 1f,
                 half, GroundDrop,  half, 1f, 0f,
                -half, GroundDrop,  half, 0f, 0f
            };

            _vao = _gl.GenVertexArray();
            _vbo = _gl.GenBuffer();
            _gl.BindVertexArray(_vao);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
            _gl.BufferData(BufferTargetARB.ArrayBuffer, new ReadOnlySpan<float>(vertices), BufferUsageARB.StaticDraw);
            _gl.EnableVertexAttribArray(0);
            _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, GroundStride, IntPtr.Zero);
            _gl.EnableVertexAttribArray(1);
            _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, GroundStride, new IntPtr(3 * sizeof(float)));
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
            _gl.BindVertexArray(0);

            if (groundTexture != null)
                _texture = UploadTexture(groundTexture);

            _ready = true;
        }

        internal void Render(Matrix4x4 viewProjection)
        {
            if (!_ready) return;

            _gl.GetInteger(GLEnum.CurrentProgram, out int previousProgram);
            _gl.GetInteger(GLEnum.VertexArrayBinding, out int previousVao);
            _gl.GetInteger(GLEnum.ArrayBufferBinding, out int previousArrayBuffer);
            _gl.GetInteger(GLEnum.ActiveTexture, out int previousActiveTexture);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.GetInteger(GLEnum.TextureBinding2D, out int previousTexture0);
            _gl.GetInteger(GLEnum.DepthWritemask, out int previousDepthWrite);
            bool depthTest = _gl.IsEnabled(EnableCap.DepthTest);
            bool blend = _gl.IsEnabled(EnableCap.Blend);
            bool cullFace = _gl.IsEnabled(EnableCap.CullFace);

            try
            {
                _gl.Enable(EnableCap.DepthTest);
                _gl.DepthMask(true);
                _gl.Disable(EnableCap.Blend);
                _gl.Disable(EnableCap.CullFace);
                _gl.UseProgram(_program);
                _gl.UniformMatrix4(_viewProjection, 1, false, in viewProjection.M11);
                _gl.Uniform1(_groundTexture, 0);
                _gl.Uniform1(_textured, _texture != 0 ? 1 : 0);
                _gl.ActiveTexture(TextureUnit.Texture0);
                _gl.BindTexture(TextureTarget.Texture2D, _texture);
                _gl.BindVertexArray(_vao);
                _gl.DrawArrays(PrimitiveType.TriangleFan, 0, 4);
            }
            finally
            {
                if (depthTest) _gl.Enable(EnableCap.DepthTest); else _gl.Disable(EnableCap.DepthTest);
                _gl.DepthMask(previousDepthWrite != 0);
                if (blend) _gl.Enable(EnableCap.Blend); else _gl.Disable(EnableCap.Blend);
                if (cullFace) _gl.Enable(EnableCap.CullFace); else _gl.Disable(EnableCap.CullFace);
                _gl.ActiveTexture(TextureUnit.Texture0);
                _gl.BindTexture(TextureTarget.Texture2D, (uint)previousTexture0);
                _gl.ActiveTexture((TextureUnit)previousActiveTexture);
                _gl.BindBuffer(BufferTargetARB.ArrayBuffer, (uint)previousArrayBuffer);
                _gl.BindVertexArray((uint)previousVao);
                _gl.UseProgram((uint)previousProgram);
            }
        }

        private uint UploadTexture(BitmapSource source)
        {
            BitmapSource bitmap = source;
            if (bitmap.Format != PixelFormats.Bgra32)
                bitmap = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);

            int width = bitmap.PixelWidth;
            int height = bitmap.PixelHeight;
            int stride = width * 4;
            var pixels = new byte[height * stride];
            bitmap.CopyPixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);

            uint texture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, texture);
            _gl.TexImage2D(
                TextureTarget.Texture2D,
                0,
                InternalFormat.Rgba8,
                (uint)width,
                (uint)height,
                0,
                Silk.NET.OpenGL.PixelFormat.Bgra,
                PixelType.UnsignedByte,
                new ReadOnlySpan<byte>(pixels));
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            return texture;
        }

        public void Dispose()
        {
            if (!_ready) return;
            try
            {
                if (_texture != 0) _gl?.DeleteTexture(_texture);
                if (_vbo != 0) _gl?.DeleteBuffer(_vbo);
                if (_vao != 0) _gl?.DeleteVertexArray(_vao);
                if (_program != 0) _gl?.DeleteProgram(_program);
            }
            catch (Silk.NET.Core.Loader.SymbolLoadingException)
            {
                // The OpenGL context owns these handles and reclaims them on teardown.
            }
            finally
            {
                _texture = 0;
                _vbo = 0;
                _vao = 0;
                _program = 0;
                _ready = false;
            }
        }
    }
}
