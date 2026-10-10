using System;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Utils.Rendering;
using AssetsManager.Views.Helpers;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Rendering
{
    /// <summary>Shared preview Ground, Grid and Stage with resources owned by each viewport's GL context.</summary>
    internal sealed class PreviewSurfaceRenderer : IDisposable
    {
        internal const float GroundDrop = -0.5f;
        internal const float GroundSize = 3200f;
        internal const float StageSize = GroundSize;
        internal const float GridCellSize = 100f;
        internal const float GridSectionSize = 500f;
        internal const float GridFadeStrength = 1.5f;

        private const int GroundStride = 5 * sizeof(float);
        private const GLEnum MaximumTextureAnisotropy = (GLEnum)0x84FF;
        private const TextureParameterName TextureAnisotropy = (TextureParameterName)0x84FE;

        private GL _gl;
        private GridRenderer _gridRenderer;

        private uint _groundProgram;
        private uint _groundVao;
        private uint _groundVbo;
        private uint _groundTexture;
        private BitmapSource _groundTextureSource;
        private uint _logoTexture;
        private BitmapSource _logoTextureSource;
        private Vector2 _logoUvSize = Vector2.One;
        private float _logoOpacity;
        private float _anisotropy = 1;
        private int _groundViewProjection;
        private int _groundTextureUniform;
        private int _groundTextured;
        private int _groundLogoUniform;
        private int _groundLogoSizeUniform;
        private int _groundLogoOpacityUniform;

        private uint _stageProgram;
        private uint _stageVao;
        private uint _stageVbo;
        private int _stageViewProjection;
        private int _stageHeight;
        private int _stageMode;

        private bool _ready;
        private bool _usesEmbeddedProfile;
        private GroundAppearance _groundAppearance;
        private float _groundSize;
        private float _groundHeight;
        private float _gridHeight;

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
uniform sampler2D uLogo;
uniform vec2 uLogoSize;
uniform float uLogoOpacity;
vec3 srgbToLinear(vec3 value) {
    return mix(pow((value + 0.055) / 1.055, vec3(2.4)), value / 12.92,
        lessThanEqual(value, vec3(0.04045)));
}
vec3 linearToSrgb(vec3 value) {
    vec3 low = value * 12.92;
    vec3 high = 1.055 * pow(max(value, vec3(0.0)), vec3(1.0 / 2.4)) - 0.055;
    return mix(high, low, lessThanEqual(value, vec3(0.0031308)));
}
void main() {
    vec4 color = uTextured != 0 ? texture(uTexture, vUv)
        : vec4(srgbToLinear(vec3(35.0, 42.0, 50.0) / 255.0), 1.0);
    // Preserve the logo's original orientation on the ground's reversed vertical UVs.
    vec2 logoUv = vec2(vUv.x - 0.5, 0.5 - vUv.y) / uLogoSize + 0.5;
    vec2 logoDx = dFdx(logoUv);
    vec2 logoDy = dFdy(logoUv);
    if (uLogoOpacity > 0.0 && all(greaterThanEqual(logoUv, vec2(0.0)))
        && all(lessThanEqual(logoUv, vec2(1.0)))) {
        // Premultiplication precedes filtering, so transparent RGB cannot bleed into the logo.
        vec4 logo = textureGrad(uLogo, logoUv, logoDx, logoDy) * uLogoOpacity;
        color = vec4(logo.rgb + color.rgb * (1.0 - logo.a), logo.a + color.a * (1.0 - logo.a));
    }
    fragColor = vec4(linearToSrgb(color.rgb), color.a);
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

        internal void Initialize(GL gl, GroundAppearance appearance,
            float groundSize = GroundSize, float groundHeight = GroundDrop, float gridHeight = 0f)
        {
            ArgumentNullException.ThrowIfNull(gl);
            if (_ready) return;

            _gl = gl;
            _usesEmbeddedProfile = GlShaderCompiler.UsesEmbeddedProfile(gl);
            _groundAppearance = appearance;
            _groundSize = groundSize;
            _groundHeight = groundHeight;
            _gridHeight = gridHeight;

            try
            {
                if (gl.IsExtensionPresent("GL_EXT_texture_filter_anisotropic")
                    || gl.IsExtensionPresent("GL_ARB_texture_filter_anisotropic"))
                {
                    gl.GetFloat(MaximumTextureAnisotropy, out float maximumAnisotropy);
                    _anisotropy = Math.Clamp(maximumAnisotropy, 1, 16);
                }
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
            Prepare(showGrid, showGround, showStage);

            if (showGround)
                RenderGround(viewProjection);

            if (showStage)
                RenderStage(viewProjection, drawFill: !showGround);

            if (showGrid)
                _gridRenderer?.Render(viewProjection);
        }

        internal void Prepare(bool showGrid, bool showGround, bool showStage)
        {
            if (!_ready) return;

            // Create each surface only when requested, on the viewport's current GL context.
            try
            {
                if (showGround && _groundProgram == 0)
                    InitializeGround(_usesEmbeddedProfile, _groundAppearance, _groundSize, _groundHeight);
                if (showStage && _stageProgram == 0)
                    InitializeStage(_usesEmbeddedProfile);
                if (showGrid && _gridRenderer == null)
                {
                    _gridRenderer = new GridRenderer();
                    _gridRenderer.Initialize(_gl, _usesEmbeddedProfile, _gridHeight);
                }
            }
            catch
            {
                DisposeResources();
                throw;
            }
        }

        private void InitializeGround(bool gles, GroundAppearance appearance, float size, float height)
        {
            _groundProgram = GlShaderCompiler.CreateProgram(_gl, gles, GroundVertexShader, GroundFragmentShader);
            _groundViewProjection = _gl.GetUniformLocation(_groundProgram, "uViewProjection");
            _groundTextureUniform = _gl.GetUniformLocation(_groundProgram, "uTexture");
            _groundTextured = _gl.GetUniformLocation(_groundProgram, "uTextured");
            _groundLogoUniform = _gl.GetUniformLocation(_groundProgram, "uLogo");
            _groundLogoSizeUniform = _gl.GetUniformLocation(_groundProgram, "uLogoSize");
            _groundLogoOpacityUniform = _gl.GetUniformLocation(_groundProgram, "uLogoOpacity");

            float half = size * 0.5f;
            float[] vertices =
            {
                -half, height, -half, 0f, 1f,
                 half, height, -half, 1f, 1f,
                 half, height,  half, 1f, 0f,
                -half, height,  half, 0f, 0f
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

            SetGroundAppearance(appearance);
        }

        internal void SetGroundAppearance(GroundAppearance appearance)
        {
            _groundAppearance = appearance;
            if (_gl == null || _groundProgram == 0) return;
            bool groundChanged = !ReferenceEquals(_groundTextureSource, appearance.Texture);
            bool logoChanged = !ReferenceEquals(_logoTextureSource, appearance.Logo);
            uint ground = groundChanged ? 0 : _groundTexture;
            uint logo = logoChanged ? 0 : _logoTexture;
            try
            {
                if (groundChanged) ground = appearance.Texture == null ? 0 : UploadTexture(appearance.Texture);
                if (logoChanged) logo = appearance.Logo == null ? 0 : UploadTexture(appearance.Logo, premultiplyAlpha: true);
            }
            catch
            {
                if (groundChanged && ground != 0) _gl.DeleteTexture(ground);
                throw;
            }
            if (groundChanged && _groundTexture != 0) _gl.DeleteTexture(_groundTexture);
            if (logoChanged && _logoTexture != 0) _gl.DeleteTexture(_logoTexture);
            _groundTexture = ground;
            _logoTexture = logo;
            _groundTextureSource = appearance.Texture;
            _logoTextureSource = appearance.Logo;
            _logoUvSize = appearance.LogoUvSize;
            _logoOpacity = appearance.Opacity;
        }

        private void RenderGround(Matrix4x4 viewProjection)
        {
            _gl.GetInteger(GLEnum.CurrentProgram, out int previousProgram);
            _gl.GetInteger(GLEnum.VertexArrayBinding, out int previousVao);
            _gl.GetInteger(GLEnum.ArrayBufferBinding, out int previousArrayBuffer);
            _gl.GetInteger(GLEnum.ActiveTexture, out int previousActiveTexture);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.GetInteger(GLEnum.TextureBinding2D, out int previousTexture0);
            _gl.GetInteger(GLEnum.SamplerBinding, out int previousSampler0);
            _gl.ActiveTexture(TextureUnit.Texture1);
            _gl.GetInteger(GLEnum.TextureBinding2D, out int previousTexture1);
            _gl.GetInteger(GLEnum.SamplerBinding, out int previousSampler1);
            _gl.GetInteger(GLEnum.DepthWritemask, out int previousDepthWrite);
            _gl.GetInteger(GLEnum.DepthFunc, out int previousDepthFunction);
            bool depthTest = _gl.IsEnabled(EnableCap.DepthTest);
            bool blend = _gl.IsEnabled(EnableCap.Blend);
            bool cullFace = _gl.IsEnabled(EnableCap.CullFace);

            try
            {
                _gl.Enable(EnableCap.DepthTest);
                _gl.DepthFunc(DepthFunction.Lequal);
                _gl.DepthMask(true);
                _gl.Disable(EnableCap.Blend);
                _gl.Disable(EnableCap.CullFace);
                _gl.UseProgram(_groundProgram);
                _gl.UniformMatrix4(_groundViewProjection, 1, false, in viewProjection.M11);
                _gl.Uniform1(_groundTextureUniform, 0);
                _gl.Uniform1(_groundTextured, _groundTexture != 0 ? 1 : 0);
                _gl.Uniform1(_groundLogoUniform, 1);
                _gl.Uniform2(_groundLogoSizeUniform, _logoUvSize.X, _logoUvSize.Y);
                _gl.Uniform1(_groundLogoOpacityUniform, _logoTexture != 0 ? _logoOpacity : 0f);
                _gl.ActiveTexture(TextureUnit.Texture0);
                _gl.BindTexture(TextureTarget.Texture2D, _groundTexture);
                // Scene samplers must not override the ground's filtering or wrapping.
                _gl.BindSampler(0, 0);
                _gl.ActiveTexture(TextureUnit.Texture1);
                _gl.BindTexture(TextureTarget.Texture2D, _logoTexture);
                _gl.BindSampler(1, 0);
                _gl.BindVertexArray(_groundVao);
                _gl.DrawArrays(PrimitiveType.TriangleFan, 0, 4);
            }
            finally
            {
                if (depthTest) _gl.Enable(EnableCap.DepthTest); else _gl.Disable(EnableCap.DepthTest);
                _gl.DepthMask(previousDepthWrite != 0);
                _gl.DepthFunc((DepthFunction)previousDepthFunction);
                if (blend) _gl.Enable(EnableCap.Blend); else _gl.Disable(EnableCap.Blend);
                if (cullFace) _gl.Enable(EnableCap.CullFace); else _gl.Disable(EnableCap.CullFace);
                _gl.ActiveTexture(TextureUnit.Texture0);
                _gl.BindTexture(TextureTarget.Texture2D, (uint)previousTexture0);
                _gl.BindSampler(0, (uint)previousSampler0);
                _gl.ActiveTexture(TextureUnit.Texture1);
                _gl.BindTexture(TextureTarget.Texture2D, (uint)previousTexture1);
                _gl.BindSampler(1, (uint)previousSampler1);
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

        private uint UploadTexture(BitmapSource source, bool premultiplyAlpha = false)
        {
            BitmapSource bitmap = source;
            if (bitmap.Format != PixelFormats.Bgra32)
                bitmap = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);

            int width = bitmap.PixelWidth;
            int height = bitmap.PixelHeight;
            int stride = checked(width * 4);
            var pixels = new byte[checked(height * stride)];
            bitmap.CopyPixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);

            uint texture = _gl.GenTexture();
            _gl.GetInteger(GLEnum.TextureBinding2D, out int previousTexture);
            try
            {
                _gl.BindTexture(TextureTarget.Texture2D, texture);
                if (premultiplyAlpha)
                {
                    // Linear premultiplied color keeps bilinear, mipmap and anisotropic filtering alpha-correct.
                    var linear = new float[pixels.Length];
                    for (int i = 0; i < pixels.Length; i += 4)
                    {
                        float alpha = pixels[i + 3] / 255f;
                        linear[i] = SrgbToLinear[pixels[i + 2]] * alpha;
                        linear[i + 1] = SrgbToLinear[pixels[i + 1]] * alpha;
                        linear[i + 2] = SrgbToLinear[pixels[i]] * alpha;
                        linear[i + 3] = alpha;
                    }
                    _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba16f,
                        (uint)width, (uint)height, 0, Silk.NET.OpenGL.PixelFormat.Rgba,
                        PixelType.Float, new ReadOnlySpan<float>(linear));
                }
                else
                {
                    _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Srgb8Alpha8,
                        (uint)width, (uint)height, 0, Silk.NET.OpenGL.PixelFormat.Bgra,
                        PixelType.UnsignedByte, new ReadOnlySpan<byte>(pixels));
                }
                _gl.GenerateMipmap(TextureTarget.Texture2D);
                _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
                _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
                _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
                if (_anisotropy > 1)
                    _gl.TexParameter(TextureTarget.Texture2D, TextureAnisotropy, _anisotropy);
                return texture;
            }
            catch
            {
                _gl.DeleteTexture(texture);
                throw;
            }
            finally
            {
                _gl.BindTexture(TextureTarget.Texture2D, (uint)previousTexture);
            }
        }

        private static readonly float[] SrgbToLinear = CreateSrgbToLinearTable();

        private static float[] CreateSrgbToLinearTable()
        {
            var values = new float[256];
            for (int i = 0; i < values.Length; i++)
            {
                float value = i / 255f;
                values[i] = value <= 0.04045f ? value / 12.92f : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);
            }
            return values;
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
                if (_logoTexture != 0) _gl?.DeleteTexture(_logoTexture);
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
                _groundTextureSource = null;
                _groundAppearance = default;
                _logoTexture = 0;
                _logoTextureSource = null;
                _anisotropy = 1;
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
