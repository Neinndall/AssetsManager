using System;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Rendering.Core;

/// <summary>Renders scene depth on a copy of the backdrop without changing the visible framebuffer.</summary>
internal sealed class GlSceneDepthPass(GL gl) : IDisposable
{
    private uint _framebuffer;
    private uint _color;
    private uint _depth;
    private uint _width;
    private uint _height;
    private int _depthBits;

    internal void Render(uint width, uint height, Action drawScene, Action captureDepth)
    {
        int read = gl.GetInteger(GLEnum.ReadFramebufferBinding);
        int draw = gl.GetInteger(GLEnum.DrawFramebufferBinding);
        int renderbuffer = gl.GetInteger(GLEnum.RenderbufferBinding);
        const GLEnum AttachmentDepthSize = (GLEnum)0x8216;
        GLEnum attachment = draw == 0 ? (GLEnum)0x1801 : GLEnum.DepthAttachment;
        gl.GetFramebufferAttachmentParameter(GLEnum.DrawFramebuffer, attachment, AttachmentDepthSize, out int depthBits);
        try
        {
            Ensure(width, height, depthBits);
            gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, (uint)draw);
            gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _framebuffer);
            gl.BlitFramebuffer(0, 0, (int)width, (int)height, 0, 0, (int)width, (int)height,
                ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit,
                BlitFramebufferFilter.Nearest);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
            drawScene();
            captureDepth();
        }
        finally
        {
            gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, (uint)read);
            gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, (uint)draw);
            gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, (uint)renderbuffer);
        }
    }

    private void Ensure(uint width, uint height, int depthBits)
    {
        if (_framebuffer != 0 && width == _width && height == _height && depthBits == _depthBits) return;
        Dispose();

        _framebuffer = gl.GenFramebuffer();
        _color = gl.GenRenderbuffer();
        _depth = gl.GenRenderbuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _color);
        gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.Rgba16f, width, height);
        gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            RenderbufferTarget.Renderbuffer, _color);
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depth);
        gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer,
            depthBits == 32 ? InternalFormat.Depth32fStencil8 : InternalFormat.Depth24Stencil8, width, height);
        gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment,
            RenderbufferTarget.Renderbuffer, _depth);
        if (gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete)
            throw new InvalidOperationException("Scene depth framebuffer is incomplete.");

        _width = width;
        _height = height;
        _depthBits = depthBits;
    }

    public void Dispose()
    {
        if (_framebuffer != 0) gl.DeleteFramebuffer(_framebuffer);
        if (_color != 0) gl.DeleteRenderbuffer(_color);
        if (_depth != 0) gl.DeleteRenderbuffer(_depth);
        _framebuffer = _color = _depth = 0;
        _width = _height = 0;
    }
}
