using System;
using System.Numerics;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Tests.Support;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering;

[Collection("Viewport native graphics")]
public sealed class MapPostEffectsRendererGpuTests
{
    [Theory]
    [InlineData(1)] // Inherited mipmapped + repeat sampler on units 0..2
    [InlineData(2)] // Inherited ScissorTest
    [InlineData(3)] // Inherited StencilTest
    [InlineData(4)] // Inherited FramebufferSrgb
    public void PostEffectsIsolateInheritedSamplersClippingStencilAndSrgb(int inheritedState)
    {
        using var context = new HiddenWglContext();
        using GL gl = GL.GetApi(context.GetProcAddress);
        using var renderer = new MapPostEffectsRenderer();
        renderer.Initialize(gl);

        const uint size = 32;
        uint framebuffer = gl.GenFramebuffer();
        uint color = gl.GenTexture();
        uint depthStencil = gl.GenRenderbuffer();
        uint sampler = gl.GenSampler();
        try
        {
            gl.SamplerParameter(sampler, SamplerParameterI.MinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            gl.SamplerParameter(sampler, SamplerParameterI.MagFilter, (int)TextureMagFilter.Nearest);
            gl.SamplerParameter(sampler, SamplerParameterI.WrapS, (int)TextureWrapMode.Repeat);
            gl.SamplerParameter(sampler, SamplerParameterI.WrapT, (int)TextureWrapMode.Repeat);

            gl.BindTexture(TextureTarget.Texture2D, color);
            gl.TexImage2D<byte>(TextureTarget.Texture2D, 0, InternalFormat.Srgb8Alpha8, size, size, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, ReadOnlySpan<byte>.Empty);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);

            gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, color, 0);
            gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depthStencil);
            gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.Depth24Stencil8, size, size);
            gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment,
                RenderbufferTarget.Renderbuffer, depthStencil);
            Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));

            var effects = new MapPostEffectsData(
                DepthFog: new MapFogData(true, new Vector4(0.1f, 0.5f, 0.9f, 1f), 0f, 4f, 0.8f),
                HeightFog: new MapFogData(false, Vector4.Zero, 300f, -100f, 0f),
                DepthOfField: new MapDepthOfFieldData(false, 2000f, 800f, 10f));
            var ssao = new MapSsaoData(0, 25f, 3f, 1f, 0.5f, 0.5f, true);
            Matrix4x4 view = Matrix4x4.Identity;
            Matrix4x4 proj = Matrix4x4.CreatePerspectiveFieldOfView(1f, 1f, 1f, 200f);

            byte[] Draw(int state)
            {
                gl.Disable(EnableCap.ScissorTest);
                gl.Disable(EnableCap.StencilTest);
                gl.Disable(EnableCap.FramebufferSrgb);
                for (uint unit = 0; unit < 3; unit++)
                    gl.BindSampler(unit, 0);

                gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
                gl.Viewport(0, 0, size, size);
                gl.ColorMask(true, true, true, true);
                gl.DepthMask(true);
                gl.StencilMask(0xFF);
                gl.ClearColor(0.8f, 0.2f, 0.1f, 1f);
                gl.ClearDepth(0.5);
                gl.ClearStencil(0);
                gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit);

                renderer.CaptureSceneDepth(effects, ssao, size, size);

                if (state == 1)
                {
                    for (uint unit = 0; unit < 3; unit++)
                        gl.BindSampler(unit, sampler);
                }
                else if (state == 2)
                {
                    gl.Scissor(0, 0, 2, 2);
                    gl.Enable(EnableCap.ScissorTest);
                }
                else if (state == 3)
                {
                    gl.Enable(EnableCap.StencilTest);
                    gl.StencilFunc(StencilFunction.Equal, 99, 0xFF);
                }
                else if (state == 4)
                {
                    gl.Enable(EnableCap.FramebufferSrgb);
                }

                renderer.Render(effects, ssao, view, proj, size, size);

                Assert.Equal(state == 2, gl.IsEnabled(EnableCap.ScissorTest));
                Assert.Equal(state == 3, gl.IsEnabled(EnableCap.StencilTest));
                Assert.Equal(state == 4, gl.IsEnabled(EnableCap.FramebufferSrgb));
                for (uint unit = 0; unit < 3; unit++)
                {
                    gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + unit));
                    Assert.Equal(state == 1 ? (int)sampler : 0, gl.GetInteger(GLEnum.SamplerBinding));
                }

                gl.Disable(EnableCap.ScissorTest);
                gl.Disable(EnableCap.StencilTest);
                gl.Disable(EnableCap.FramebufferSrgb);
                var pixels = new byte[size * size * 4];
                gl.ReadPixels<byte>(0, 0, size, size, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
                Assert.Equal(GLEnum.NoError, gl.GetError());
                return pixels;
            }

            byte[] expected = Draw(0);
            // Sanity check: fog shifted the red clear color noticeably toward blue.
            Assert.True(expected[(16 * size + 16) * 4 + 2] > 60,
                $"Expected visible depth fog contribution in baseline, got B={expected[(16 * size + 16) * 4 + 2]}.");

            byte[] actual = Draw(inheritedState);
            for (int at = 0; at < actual.Length; at++)
            {
                Assert.True(Math.Abs(actual[at] - expected[at]) <= 1,
                    $"inheritedState={inheritedState}, byte={at}: expected {expected[at]}, got {actual[at]}.");
            }
        }
        finally
        {
            gl.Disable(EnableCap.ScissorTest);
            gl.Disable(EnableCap.StencilTest);
            gl.Disable(EnableCap.FramebufferSrgb);
            for (uint unit = 0; unit < 3; unit++)
                gl.BindSampler(unit, 0);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.DeleteSampler(sampler);
            gl.DeleteFramebuffer(framebuffer);
            gl.DeleteTexture(color);
            gl.DeleteRenderbuffer(depthStencil);
        }
    }
}
