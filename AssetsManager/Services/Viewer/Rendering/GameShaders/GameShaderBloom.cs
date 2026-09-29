using System;
using AssetsManager.Services.Viewer.Rendering.Core;
using AssetsManager.Utils.Rendering;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Rendering.GameShaders
{
    /// <summary>
    /// The glow game skin shaders write to their second target (<c>SV_Target1</c>, <c>FEATURE_BLOOM</c>): swords,
    /// eyes and runes. A renderer redraws its game passes between <see cref="BeginPasses"/> and
    /// <see cref="EndPasses"/>, which route output 1 into a bloom texture depth-tested against the scene drawn so
    /// far; <see cref="Compose"/> blurs it at half and quarter resolution and adds it to the frame once.
    /// </summary>
    internal sealed class GameShaderBloom : IDisposable
    {
        internal const float Strength = 1f;

        private const string FullscreenVertex = @"
out vec2 vUv;

void main()
{
    vec2 corner = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
    vUv = corner;
    gl_Position = vec4(corner * 2.0 - 1.0, 0.0, 1.0);
}";

        // Nine-tap Gaussian along uStride; the downsample pass uses it at a coarser level too.
        private const string BlurFragment = @"
uniform sampler2D uSource;
uniform vec2 uStride;

in vec2 vUv;
out vec4 FragColor;

void main()
{
    const float weights[5] = float[5](0.2270270, 0.1945946, 0.1216216, 0.0540541, 0.0162162);
    vec3 sum = texture(uSource, vUv).rgb * weights[0];
    for (int i = 1; i < 5; i++)
    {
        sum += texture(uSource, vUv + uStride * float(i)).rgb * weights[i];
        sum += texture(uSource, vUv - uStride * float(i)).rgb * weights[i];
    }
    FragColor = vec4(sum, 1.0);
}";

        private const string ComposeFragment = @"
uniform sampler2D uHalf;
uniform sampler2D uQuarter;
uniform float uStrength;

in vec2 vUv;
out vec4 FragColor;

void main()
{
    // The two blur levels share the glow, so the halo keeps the energy the shader wrote.
    vec3 glow = (texture(uHalf, vUv).rgb + texture(uQuarter, vUv).rgb) * 0.5;
    FragColor = vec4(max(glow, vec3(0.0)) * uStrength, 0.0);
}";

        private GL _gl;
        private GlSceneCapture _depth;
        private uint _blurProgram;
        private uint _composeProgram;
        private uint _vao;
        private Target _glow;
        private Target _halfA, _halfB, _quarterA, _quarterB;
        private uint _width, _height;
        private bool _pending;
        private int _previousFramebuffer;
        private readonly int[] _previousViewport = new int[4];

        private sealed record Target(uint Texture, uint Framebuffer, uint Width, uint Height);

        internal void Initialize(GL gl)
        {
            if (_gl != null)
                return;
            _gl = gl;
            bool embedded = GlShaderCompiler.UsesEmbeddedProfile(gl);
            _blurProgram = GlShaderCompiler.CreateProgram(gl, embedded, FullscreenVertex, BlurFragment);
            _composeProgram = GlShaderCompiler.CreateProgram(gl, embedded, FullscreenVertex, ComposeFragment);
            _vao = gl.GenVertexArray();
            _depth = new GlSceneCapture(gl);
        }

        /// <summary>
        /// Points output 1 of the game programs at the bloom texture, over a copy of the depth drawn so far so
        /// hidden glow stays hidden. The first call of a frame clears the glow of the last one.
        /// </summary>
        internal bool BeginPasses()
        {
            if (_gl == null)
                return false;

            _gl.GetInteger(GLEnum.FramebufferBinding, out _previousFramebuffer);
            _gl.GetInteger(GLEnum.Viewport, _previousViewport);
            uint width = (uint)Math.Max(1, _previousViewport[2]);
            uint height = (uint)Math.Max(1, _previousViewport[3]);
            EnsureTargets(width, height);

            _depth.Capture(width, height, captureColor: false, captureDepth: true);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _glow.Framebuffer);
            _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, _depth.DepthTexture, 0);
            _gl.DrawBuffers(2, stackalloc GLEnum[] { GLEnum.None, GLEnum.ColorAttachment0 });
            _gl.Viewport(0, 0, width, height);
            if (!_pending)
            {
                _gl.ClearColor(0f, 0f, 0f, 0f);
                _gl.Clear(ClearBufferMask.ColorBufferBit);
                _pending = true;
            }
            return true;
        }

        internal void EndPasses()
        {
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)_previousFramebuffer);
            _gl.Viewport(_previousViewport[0], _previousViewport[1], (uint)_previousViewport[2], (uint)_previousViewport[3]);
        }

        /// <summary>Adds this frame's blurred glow to the bound framebuffer, when any game pass wrote some.</summary>
        internal void Compose()
        {
            if (_gl == null || !_pending)
                return;
            _pending = false;

            _gl.GetInteger(GLEnum.FramebufferBinding, out int target);
            _gl.GetInteger(GLEnum.Viewport, _previousViewport);
            _gl.Disable(EnableCap.DepthTest);
            _gl.DepthMask(false);
            _gl.Disable(EnableCap.CullFace);
            _gl.Disable(EnableCap.Blend);
            _gl.BindVertexArray(_vao);
            _gl.UseProgram(_blurProgram);

            Blur(_glow, _halfA, _halfB);
            Blur(_halfA, _quarterA, _quarterB);

            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)target);
            _gl.Viewport(_previousViewport[0], _previousViewport[1], (uint)_previousViewport[2], (uint)_previousViewport[3]);
            _gl.Enable(EnableCap.Blend);
            _gl.BlendEquation(BlendEquationModeEXT.FuncAdd);
            _gl.BlendFunc(BlendingFactor.One, BlendingFactor.One);
            // Alpha stays as drawn, so a transparent-background capture keeps its coverage.
            _gl.ColorMask(true, true, true, false);
            _gl.UseProgram(_composeProgram);
            Bind(0, _halfA.Texture, _composeProgram, "uHalf");
            Bind(1, _quarterA.Texture, _composeProgram, "uQuarter");
            _gl.Uniform1(_gl.GetUniformLocation(_composeProgram, "uStrength"), Strength);
            _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

            _gl.ColorMask(true, true, true, true);
            _gl.Disable(EnableCap.Blend);
            _gl.Enable(EnableCap.DepthTest);
            _gl.DepthMask(true);
            _gl.BindVertexArray(0);
            _gl.UseProgram(0);
            _gl.ActiveTexture(TextureUnit.Texture0);
        }

        // Downsamples `source` into `into`, blurs it horizontally into `scratch` and vertically back into `into`.
        private void Blur(Target source, Target into, Target scratch)
        {
            _gl.Viewport(0, 0, into.Width, into.Height);
            Pass(source.Texture, into.Framebuffer, 0f, 0f);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, into.Framebuffer);
            Pass(into.Texture, scratch.Framebuffer, 1f / into.Width, 0f);
            Pass(scratch.Texture, into.Framebuffer, 0f, 1f / into.Height);
        }

        private void Pass(uint source, uint framebuffer, float strideX, float strideY)
        {
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            Bind(0, source, _blurProgram, "uSource");
            _gl.Uniform2(_gl.GetUniformLocation(_blurProgram, "uStride"), strideX, strideY);
            _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        }

        private void Bind(int unit, uint texture, uint program, string name)
        {
            _gl.ActiveTexture(TextureUnit.Texture0 + unit);
            _gl.BindTexture(TextureTarget.Texture2D, texture);
            _gl.Uniform1(_gl.GetUniformLocation(program, name), unit);
        }

        private void EnsureTargets(uint width, uint height)
        {
            if (_glow != null && _width == width && _height == height)
                return;
            DeleteTargets();
            // The game writes SV_Target1 to an 8-bit target, so glow clamps at 1: Aatrox Skin33's body pulses
            // Bloom_Intensity up to 10, which would otherwise bloom ten times too bright.
            _glow = CreateTarget(width, height, lowDynamicRange: true);
            uint halfWidth = Math.Max(1, width / 2), halfHeight = Math.Max(1, height / 2);
            uint quarterWidth = Math.Max(1, width / 4), quarterHeight = Math.Max(1, height / 4);
            _halfA = CreateTarget(halfWidth, halfHeight);
            _halfB = CreateTarget(halfWidth, halfHeight);
            _quarterA = CreateTarget(quarterWidth, quarterHeight);
            _quarterB = CreateTarget(quarterWidth, quarterHeight);
            _width = width;
            _height = height;
            _pending = false;
        }

        private Target CreateTarget(uint width, uint height, bool lowDynamicRange = false)
        {
            uint texture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, texture);
            if (lowDynamicRange)
                _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, width, height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, ReadOnlySpan<byte>.Empty);
            else
                _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba16f, width, height, 0, PixelFormat.Rgba, PixelType.HalfFloat, ReadOnlySpan<byte>.Empty);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            _gl.BindTexture(TextureTarget.Texture2D, 0);

            uint framebuffer = _gl.GenFramebuffer();
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, texture, 0);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)_previousFramebuffer);
            return new Target(texture, framebuffer, width, height);
        }

        private void DeleteTargets()
        {
            foreach (Target target in new[] { _glow, _halfA, _halfB, _quarterA, _quarterB })
            {
                if (target == null)
                    continue;
                _gl.DeleteFramebuffer(target.Framebuffer);
                _gl.DeleteTexture(target.Texture);
            }
            _glow = _halfA = _halfB = _quarterA = _quarterB = null;
        }

        public void Dispose()
        {
            if (_gl == null)
                return;
            DeleteTargets();
            _depth?.Dispose();
            if (_blurProgram != 0) _gl.DeleteProgram(_blurProgram);
            if (_composeProgram != 0) _gl.DeleteProgram(_composeProgram);
            if (_vao != 0) _gl.DeleteVertexArray(_vao);
            _gl = null;
        }
    }
}
