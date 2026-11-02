using System;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Rendering.Core
{
    /// <summary>
    /// Reusable copy of the active framebuffer color/depth into textures. Shared by VFX soft
    /// particles/distortion and MAP screen-space post effects so framebuffer capture has one owner.
    /// </summary>
    internal sealed class GlSceneCapture : IDisposable
    {
        private readonly GL _gl;
        private int _colorWidth;
        private int _colorHeight;
        private int _depthWidth;
        private int _depthHeight;
        private InternalFormat _depthFormat;
        private uint _resolveFramebuffer;

        internal GlSceneCapture(GL gl)
        {
            _gl = gl ?? throw new ArgumentNullException(nameof(gl));
        }

        internal uint ColorTexture { get; private set; }
        internal uint DepthTexture { get; private set; }
        internal int Width { get; private set; }
        internal int Height { get; private set; }

        internal void Capture(uint width, uint height, bool captureColor, bool captureDepth)
        {
            if (width == 0 || height == 0 || (!captureColor && !captureDepth))
                return;

            _gl.GetInteger(GLEnum.ActiveTexture, out int activeTexture);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.GetInteger(GLEnum.TextureBinding2D, out int textureBinding);
            try
            {
                int readFramebuffer = _gl.GetInteger(GLEnum.ReadFramebufferBinding);
                int drawFramebuffer = _gl.GetInteger(GLEnum.DrawFramebufferBinding);
                _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, (uint)readFramebuffer);
                bool multisampled = _gl.GetInteger(GLEnum.Samples) > 0;
                _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, (uint)drawFramebuffer);
                InternalFormat depthFormat = InternalFormat.DepthComponent24;
                if (captureDepth && multisampled)
                {
                    _gl.GetFramebufferAttachmentParameter(GLEnum.ReadFramebuffer,
                        readFramebuffer == 0 ? (GLEnum)0x1801 : GLEnum.DepthAttachment,
                        (GLEnum)0x8216, out int bits);
                    if (bits == 32) depthFormat = InternalFormat.DepthComponent32f;
                }

                if (captureColor)
                {
                    bool created = ColorTexture == 0;
                    if (created)
                    {
                        ColorTexture = _gl.GenTexture();
                        _gl.BindTexture(TextureTarget.Texture2D, ColorTexture);
                        SetTextureParameters(TextureMinFilter.Linear, TextureMagFilter.Linear);
                    }
                    else
                    {
                        _gl.BindTexture(TextureTarget.Texture2D, ColorTexture);
                    }

                    if (created || _colorWidth != (int)width || _colorHeight != (int)height)
                    {
                        _gl.TexImage2D(
                            TextureTarget.Texture2D,
                            0,
                            InternalFormat.Rgba8,
                            width,
                            height,
                            0,
                            PixelFormat.Rgba,
                            PixelType.UnsignedByte,
                            ReadOnlySpan<byte>.Empty);
                    }

                    if (!multisampled) _gl.CopyTexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, 0, 0, width, height);
                    _colorWidth = (int)width;
                    _colorHeight = (int)height;
                }

                if (captureDepth)
                {
                    bool created = DepthTexture == 0;
                    if (created)
                    {
                        DepthTexture = _gl.GenTexture();
                        _gl.BindTexture(TextureTarget.Texture2D, DepthTexture);
                        SetTextureParameters(TextureMinFilter.Nearest, TextureMagFilter.Nearest);
                    }
                    else
                    {
                        _gl.BindTexture(TextureTarget.Texture2D, DepthTexture);
                    }

                    if (created || _depthWidth != (int)width || _depthHeight != (int)height || _depthFormat != depthFormat)
                    {
                        _gl.TexImage2D(
                            TextureTarget.Texture2D,
                            0,
                            depthFormat,
                            width,
                            height,
                            0,
                            PixelFormat.DepthComponent,
                            PixelType.Float,
                            ReadOnlySpan<byte>.Empty);
                    }

                    if (!multisampled) _gl.CopyTexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, 0, 0, width, height);
                    _depthFormat = depthFormat;
                    _depthWidth = (int)width;
                    _depthHeight = (int)height;
                }

                // CopyTexSubImage cannot read a multisampled framebuffer. Resolve into the same sampled
                // textures used by soft particles, distortion and post effects, preserving the visible target.
                if (multisampled)
                {
                    if (_resolveFramebuffer == 0) _resolveFramebuffer = _gl.GenFramebuffer();
                    try
                    {
                        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _resolveFramebuffer);
                        _gl.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment0,
                            TextureTarget.Texture2D, captureColor ? ColorTexture : 0, 0);
                        _gl.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.DepthAttachment,
                            TextureTarget.Texture2D, captureDepth ? DepthTexture : 0, 0);
                        _gl.DrawBuffer(captureColor ? DrawBufferMode.ColorAttachment0 : DrawBufferMode.None);
                        if (_gl.CheckFramebufferStatus(FramebufferTarget.DrawFramebuffer) != GLEnum.FramebufferComplete)
                            throw new InvalidOperationException("Scene capture resolve framebuffer is incomplete.");
                        var mask = (captureColor ? ClearBufferMask.ColorBufferBit : 0) |
                                   (captureDepth ? ClearBufferMask.DepthBufferBit : 0);
                        _gl.BlitFramebuffer(0, 0, (int)width, (int)height, 0, 0, (int)width, (int)height,
                            mask, BlitFramebufferFilter.Nearest);
                    }
                    finally
                    {
                        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, (uint)drawFramebuffer);
                    }
                }

            }
            finally
            {
                _gl.BindTexture(TextureTarget.Texture2D, (uint)textureBinding);
                _gl.ActiveTexture((TextureUnit)activeTexture);
            }
            Width = (int)width;
            Height = (int)height;
        }

        public void Dispose()
        {
            if (_resolveFramebuffer != 0) _gl.DeleteFramebuffer(_resolveFramebuffer);
            _resolveFramebuffer = 0;
            if (ColorTexture != 0)
                _gl.DeleteTexture(ColorTexture);
            if (DepthTexture != 0)
                _gl.DeleteTexture(DepthTexture);

            ColorTexture = 0;
            DepthTexture = 0;
            Width = 0;
            Height = 0;
        }

        private void SetTextureParameters(TextureMinFilter minFilter, TextureMagFilter magFilter)
        {
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)minFilter);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)magFilter);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        }
    }
}
