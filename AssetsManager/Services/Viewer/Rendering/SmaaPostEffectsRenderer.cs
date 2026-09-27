using System;
using System.IO;
using AssetsManager.Services.Viewer.Rendering.Core;
using AssetsManager.Utils.Rendering;
using Silk.NET.OpenGL;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AssetsManager.Services.Viewer.Rendering
{
    /// <summary>Three.js r185 SMAA 1x Medium, with the original area and search tables.</summary>
    internal sealed class SmaaPostEffectsRenderer : IDisposable
    {
        private GL _gl;
        private GlSceneCapture _capture;
        private uint _vao, _edgesProgram, _weightsProgram, _blendProgram;
        private uint _edgesTarget, _weightsTarget, _edgesTexture, _weightsTexture, _areaTexture, _searchTexture;
        private int _width, _height;
        private bool _embedded;
        internal bool IsReady => _blendProgram != 0;

        internal void Initialize(GL gl)
        {
            if (IsReady) return;
            _gl = gl ?? throw new ArgumentNullException(nameof(gl));
            gl.GetInteger(GLEnum.ActiveTexture, out int active);
            gl.ActiveTexture(TextureUnit.Texture0);
            gl.GetInteger(GLEnum.TextureBinding2D, out int binding);
            try
            {
                bool embedded = _embedded = GlShaderCompiler.UsesEmbeddedProfile(gl);
                _edgesProgram = GlShaderCompiler.CreateProgram(gl, embedded, SmaaShaderSource.EdgesVertex, SmaaShaderSource.EdgesFragment);
                _weightsProgram = GlShaderCompiler.CreateProgram(gl, embedded, SmaaShaderSource.WeightsVertex, SmaaShaderSource.WeightsFragment);
                _blendProgram = GlShaderCompiler.CreateProgram(gl, embedded, SmaaShaderSource.BlendVertex, SmaaShaderSource.BlendFragment);
                _vao = gl.GenVertexArray();
                _capture = new GlSceneCapture(gl);
                _areaTexture = LoadLookup("Area", TextureMinFilter.Linear, TextureMagFilter.Linear);
                _searchTexture = LoadLookup("Search", TextureMinFilter.Nearest, TextureMagFilter.Nearest);
            }
            catch
            {
                Dispose();
                throw;
            }
            finally
            {
                gl.BindTexture(TextureTarget.Texture2D, (uint)binding);
                gl.ActiveTexture((TextureUnit)active);
            }
        }

        private uint LoadLookup(string name, TextureMinFilter min, TextureMagFilter mag)
        {
            using Stream stream = typeof(SmaaPostEffectsRenderer).Assembly.GetManifestResourceStream(
                "AssetsManager.Resources.Shaders.Smaa." + name + ".png")
                ?? throw new InvalidOperationException("Missing SMAA lookup: " + name);
            using var image = Image.Load<Rgba32>(stream);
            byte[] pixels = new byte[image.Width * image.Height * 4];
            image.CopyPixelDataTo(pixels);
            uint texture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, texture);
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
                (uint)image.Width, (uint)image.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, pixels.AsSpan());
            Parameters(min, mag);
            return texture;
        }

        private void Parameters(TextureMinFilter min, TextureMagFilter mag)
        {
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)min);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)mag);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        }

        private void Target(ref uint framebuffer, ref uint texture, int width, int height)
        {
            if (framebuffer == 0) framebuffer = _gl.GenFramebuffer();
            if (texture == 0) texture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, texture);
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba16f,
                (uint)width, (uint)height, 0, PixelFormat.Rgba, PixelType.HalfFloat, ReadOnlySpan<byte>.Empty);
            Parameters(TextureMinFilter.Linear, TextureMagFilter.Linear);
            _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, framebuffer);
            _gl.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, texture, 0);
            if (_gl.CheckFramebufferStatus(FramebufferTarget.DrawFramebuffer) != GLEnum.FramebufferComplete)
                throw new InvalidOperationException("SMAA framebuffer is incomplete.");
        }

        internal void Render(int width, int height)
        {
            if (!IsReady || width <= 0 || height <= 0) return;
            _capture.Capture((uint)width, (uint)height, true, false);
            RenderTexture(_capture.ColorTexture, width, height);
        }

        internal void RenderTexture(uint source, int width, int height)
        {
            if (!IsReady || source == 0 || width <= 0 || height <= 0) return;
            _gl.GetInteger(GLEnum.DrawFramebufferBinding, out int target);
            _gl.GetInteger(GLEnum.CurrentProgram, out int program);
            _gl.GetInteger(GLEnum.VertexArrayBinding, out int vao);
            _gl.GetInteger(GLEnum.ActiveTexture, out int active);
            _gl.GetInteger(GLEnum.DepthWritemask, out int depthWrite);
            Span<int> viewport = stackalloc int[4];
            Span<int> colorMask = stackalloc int[4];
            _gl.GetInteger(GLEnum.Viewport, viewport);
            _gl.GetInteger(GLEnum.ColorWritemask, colorMask);
            bool depth = _gl.IsEnabled(EnableCap.DepthTest), blend = _gl.IsEnabled(EnableCap.Blend);
            bool cull = _gl.IsEnabled(EnableCap.CullFace), scissor = _gl.IsEnabled(EnableCap.ScissorTest);
            bool stencil = _gl.IsEnabled(EnableCap.StencilTest), srgb = !_embedded && _gl.IsEnabled(EnableCap.FramebufferSrgb);
            Span<int> textures = stackalloc int[3];
            Span<int> samplers = stackalloc int[3];
            for (int unit = 0; unit < 3; unit++)
            {
                _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + unit));
                _gl.GetInteger(GLEnum.TextureBinding2D, out textures[unit]);
                _gl.GetInteger(GLEnum.SamplerBinding, out samplers[unit]);
                _gl.BindSampler((uint)unit, 0);
            }
            try
            {
                _gl.Disable(EnableCap.DepthTest);
                _gl.DepthMask(false);
                _gl.Disable(EnableCap.Blend);
                _gl.Disable(EnableCap.CullFace);
                _gl.Disable(EnableCap.ScissorTest);
                _gl.Disable(EnableCap.StencilTest);
                if (!_embedded) _gl.Disable(EnableCap.FramebufferSrgb);
                _gl.ColorMask(true, true, true, true);
                _gl.ActiveTexture(TextureUnit.Texture0);
                if (_width != width || _height != height)
                {
                    Target(ref _edgesTarget, ref _edgesTexture, width, height);
                    Target(ref _weightsTarget, ref _weightsTexture, width, height);
                    _width = width;
                    _height = height;
                }
                _gl.Viewport(0, 0, (uint)width, (uint)height);
                _gl.BindVertexArray(_vao);
                Draw(_edgesTarget, _edgesProgram, width, height, source);
                Draw(_weightsTarget, _weightsProgram, width, height, _edgesTexture, _areaTexture, _searchTexture);
                Draw((uint)target, _blendProgram, width, height, _weightsTexture, source);
            }
            finally
            {
                _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, (uint)target);
                _gl.Viewport(viewport[0], viewport[1], (uint)viewport[2], (uint)viewport[3]);
                _gl.UseProgram((uint)program);
                _gl.BindVertexArray((uint)vao);
                _gl.DepthMask(depthWrite != 0);
                _gl.ColorMask(colorMask[0] != 0, colorMask[1] != 0, colorMask[2] != 0, colorMask[3] != 0);
                Restore(EnableCap.DepthTest, depth); Restore(EnableCap.Blend, blend);
                Restore(EnableCap.CullFace, cull); Restore(EnableCap.ScissorTest, scissor);
                Restore(EnableCap.StencilTest, stencil); if (!_embedded) Restore(EnableCap.FramebufferSrgb, srgb);
                for (int unit = 0; unit < 3; unit++)
                {
                    _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + unit));
                    _gl.BindTexture(TextureTarget.Texture2D, (uint)textures[unit]);
                    _gl.BindSampler((uint)unit, (uint)samplers[unit]);
                }
                _gl.ActiveTexture((TextureUnit)active);
            }
        }

        private void Restore(EnableCap cap, bool enabled)
        {
            if (enabled) _gl.Enable(cap); else _gl.Disable(cap);
        }

        private void Draw(uint target, uint program, int width, int height, uint first, uint second = 0, uint third = 0)
        {
            _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, target);
            if (target == _edgesTarget || target == _weightsTarget)
            {
                // Edge detection discards non-edges; stale texels must never reach the weights pass.
                Span<float> clear = stackalloc float[4];
                clear.Clear();
                _gl.ClearBuffer(GLEnum.Color, 0, clear);
            }
            _gl.UseProgram(program);
            _gl.Uniform2(_gl.GetUniformLocation(program, "resolution"), 1f / width, 1f / height);
            Bind(program, "tDiffuse", 0, first);
            if (second != 0) Bind(program, program == _blendProgram ? "tColor" : "tArea", 1, second);
            if (third != 0) Bind(program, "tSearch", 2, third);
            _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        }

        private void Bind(uint program, string uniform, int unit, uint texture)
        {
            _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + unit));
            _gl.BindTexture(TextureTarget.Texture2D, texture);
            _gl.Uniform1(_gl.GetUniformLocation(program, uniform), unit);
        }

        public void Dispose()
        {
            if (_gl == null) return;
            _capture?.Dispose(); _capture = null;
            foreach (uint target in new[] { _edgesTarget, _weightsTarget })
                if (target != 0) _gl.DeleteFramebuffer(target);
            foreach (uint texture in new[] { _edgesTexture, _weightsTexture, _areaTexture, _searchTexture })
                if (texture != 0) _gl.DeleteTexture(texture);
            foreach (uint program in new[] { _edgesProgram, _weightsProgram, _blendProgram })
                if (program != 0) _gl.DeleteProgram(program);
            if (_vao != 0) _gl.DeleteVertexArray(_vao);
            _edgesTarget = _weightsTarget = _edgesTexture = _weightsTexture = _areaTexture = _searchTexture = 0;
            _edgesProgram = _weightsProgram = _blendProgram = _vao = 0;
            _width = _height = 0; _gl = null;
        }
    }
}
