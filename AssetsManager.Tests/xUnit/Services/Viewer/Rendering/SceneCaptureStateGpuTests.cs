using System;
using System.Collections.Generic;
using AssetsManager.Services.Viewer.Rendering.Core;
using AssetsManager.Tests.Support;
using Silk.NET.OpenGL;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering;

[Collection("Viewport native graphics")]
public sealed class SceneCaptureStateGpuTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (bool prepass in new[] { false, true })
        foreach (bool multisampled in new[] { false, true })
        foreach (bool floatingDepth in new[] { false, true })
        foreach (bool scissor in new[] { false, true })
        foreach (int channels in new[] { 1, 2, 3 })
            yield return new object[] { prepass, multisampled, floatingDepth, scissor, channels };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void SceneCopiesKeepEveryPixelAndRestoreCallerClipping(bool prepass, bool multisampled, bool floatingDepth, bool scissor, int channels)
    {
        using var context = new HiddenWglContext();
        using GL gl = GL.GetApi(context.GetProcAddress);
        uint source = gl.GenFramebuffer();
        uint read = gl.GenFramebuffer();
        uint color = gl.GenRenderbuffer();
        uint depth = gl.GenRenderbuffer();
        using var capture = new GlSceneCapture(gl);
        using var scene = new GlSceneDepthPass(gl);
        try
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, source);
            void Attach(uint buffer, InternalFormat format, FramebufferAttachment attachment)
            {
                gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, buffer);
                if (multisampled) gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, 4, format, 16, 16);
                else gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, format, 16, 16);
                gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, attachment, RenderbufferTarget.Renderbuffer, buffer);
            }
            Attach(color, InternalFormat.Rgba8, FramebufferAttachment.ColorAttachment0);
            Attach(depth, floatingDepth ? InternalFormat.Depth32fStencil8 : InternalFormat.Depth24Stencil8,
                FramebufferAttachment.DepthStencilAttachment);
            Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
            foreach (uint size in new uint[] { 8, 16, 8 })
            {
                bool copyColor = (channels & 1) != 0, copyDepth = (channels & 2) != 0;
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, source);
                gl.Disable(EnableCap.ScissorTest);
                gl.ClearColor(0, 0, 1, 1);
                gl.ClearDepth(.8);
                gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                void Take(bool seed = false)
                {
                    bool clipping = gl.IsEnabled(EnableCap.ScissorTest);
                    if (prepass) scene.Render(size, size,
                        () => Assert.Equal(clipping, gl.IsEnabled(EnableCap.ScissorTest)),
                        () =>
                        {
                            Assert.Equal(clipping, gl.IsEnabled(EnableCap.ScissorTest));
                            capture.Capture(size, size, seed || copyColor, seed || copyDepth);
                        });
                    else capture.Capture(size, size, seed || copyColor, seed || copyDepth);
                }
                // Seed both copies so incomplete updates leave a deterministic previous-frame colour and depth.
                Take(seed: true);
                gl.ClearColor(.6f, .2f, .4f, 1);
                gl.ClearDepth(.3);
                gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                gl.Scissor(3, 4, 2, 3);
                if (scissor) gl.Enable(EnableCap.ScissorTest);
                Take();
                Assert.Equal(scissor, gl.IsEnabled(EnableCap.ScissorTest));
                int[] box = new int[4];
                gl.GetInteger(GLEnum.ScissorBox, box);
                Assert.Equal(new[] { 3, 4, 2, 3 }, box);
                Assert.Equal((int)source, gl.GetInteger(GLEnum.ReadFramebufferBinding));
                Assert.Equal((int)source, gl.GetInteger(GLEnum.DrawFramebufferBinding));
                Assert.Equal(GLEnum.NoError, gl.GetError());

                gl.BindFramebuffer(FramebufferTarget.Framebuffer, read);
                gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                    TextureTarget.Texture2D, capture.ColorTexture, 0);
                gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
                    TextureTarget.Texture2D, capture.DepthTexture, 0);
                Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
                float[] rgba = new float[size * size * 4], z = new float[size * size];
                gl.ReadPixels(0, 0, size, size, PixelFormat.Rgba, PixelType.Float, rgba.AsSpan());
                gl.ReadPixels(0, 0, size, size, PixelFormat.DepthComponent, PixelType.Float, z.AsSpan());
                for (int pixel = 0; pixel < z.Length; pixel++)
                {
                    Assert.InRange(rgba[pixel * 4], (copyColor ? .6f : 0f) - .005f, (copyColor ? .6f : 0f) + .005f);
                    Assert.InRange(rgba[pixel * 4 + 1], (copyColor ? .2f : 0f) - .005f, (copyColor ? .2f : 0f) + .005f);
                    Assert.InRange(rgba[pixel * 4 + 2], (copyColor ? .4f : 1f) - .005f, (copyColor ? .4f : 1f) + .005f);
                    Assert.InRange(z[pixel], (copyDepth ? .3f : .8f) - .001f, (copyDepth ? .3f : .8f) + .001f);
                }
                Assert.Equal(GLEnum.NoError, gl.GetError());
            }
        }
        finally
        {
            gl.Disable(EnableCap.ScissorTest);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0);
            gl.DeleteFramebuffer(read);
            gl.DeleteFramebuffer(source);
            gl.DeleteRenderbuffer(color);
            gl.DeleteRenderbuffer(depth);
        }
    }
}
