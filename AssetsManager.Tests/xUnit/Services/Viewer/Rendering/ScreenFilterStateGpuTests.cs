using System;
using System.Collections.Generic;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Tests.Support;
using AssetsManager.Utils.Rendering;
using Silk.NET.OpenGL;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering;

[Collection("Viewport native graphics")]
public sealed class ScreenFilterStateGpuTests
{
    public static IEnumerable<object[]> FilterCases()
    {
        foreach (bool smaa in new[] { false, true })
        foreach (bool capture in new[] { false, true })
        for (int inherited = 0; inherited < 6; inherited++)
            yield return new object[] { smaa, capture, inherited };
    }

    [Theory]
    [MemberData(nameof(FilterCases))]
    public void ScreenFilterOwnsSamplingAndClippingAndRestoresInheritedState(bool smaa, bool capture, int inherited)
    {
        using var context = new HiddenWglContext();
        using GL gl = GL.GetApi(context.GetProcAddress);
        using var fxaa = new FxaaPostEffectsRenderer();
        using var morphological = new SmaaPostEffectsRenderer();
        if (smaa) morphological.Initialize(gl); else fxaa.Initialize(gl);
        uint input = gl.GenTexture(), output = gl.GenTexture(), depth = gl.GenRenderbuffer();
        uint read = gl.GenFramebuffer(), target = gl.GenFramebuffer(), sampler = gl.GenSampler();
        uint callerVao = gl.GenVertexArray();
        uint callerProgram = GlShaderCompiler.CreateProgram(gl, false,
            "void main(){gl_Position=vec4(0.0);}", "out vec4 color; void main(){color=vec4(1.0);}");
        try
        {
            gl.SamplerParameter(sampler, SamplerParameterI.MinFilter,
                (int)(inherited == 2 ? TextureMinFilter.LinearMipmapLinear : TextureMinFilter.Nearest));
            gl.SamplerParameter(sampler, SamplerParameterI.MagFilter, (int)TextureMagFilter.Nearest);
            gl.SamplerParameter(sampler, SamplerParameterI.WrapS, (int)TextureWrapMode.Repeat);
            gl.SamplerParameter(sampler, SamplerParameterI.WrapT, (int)TextureWrapMode.Repeat);
            foreach (var size in new[] { (40, 32), (31, 25), (40, 32) })
            {
                int width = size.Item1, height = size.Item2;
                byte[] pattern = new byte[width * height * 4];
                for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int at = (y * width + x) * 4;
                    bool bright = x + y / 2 > width / 2;
                    pattern[at] = bright ? (byte)230 : (byte)15;
                    pattern[at + 1] = bright ? (byte)220 : (byte)25;
                    pattern[at + 2] = bright ? (byte)210 : (byte)35;
                    pattern[at + 3] = (byte)(64 + x * 128 / width);
                }
                Texture(input, InternalFormat.Rgba8, pattern);
                Texture(output, InternalFormat.Srgb8Alpha8, new byte[pattern.Length]);
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, read);
                gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                    TextureTarget.Texture2D, input, 0);
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, target);
                gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                    TextureTarget.Texture2D, output, 0);
                gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depth);
                gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.Depth24Stencil8, (uint)width, (uint)height);
                gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment,
                    RenderbufferTarget.Renderbuffer, depth);
                Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
                byte[] expected = Draw(0);
                byte[] actual = Draw(inherited);
                for (int at = 0; at < actual.Length; at++)
                    Assert.True(Math.Abs(actual[at] - expected[at]) <= 1,
                        $"smaa={smaa}, capture={capture}, inherited={inherited}, size={width}x{height}, byte={at}: expected {expected[at]}, got {actual[at]}.");
                Assert.Equal(GLEnum.NoError, gl.GetError());

                void Texture(uint texture, InternalFormat format, byte[] pixels)
                {
                    gl.ActiveTexture(TextureUnit.Texture0); gl.BindSampler(0, 0);
                    gl.BindTexture(TextureTarget.Texture2D, texture);
                    gl.TexImage2D(TextureTarget.Texture2D, 0, format, (uint)width, (uint)height, 0,
                        PixelFormat.Rgba, PixelType.UnsignedByte, pixels.AsSpan());
                    gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
                    gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                    gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
                    gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
                }
                byte[] Draw(int state)
                {
                    gl.Disable(EnableCap.FramebufferSrgb); gl.Disable(EnableCap.StencilTest); gl.Disable(EnableCap.ScissorTest);
                    gl.BindFramebuffer(FramebufferTarget.Framebuffer, target);
                    gl.Viewport(0, 0, (uint)width, (uint)height);
                    gl.ColorMask(true, true, true, true); gl.DepthMask(true); gl.StencilMask(255);
                    gl.ClearColor(1, 0, 1, 1); gl.ClearStencil(5); gl.ClearDepth(1);
                    gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit);
                    if (capture)
                    {
                        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, read);
                        gl.BlitFramebuffer(0, 0, width, height, 0, 0, width, height,
                            ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
                        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, target);
                    }
                    gl.ActiveTexture(TextureUnit.Texture0);
                    uint inheritedSampler = state is 1 or 2 ? sampler : 0;
                    gl.BindSampler(0, inheritedSampler);
                    gl.StencilFunc(StencilFunction.Never, 7, 255);
                    gl.Scissor(0, 0, 3, 3);
                    if (state == 3) gl.Enable(EnableCap.StencilTest);
                    if (state == 4) gl.Enable(EnableCap.ScissorTest);
                    if (state == 5) gl.Enable(EnableCap.FramebufferSrgb);
                    gl.Enable(EnableCap.DepthTest); gl.DepthFunc(DepthFunction.Greater); gl.DepthMask(false);
                    gl.Enable(EnableCap.Blend); gl.Enable(EnableCap.CullFace);
                    gl.ColorMask(false, true, false, false);
                    gl.Viewport(2, 3, (uint)(width - 4), (uint)(height - 6));
                    gl.UseProgram(callerProgram); gl.BindVertexArray(callerVao);
                    gl.BindTexture(TextureTarget.Texture2D, output);
                    gl.ActiveTexture(TextureUnit.Texture3);
                    if (smaa)
                    {
                        if (capture) morphological.Render(width, height); else morphological.RenderTexture(input, width, height);
                    }
                    else
                    {
                        if (capture) fxaa.Render(width, height); else fxaa.RenderTexture(input, width, height);
                    }
                    Assert.Equal(state == 3, gl.IsEnabled(EnableCap.StencilTest));
                    Assert.Equal(state == 4, gl.IsEnabled(EnableCap.ScissorTest));
                    Assert.Equal(state == 5, gl.IsEnabled(EnableCap.FramebufferSrgb));
                    Assert.True(gl.IsEnabled(EnableCap.DepthTest));
                    Assert.True(gl.IsEnabled(EnableCap.Blend));
                    Assert.True(gl.IsEnabled(EnableCap.CullFace));
                    Assert.Equal(0, gl.GetInteger(GLEnum.DepthWritemask));
                    Assert.Equal((int)DepthFunction.Greater, gl.GetInteger(GLEnum.DepthFunc));
                    Assert.Equal((int)callerProgram, gl.GetInteger(GLEnum.CurrentProgram));
                    Assert.Equal((int)callerVao, gl.GetInteger(GLEnum.VertexArrayBinding));
                    Assert.Equal((int)TextureUnit.Texture3, gl.GetInteger(GLEnum.ActiveTexture));
                    int[] viewport = new int[4], mask = new int[4];
                    gl.GetInteger(GLEnum.Viewport, viewport); gl.GetInteger(GLEnum.ColorWritemask, mask);
                    Assert.Equal(new[] { 2, 3, width - 4, height - 6 }, viewport);
                    Assert.Equal(new[] { 0, 1, 0, 0 }, mask);
                    gl.ActiveTexture(TextureUnit.Texture0);
                    Assert.Equal((int)inheritedSampler, gl.GetInteger(GLEnum.SamplerBinding));
                    Assert.Equal((int)output, gl.GetInteger(GLEnum.TextureBinding2D));
                    Assert.Equal((int)target, gl.GetInteger(GLEnum.DrawFramebufferBinding));
                    byte[] pixels = new byte[pattern.Length];
                    gl.ReadPixels(0, 0, (uint)width, (uint)height, PixelFormat.Rgba, PixelType.UnsignedByte, pixels.AsSpan());
                    return pixels;
                }
            }
        }
        finally
        {
            gl.ActiveTexture(TextureUnit.Texture0); gl.BindSampler(0, 0);
            gl.UseProgram(0); gl.BindVertexArray(0);
            gl.Disable(EnableCap.FramebufferSrgb); gl.Disable(EnableCap.StencilTest); gl.Disable(EnableCap.ScissorTest);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.DeleteFramebuffer(read); gl.DeleteFramebuffer(target); gl.DeleteTexture(input); gl.DeleteTexture(output);
            gl.DeleteRenderbuffer(depth); gl.DeleteSampler(sampler);
            gl.DeleteProgram(callerProgram); gl.DeleteVertexArray(callerVao);
        }
    }
}
