using System;
using AssetsManager.Services.Viewer.Rendering.GameShaders;
using AssetsManager.Tests.Support;
using AssetsManager.Utils.Rendering;
using Silk.NET.OpenGL;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering;

[Collection("Viewport native graphics")]
public sealed class GameShaderBloomGpuTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public void BloomUsesItsOwnFilteringAndPreservesTheCallerSampler(int samplerMode, bool localized)
    {
        using var context = new HiddenWglContext();
        using GL gl = GL.GetApi(context.GetProcAddress);
        using var bloom = new GameShaderBloom();
        bloom.Initialize(gl);
        uint program = GlShaderCompiler.CreateProgram(gl, false,
            "void main(){ vec2 p=vec2((gl_VertexID << 1) & 2, gl_VertexID & 2); gl_Position=vec4(p*2.0-1.0,0.0,1.0); }",
            "uniform int localized;\nlayout(location=0) out vec4 color;\nlayout(location=1) out vec4 glow;\n" +
            "void main(){ float mask = localized == 0 ? 1.0 : float(gl_FragCoord.x < 5.0 && gl_FragCoord.y < 7.0); " +
            "color=vec4(0.0); glow=vec4(vec3(0.25,0.5,0.75)*mask,1.0); }");
        uint framebuffer = gl.GenFramebuffer(), color = gl.GenTexture(), depth = gl.GenRenderbuffer();
        uint vao = gl.GenVertexArray(), sampler = gl.GenSampler();
        try
        {
            gl.SamplerParameter(sampler, SamplerParameterI.MinFilter,
                (int)(samplerMode == 2 ? TextureMinFilter.LinearMipmapLinear : TextureMinFilter.Nearest));
            gl.SamplerParameter(sampler, SamplerParameterI.MagFilter, (int)TextureMagFilter.Nearest);
            gl.SamplerParameter(sampler, SamplerParameterI.WrapS, (int)TextureWrapMode.Repeat);
            gl.SamplerParameter(sampler, SamplerParameterI.WrapT, (int)TextureWrapMode.Repeat);
            foreach (var size in new[] { (64u, 48u), (47u, 35u), (64u, 48u) })
            {
                uint width = size.Item1, height = size.Item2;
                gl.ActiveTexture(TextureUnit.Texture0);
                gl.BindTexture(TextureTarget.Texture2D, color);
                gl.TexImage2D<float>(TextureTarget.Texture2D, 0, InternalFormat.Rgba32f, width, height, 0,
                    PixelFormat.Rgba, PixelType.Float, ReadOnlySpan<float>.Empty);
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
                gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                    TextureTarget.Texture2D, color, 0);
                gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depth);
                gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24, width, height);
                gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
                    RenderbufferTarget.Renderbuffer, depth);
                Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));

                float[] Draw(uint inheritedSampler)
                {
                    gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
                    gl.Viewport(0, 0, width, height);
                    gl.ColorMask(true, true, true, true);
                    gl.DepthMask(true);
                    gl.ClearDepth(1);
                    gl.ClearColor(.125f, .0625f, .03125f, .375f);
                    gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                    Assert.True(bloom.BeginPasses());
                    gl.Disable(EnableCap.DepthTest);
                    gl.Disable(EnableCap.CullFace);
                    gl.Disable(EnableCap.Blend);
                    gl.UseProgram(program);
                    gl.Uniform1(gl.GetUniformLocation(program, "localized"), localized ? 1 : 0);
                    gl.BindVertexArray(vao);
                    gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
                    bloom.EndPasses();
                    // A material drawn before composition owns the sampler on unit zero.
                    gl.ActiveTexture(TextureUnit.Texture0);
                    gl.BindSampler(0, inheritedSampler);
                    bloom.Compose();
                    Assert.Equal((int)inheritedSampler, gl.GetInteger(GLEnum.SamplerBinding));
                    Assert.Equal((int)framebuffer, gl.GetInteger(GLEnum.DrawFramebufferBinding));
                    var pixels = new float[width * height * 4];
                    gl.ReadPixels<float>(0, 0, width, height, PixelFormat.Rgba, PixelType.Float, pixels);
                    Assert.Equal(GLEnum.NoError, gl.GetError());
                    return pixels;
                }

                float[] expected = Draw(0);
                float[] actual = Draw(samplerMode == 0 ? 0 : sampler);
                float totalGlow = 0;
                for (int at = 0; at < actual.Length; at += 4)
                {
                    for (int channel = 0; channel < 4; channel++)
                        Assert.InRange(MathF.Abs(actual[at + channel] - expected[at + channel]), 0, .0001f);
                    Assert.InRange(MathF.Abs(actual[at + 3] - .375f), 0, .00001f);
                    totalGlow += expected[at] - .125f;
                    if (!localized)
                    {
                        Assert.InRange(expected[at], .37f, .38f);
                        Assert.InRange(expected[at + 1], .55f, .57f);
                        Assert.InRange(expected[at + 2], .77f, .79f);
                    }
                }
                Assert.True(totalGlow > .1f, "The baseline must contain visible bloom.");
            }
        }
        finally
        {
            gl.BindSampler(0, 0);
            gl.UseProgram(0);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.DeleteSampler(sampler);
            gl.DeleteVertexArray(vao);
            gl.DeleteFramebuffer(framebuffer);
            gl.DeleteTexture(color);
            gl.DeleteRenderbuffer(depth);
            gl.DeleteProgram(program);
        }
    }
}
