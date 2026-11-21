using System;
using AssetsManager.Services.Viewer.Rendering.GameShaders;
using AssetsManager.Tests.Support;
using AssetsManager.Utils.Rendering;
using Silk.NET.OpenGL;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering;

[Collection("Viewport native graphics")]
public sealed class GameShaderImageLightGpuTests
{
    [Fact]
    public void PreviewSkyArraySamplesNonZeroRadianceThroughMipmappedSampler()
    {
        using var context = new HiddenWglContext();
        using GL gl = GL.GetApi(context.GetProcAddress);
        using var imageLight = new GameShaderImageLight(gl);
        uint program = GlShaderCompiler.CreateProgram(gl, false,
            "void main(){ vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2); gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0); }",
            "uniform sampler2DArray uSky;\nout vec4 color;\nvoid main(){ color = 0.5 * (textureLod(uSky, vec3(0.5, 0.5, 2.0), 0.0) + textureLod(uSky, vec3(0.5, 0.5, 2.0), 2.0)); }");
        uint framebuffer = gl.GenFramebuffer(), target = gl.GenTexture(), vao = gl.GenVertexArray(), sampler = gl.GenSampler();
        try
        {
            uint skyTexture = imageLight.Resolve(null, null);
            Assert.NotEqual(0u, skyTexture);

            gl.SamplerParameter(sampler, SamplerParameterI.MinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            gl.SamplerParameter(sampler, SamplerParameterI.MagFilter, (int)TextureMagFilter.Linear);
            gl.SamplerParameter(sampler, SamplerParameterI.WrapS, (int)TextureWrapMode.ClampToEdge);
            gl.SamplerParameter(sampler, SamplerParameterI.WrapT, (int)TextureWrapMode.ClampToEdge);

            gl.BindTexture(TextureTarget.Texture2D, target);
            gl.TexImage2D<float>(TextureTarget.Texture2D, 0, InternalFormat.Rgba32f, 4, 4, 0,
                PixelFormat.Rgba, PixelType.Float, ReadOnlySpan<float>.Empty);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, target, 0);
            Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));

            gl.Viewport(0, 0, 4, 4);
            gl.Disable(EnableCap.DepthTest);
            gl.Disable(EnableCap.CullFace);
            gl.Disable(EnableCap.Blend);
            gl.ClearColor(0f, 0f, 0f, 0f);
            gl.Clear(ClearBufferMask.ColorBufferBit);

            gl.UseProgram(program);
            gl.ActiveTexture(TextureUnit.Texture0);
            gl.BindTexture(TextureTarget.Texture2DArray, skyTexture);
            gl.BindSampler(0, sampler);
            gl.Uniform1(gl.GetUniformLocation(program, "uSky"), 0);
            gl.BindVertexArray(vao);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

            var pixel = new float[4];
            gl.ReadPixels<float>(2, 2, 1, 1, PixelFormat.Rgba, PixelType.Float, pixel);
            Assert.Equal(GLEnum.NoError, gl.GetError());
            Assert.True(pixel[0] > 0.05f && pixel[1] > 0.05f && pixel[2] > 0.05f,
                $"Expected non-zero fallback sky radiance through mipmapped sampler, got RGBA=({pixel[0]}, {pixel[1]}, {pixel[2]}, {pixel[3]}).");
        }
        finally
        {
            gl.ActiveTexture(TextureUnit.Texture0);
            gl.BindSampler(0, 0);
            gl.BindTexture(TextureTarget.Texture2DArray, 0);
            gl.UseProgram(0);
            gl.BindVertexArray(0);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.DeleteSampler(sampler);
            gl.DeleteVertexArray(vao);
            gl.DeleteFramebuffer(framebuffer);
            gl.DeleteTexture(target);
            gl.DeleteProgram(program);
        }
    }
}
