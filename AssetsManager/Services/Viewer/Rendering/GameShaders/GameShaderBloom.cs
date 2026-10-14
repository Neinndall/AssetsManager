using System;
using System.Linq;
using AssetsManager.Services.Viewer.Rendering.Core;
using AssetsManager.Utils.Rendering;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Rendering.GameShaders
{
    /// <summary>
    /// The glow game skin shaders write to their second target (<c>SV_Target1</c>, <c>FEATURE_BLOOM</c>): swords,
    /// eyes and runes. A renderer redraws its game passes between <see cref="BeginPasses"/> and
    /// <see cref="EndPasses"/>, which route output 1 into a bloom texture depth-tested against the scene drawn so
    /// far; <see cref="Compose"/> adds it to the frame once through the game's mip chain: the glow is downsampled
    /// level by level with <c>MipChainBloomDownsample</c>, upsampled back with <c>MipChainBloomUpsample</c> adding
    /// each level onto the finer one, and summed onto the scene (<c>ps_copy_post</c> with <c>BLOOM ADDITIVE</c>).
    /// The kernels are the game's; the level count and <c>BLOOM_INTENSITY_SCALE</c>, which the executable sets,
    /// are ours: the scale keeps the energy the shaders wrote.
    /// </summary>
    internal sealed class GameShaderBloom : IDisposable
    {
        internal const int Levels = 5;
        internal const float Strength = 1f / Levels;

        private const string FullscreenVertex = @"
out vec2 vUv;

void main()
{
    vec2 corner = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
    vUv = corner;
    gl_Position = vec4(corner * 2.0 - 1.0, 0.0, 1.0);
}";

        // ASSETS/Shaders/HLSL/Filters/MipChainBloomDownsample.ps: 13 taps at one and two source texels (uStep).
        private const string DownsampleFragment = @"
uniform sampler2D uSource;
uniform vec2 uStep;

in vec2 vUv;
out vec4 FragColor;

vec3 tap(float x, float y) { return texture(uSource, vUv + uStep * vec2(x, y)).rgb; }

void main()
{
    vec3 sum = tap(0.0, 0.0) * 0.5
        + (tap(-1.0, -1.0) + tap(1.0, -1.0) + tap(-1.0, 1.0) + tap(1.0, 1.0)) * 0.5
        + (tap(0.0, -2.0) + tap(-2.0, 0.0) + tap(2.0, 0.0) + tap(0.0, 2.0)) * 0.25
        + (tap(-2.0, -2.0) + tap(2.0, -2.0) + tap(-2.0, 2.0) + tap(2.0, 2.0)) * 0.125;
    FragColor = vec4(sum * 0.25, 1.0);
}";

        // ASSETS/Shaders/HLSL/Filters/MipChainBloomUpsample.ps: 3x3 tent over the coarser level.
        private const string UpsampleFragment = @"
uniform sampler2D uSource;
uniform vec2 uStep;

in vec2 vUv;
out vec4 FragColor;

vec3 tap(float x, float y) { return texture(uSource, vUv + uStep * vec2(x, y)).rgb; }

void main()
{
    vec3 sum = tap(0.0, 0.0) * 4.0
        + (tap(0.0, -1.0) + tap(-1.0, 0.0) + tap(1.0, 0.0) + tap(0.0, 1.0)) * 2.0
        + tap(-1.0, -1.0) + tap(1.0, -1.0) + tap(-1.0, 1.0) + tap(1.0, 1.0);
    FragColor = vec4(sum * 0.0625, 1.0);
}";

        // ASSETS/Shaders/HLSL/Gamma/ps_copy_post.ps with BLOOM ADDITIVE: scene + bloom * BLOOM_INTENSITY_SCALE.
        private const string ComposeFragment = @"
uniform sampler2D uBloom;
uniform float uStrength;

in vec2 vUv;
out vec4 FragColor;

void main()
{
    FragColor = vec4(max(texture(uBloom, vUv).rgb, vec3(0.0)) * uStrength, 0.0);
}";

        private GL _gl;
        private GlSceneCapture _depth;
        private uint _downsampleProgram;
        private uint _upsampleProgram;
        private uint _composeProgram;
        private uint _vao;
        private Target _glow;
        private Target[] _levels = Array.Empty<Target>();
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
            _downsampleProgram = GlShaderCompiler.CreateProgram(gl, embedded, FullscreenVertex, DownsampleFragment);
            _upsampleProgram = GlShaderCompiler.CreateProgram(gl, embedded, FullscreenVertex, UpsampleFragment);
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

        /// <summary>Adds this frame's glow, spread by the mip chain, to the bound framebuffer when any game pass wrote some.</summary>
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

            _gl.UseProgram(_downsampleProgram);
            Target source = _glow;
            foreach (Target level in _levels)
            {
                Pass(_downsampleProgram, source, level);
                source = level;
            }

            // Each coarser level, tent-filtered, adds onto the finer one: the finest ends with every level's glow.
            _gl.UseProgram(_upsampleProgram);
            _gl.Enable(EnableCap.Blend);
            _gl.BlendEquation(BlendEquationModeEXT.FuncAdd);
            _gl.BlendFunc(BlendingFactor.One, BlendingFactor.One);
            for (int at = _levels.Length - 1; at > 0; at--)
                Pass(_upsampleProgram, _levels[at], _levels[at - 1]);

            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)target);
            _gl.Viewport(_previousViewport[0], _previousViewport[1], (uint)_previousViewport[2], (uint)_previousViewport[3]);
            // Alpha stays as drawn, so a transparent-background capture keeps its coverage.
            _gl.ColorMask(true, true, true, false);
            _gl.UseProgram(_composeProgram);
            Bind(0, _levels[0].Texture, _composeProgram, "uBloom");
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

        // Filters `source` into `into`; UVStep is one texel of the source, as the game sets it.
        private void Pass(uint program, Target source, Target into)
        {
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, into.Framebuffer);
            _gl.Viewport(0, 0, into.Width, into.Height);
            Bind(0, source.Texture, program, "uSource");
            _gl.Uniform2(_gl.GetUniformLocation(program, "uStep"), 1f / source.Width, 1f / source.Height);
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
            _levels = new Target[Levels];
            for (int at = 0; at < Levels; at++)
                _levels[at] = CreateTarget(Math.Max(1, width >> (at + 1)), Math.Max(1, height >> (at + 1)));
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
            foreach (Target target in _levels.Prepend(_glow))
            {
                if (target == null)
                    continue;
                _gl.DeleteFramebuffer(target.Framebuffer);
                _gl.DeleteTexture(target.Texture);
            }
            _glow = null;
            _levels = Array.Empty<Target>();
        }

        public void Dispose()
        {
            if (_gl == null)
                return;
            DeleteTargets();
            _depth?.Dispose();
            if (_downsampleProgram != 0) _gl.DeleteProgram(_downsampleProgram);
            if (_upsampleProgram != 0) _gl.DeleteProgram(_upsampleProgram);
            if (_composeProgram != 0) _gl.DeleteProgram(_composeProgram);
            if (_vao != 0) _gl.DeleteVertexArray(_vao);
            _gl = null;
        }
    }
}
