using System;
using AssetsManager.Services.Viewer.Rendering.Core;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Tests.Support;
using Silk.NET.OpenGL;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering;

public sealed class MultisampleCaptureGpuTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SceneColorAndDepthResolveBeforeVfxSamplingAndPreserveFramebufferBindings(bool floatingDepth)
    {
        using var context = new HiddenWglContext();
        using var gl = GL.GetApi(context.GetProcAddress);
        uint frame = gl.GenFramebuffer(); gl.BindFramebuffer(FramebufferTarget.Framebuffer,frame);
        uint color = gl.GenRenderbuffer(); gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer,color);
        gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer,4,InternalFormat.Rgba8,16,16);
        gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer,FramebufferAttachment.ColorAttachment0,RenderbufferTarget.Renderbuffer,color);
        uint depth = gl.GenRenderbuffer(); gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer,depth);
        gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer,4,
            floatingDepth ? InternalFormat.Depth32fStencil8 : InternalFormat.Depth24Stencil8,16,16);
        gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer,FramebufferAttachment.DepthStencilAttachment,RenderbufferTarget.Renderbuffer,depth);
        Assert.Equal(GLEnum.FramebufferComplete,gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
        gl.ClearColor(.25f,.5f,.75f,1); gl.ClearDepth(.4); gl.ClearStencil(7);
        gl.Clear(ClearBufferMask.ColorBufferBit|ClearBufferMask.DepthBufferBit|ClearBufferMask.StencilBufferBit);
        using var capture = new GlSceneCapture(gl);
        capture.Capture(16,16,true,true);
        Assert.Equal(GLEnum.NoError,gl.GetError());
        capture.Capture(16,16,true,false);
        capture.Capture(16,16,false,true);
        Assert.Equal(GLEnum.NoError,gl.GetError());
        Assert.Equal((int)frame,gl.GetInteger(GLEnum.ReadFramebufferBinding));
        Assert.Equal((int)frame,gl.GetInteger(GLEnum.DrawFramebufferBinding));
        using (var scene = new GlSceneDepthPass(gl))
            scene.Render(16,16,() => Assert.Equal(GLEnum.NoError,gl.GetError()),() => {});
        Assert.Equal(GLEnum.NoError,gl.GetError());
        uint read = gl.GenFramebuffer(); gl.BindFramebuffer(FramebufferTarget.Framebuffer,read);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.ColorAttachment0,TextureTarget.Texture2D,capture.ColorTexture,0);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.DepthAttachment,TextureTarget.Texture2D,capture.DepthTexture,0);
        Assert.Equal(GLEnum.FramebufferComplete,gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
        float[] rgb = new float[4], z = new float[1];
        gl.ReadPixels(0,0,1,1,PixelFormat.Rgba,PixelType.Float,rgb.AsSpan());
        gl.ReadPixels(0,0,1,1,PixelFormat.DepthComponent,PixelType.Float,z.AsSpan());
        Assert.InRange(rgb[0],.249f,.253f); Assert.InRange(rgb[2],.747f,.753f);
        Assert.InRange(z[0],.399f,.401f);
        Assert.Equal(GLEnum.NoError,gl.GetError());
        gl.BindFramebuffer(FramebufferTarget.Framebuffer,frame);
        using (var fxaa = new FxaaPostEffectsRenderer())
        {
            fxaa.Initialize(gl); fxaa.Render(16,16);
            Assert.Equal(GLEnum.NoError,gl.GetError());
            Assert.Equal((int)frame,gl.GetInteger(GLEnum.DrawFramebufferBinding));
        }
        using (var smaa = new SmaaPostEffectsRenderer())
        {
            smaa.Initialize(gl); smaa.Render(16,16);
            Assert.Equal(GLEnum.NoError,gl.GetError());
            Assert.Equal((int)frame,gl.GetInteger(GLEnum.DrawFramebufferBinding));
        }
        gl.DeleteFramebuffer(read); gl.DeleteFramebuffer(frame); gl.DeleteRenderbuffer(color); gl.DeleteRenderbuffer(depth);
    }
}
