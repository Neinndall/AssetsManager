using System;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Services.Viewer.Vfx.Session;
using AssetsManager.Utils.Rendering;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Vfx.Rendering
{
    /// <summary>Owns the VFX preview Grid, Ground and Stage rendering paths behind one GPU lifecycle.</summary>
    internal sealed class VfxPreviewSurfaceRenderer : IDisposable
    {
        internal const float GroundDrop = -0.5f;
        internal const float GroundSize = VfxRigMotion.ChampionHeight * 16f;
        internal const float StageSize = GroundSize;
        internal const float GridCellSize = 100f;
        internal const float GridSectionSize = 500f;
        internal const float GridFadeStrength = 1.5f;

        private const int GroundStride = 5 * sizeof(float);

        private GL _gl;
        private GridRenderer _gridRenderer;

        private uint _groundProgram;
        private uint _groundVao;
        private uint _groundVbo;
        private uint _groundTexture;
        private int _groundViewProjection;
        private int _groundTextureUniform;
        private int _groundTextured;

        private uint _stageProgram;
        private uint _stageVao;
        private uint _stageVbo;
        private int _stageViewProjection;
        private int _stageHeight;
        private int _stageMode;

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

        private const string StageVertexShader = @"
layout(location = 0) in vec2 aPosition;
uniform mat4 uViewProjection;
uniform float uHeight;
out vec2 vGround;
void main() {
    vGround = aPosition;
    gl_Position = uViewProjection * vec4(aPosition.x, uHeight, aPosition.y, 1.0);
}";

        private const string StageFragmentShader = @"
in vec2 vGround;
out vec4 fragColor;
uniform int uMode;

const vec3 GROUND_COLOR = vec3(0.105, 0.115, 0.135);
const vec3 CELL_COLOR = vec3(0.158, 0.171, 0.200);
const vec3 SECTION_COLOR = vec3(0.188, 0.201, 0.233);
const float CELL_SIZE = 100.0;
const float SECTION_SIZE = 500.0;
const float HALF_STAGE = 1600.0;
const float FADE_STRENGTH = 1.5;

float gridMask(vec2 ground, float spacing) {
    vec2 scaled = ground / spacing;
    vec2 width = max(fwidth(scaled), vec2(0.0001));
    vec2 distanceToLine = abs(fract(scaled - 0.5) - 0.5) / width;
    float nearest = min(distanceToLine.x, distanceToLine.y);
    return 1.0 - clamp(nearest, 0.0, 1.0);
}

void main() {
    if (uMode == 0) {
        fragColor = vec4(GROUND_COLOR, 1.0);
        return;
    }

    float cell = gridMask(vGround, CELL_SIZE);
    float section = gridMask(vGround, SECTION_SIZE);
    float line = max(cell, section);
    if (line <= 0.001) discard;

    float edge = clamp(1.0 - length(vGround) / HALF_STAGE, 0.0, 1.0);
    float fade = pow(edge, FADE_STRENGTH);
    vec3 lineColor = mix(CELL_COLOR, SECTION_COLOR, section);
    fragColor = vec4(lineColor, line * fade);
}";

        internal void Initialize(GL gl, BitmapSource groundTexture)
        {
            ArgumentNullException.ThrowIfNull(gl);
            if (_ready) return;

            _gl = gl;
            bool gles = GlShaderCompiler.UsesEmbeddedProfile(gl);

            try
            {
                _gridRenderer = new GridRenderer();
                _gridRenderer.Initialize(_gl, gles);
                InitializeGround(gles, groundTexture);
                InitializeStage(gles);
                _ready = true;
            }
            catch
            {
                DisposeResources();
                throw;
            }
        }

        internal void Render(
            Matrix4x4 viewProjection,
            bool showGrid,
            bool showGround,
            bool showStage)
        {
            if (!_ready) return;

            if (showGround)
                RenderGround(viewProjection);

            if (showStage)
                RenderStage(viewProjection, drawFill: !showGround);

            if (showGrid)
                _gridRenderer?.Render(viewProjection);
        }

        private void InitializeGround(bool gles, BitmapSource groundTexture)
        {
            _groundProgram = GlShaderCompiler.CreateProgram(_gl, gles, GroundVertexShader, GroundFragmentShader);
            _groundViewProjection = _gl.GetUniformLocation(_groundProgram, "uViewProjection");
            _groundTextureUniform = _gl.GetUniformLocation(_groundProgram, "uTexture");
            _groundTextured = _gl.GetUniformLocation(_groundProgram, "uTextured");

            float half = GroundSize * 0.5f;
            float[] vertices =
            {
                -half, GroundDrop, -half, 0f, 1f,
                 half, GroundDrop, -half, 1f, 1f,
                 half, GroundDrop,  half, 1f, 0f,
                -half, GroundDrop,  half, 0f, 0f
            };

            _groundVao = _gl.GenVertexArray();
            _groundVbo = _gl.GenBuffer();
            _gl.BindVertexArray(_groundVao);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _groundVbo);
            _gl.BufferData(BufferTargetARB.ArrayBuffer, new ReadOnlySpan<float>(vertices), BufferUsageARB.StaticDraw);
            _gl.EnableVertexAttribArray(0);
            _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, GroundStride, IntPtr.Zero);
            _gl.EnableVertexAttribArray(1);
            _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, GroundStride, new IntPtr(3 * sizeof(float)));
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
            _gl.BindVertexArray(0);

            if (groundTexture != null)
                _groundTexture = UploadTexture(groundTexture);
        }

        private void RenderGround(Matrix4x4 viewProjection)
        {
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
                _gl.UseProgram(_groundProgram);
                _gl.UniformMatrix4(_groundViewProjection, 1, false, in viewProjection.M11);
                _gl.Uniform1(_groundTextureUniform, 0);
                _gl.Uniform1(_groundTextured, _groundTexture != 0 ? 1 : 0);
                _gl.ActiveTexture(TextureUnit.Texture0);
                _gl.BindTexture(TextureTarget.Texture2D, _groundTexture);
                _gl.BindVertexArray(_groundVao);
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

        private void InitializeStage(bool gles)
        {
            _stageProgram = GlShaderCompiler.CreateProgram(_gl, gles, StageVertexShader, StageFragmentShader);
            _stageViewProjection = _gl.GetUniformLocation(_stageProgram, "uViewProjection");
            _stageHeight = _gl.GetUniformLocation(_stageProgram, "uHeight");
            _stageMode = _gl.GetUniformLocation(_stageProgram, "uMode");

            float half = StageSize * 0.5f;
            float[] vertices =
            {
                -half, -half,
                 half, -half,
                 half,  half,
                -half,  half
            };

            _stageVao = _gl.GenVertexArray();
            _stageVbo = _gl.GenBuffer();
            _gl.BindVertexArray(_stageVao);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _stageVbo);
            _gl.BufferData(BufferTargetARB.ArrayBuffer, new ReadOnlySpan<float>(vertices), BufferUsageARB.StaticDraw);
            _gl.EnableVertexAttribArray(0);
            _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), IntPtr.Zero);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
            _gl.BindVertexArray(0);
        }

        private void RenderStage(Matrix4x4 viewProjection, bool drawFill)
        {
            _gl.GetInteger(GLEnum.CurrentProgram, out int previousProgram);
            _gl.GetInteger(GLEnum.VertexArrayBinding, out int previousVao);
            _gl.GetInteger(GLEnum.ArrayBufferBinding, out int previousArrayBuffer);
            _gl.GetInteger(GLEnum.DepthWritemask, out int previousDepthWrite);
            _gl.GetInteger(GLEnum.DepthFunc, out int previousDepthFunction);
            _gl.GetInteger(GLEnum.BlendSrcRgb, out int previousBlendSource);
            _gl.GetInteger(GLEnum.BlendDstRgb, out int previousBlendDestination);
            _gl.GetInteger(GLEnum.BlendSrcAlpha, out int previousBlendSourceAlpha);
            _gl.GetInteger(GLEnum.BlendDstAlpha, out int previousBlendDestinationAlpha);
            _gl.GetInteger(GLEnum.BlendEquationRgb, out int previousBlendEquation);
            _gl.GetInteger(GLEnum.BlendEquationAlpha, out int previousBlendEquationAlpha);
            bool depthTest = _gl.IsEnabled(EnableCap.DepthTest);
            bool blend = _gl.IsEnabled(EnableCap.Blend);
            bool cullFace = _gl.IsEnabled(EnableCap.CullFace);

            try
            {
                _gl.Enable(EnableCap.DepthTest);
                _gl.DepthFunc(DepthFunction.Lequal);
                _gl.Disable(EnableCap.CullFace);
                _gl.UseProgram(_stageProgram);
                _gl.UniformMatrix4(_stageViewProjection, 1, false, in viewProjection.M11);
                _gl.BindVertexArray(_stageVao);

                if (drawFill)
                {
                    _gl.Disable(EnableCap.Blend);
                    _gl.DepthMask(true);
                    _gl.Uniform1(_stageHeight, GroundDrop);
                    _gl.Uniform1(_stageMode, 0);
                    _gl.DrawArrays(PrimitiveType.TriangleFan, 0, 4);
                }

                _gl.Enable(EnableCap.Blend);
                _gl.BlendEquationSeparate(GLEnum.FuncAdd, GLEnum.FuncAdd);
                _gl.BlendFuncSeparate(
                    BlendingFactor.SrcAlpha,
                    BlendingFactor.OneMinusSrcAlpha,
                    BlendingFactor.One,
                    BlendingFactor.OneMinusSrcAlpha);
                _gl.DepthMask(false);
                _gl.Uniform1(_stageHeight, 0f);
                _gl.Uniform1(_stageMode, 1);
                _gl.DrawArrays(PrimitiveType.TriangleFan, 0, 4);
            }
            finally
            {
                if (depthTest) _gl.Enable(EnableCap.DepthTest); else _gl.Disable(EnableCap.DepthTest);
                _gl.DepthMask(previousDepthWrite != 0);
                _gl.DepthFunc((DepthFunction)previousDepthFunction);
                if (blend) _gl.Enable(EnableCap.Blend); else _gl.Disable(EnableCap.Blend);
                _gl.BlendEquationSeparate((GLEnum)previousBlendEquation, (GLEnum)previousBlendEquationAlpha);
                _gl.BlendFuncSeparate(
                    (BlendingFactor)previousBlendSource,
                    (BlendingFactor)previousBlendDestination,
                    (BlendingFactor)previousBlendSourceAlpha,
                    (BlendingFactor)previousBlendDestinationAlpha);
                if (cullFace) _gl.Enable(EnableCap.CullFace); else _gl.Disable(EnableCap.CullFace);
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
            if (!_ready && _gl == null) return;
            DisposeResources();
        }

        private void DisposeResources()
        {
            try
            {
                _gridRenderer?.Dispose();
                if (_groundTexture != 0) _gl?.DeleteTexture(_groundTexture);
                if (_groundVbo != 0) _gl?.DeleteBuffer(_groundVbo);
                if (_groundVao != 0) _gl?.DeleteVertexArray(_groundVao);
                if (_groundProgram != 0) _gl?.DeleteProgram(_groundProgram);
                if (_stageVbo != 0) _gl?.DeleteBuffer(_stageVbo);
                if (_stageVao != 0) _gl?.DeleteVertexArray(_stageVao);
                if (_stageProgram != 0) _gl?.DeleteProgram(_stageProgram);
            }
            catch (Silk.NET.Core.Loader.SymbolLoadingException)
            {
                // The OpenGL context owns these handles and reclaims them on teardown.
            }
            finally
            {
                _gridRenderer = null;
                _groundTexture = 0;
                _groundVbo = 0;
                _groundVao = 0;
                _groundProgram = 0;
                _stageVbo = 0;
                _stageVao = 0;
                _stageProgram = 0;
                _ready = false;
                _gl = null;
            }
        }
    }
}
