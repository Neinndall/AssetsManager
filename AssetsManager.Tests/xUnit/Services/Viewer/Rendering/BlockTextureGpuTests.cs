using System;
using System.IO;
using AssetsManager.Services.Viewer.Rendering.Core;
using AssetsManager.Services.Viewer.Resources.Textures;
using AssetsManager.Tests.Support;
using AssetsManager.Tests.xUnit.Services.Viewer.Map;
using AssetsManager.Utils.Rendering;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering;

[Collection("Viewport native graphics")]
public sealed class BlockTextureGpuTests
{
    [Theory]
    [InlineData(10, false, false)] [InlineData(10, true, false)]
    [InlineData(11, false, false)] [InlineData(11, true, false)]
    [InlineData(12, false, false)] [InlineData(12, true, false)]
    [InlineData(10, false, true)] [InlineData(12, false, true)]
    public void BlockUploadMatchesDecodedSamplingAtEveryAuthoredMip(byte format, bool srgb, bool array)
    {
        using var context = new HiddenWglContext();
        using GL gl = GL.GetApi(context.GetProcAddress);
        using var stream = new MemoryStream(BlockTextureDataTests.Tex(format, 16, 8));
        var image = new MapTextureImage(BlockTextureData.TryReadTex(stream, 16));
        uint compressed = gl.GenTexture(), decoded = gl.GenTexture(), target = gl.GenTexture();
        uint framebuffer = gl.GenFramebuffer(), vao = gl.GenVertexArray();
        TextureTarget type = array ? TextureTarget.Texture2DArray : TextureTarget.Texture2D;
        gl.BindTexture(type, compressed);
        Assert.True(GlBlockTextureUploader.TryUpload(gl, image.Blocks, srgb, array), "GPU must expose the S3TC format under test.");
        Assert.False(image.PixelsDecoded);
        SetSampling(gl, type);
        gl.BindTexture(type, decoded);
        for (int level = 0; level < image.LevelCount; level++)
        {
            var mip = image.MipLevels[level];
            var pixels = new byte[mip.PixelWidth * mip.PixelHeight * 4];
            mip.CopyPixels(pixels, mip.PixelWidth * 4, 0);
            var storage = srgb ? InternalFormat.Srgb8Alpha8 : InternalFormat.Rgba8;
            if (array) gl.TexImage3D(type, level, storage, (uint)mip.PixelWidth, (uint)mip.PixelHeight, 1, 0,
                PixelFormat.Bgra, PixelType.UnsignedByte, pixels.AsSpan());
            else gl.TexImage2D(type, level, storage, (uint)mip.PixelWidth, (uint)mip.PixelHeight, 0,
                PixelFormat.Bgra, PixelType.UnsignedByte, pixels.AsSpan());
        }
        SetSampling(gl, type);
        gl.BindTexture(TextureTarget.Texture2D, target);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, 16, 8, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, new byte[16 * 8 * 4].AsSpan());
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, target, 0);
        Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
        uint program = GlShaderCompiler.CreateProgram(gl, false,
            "out vec2 uv; void main(){ vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2); uv=p; gl_Position=vec4(p*2.0-1.0,0,1); }",
            "in vec2 uv; out vec4 color; uniform float lod; uniform " + (array ? "sampler2DArray" : "sampler2D") +
            " source; void main(){ color=textureLod(source," + (array ? "vec3(uv,0)" : "uv") + ",lod); }");
        try
        {
            gl.UseProgram(program); gl.BindVertexArray(vao); gl.Viewport(0, 0, 16, 8);
            gl.Disable(EnableCap.Blend); gl.Disable(EnableCap.DepthTest); gl.Disable(EnableCap.FramebufferSrgb);
            gl.Uniform1(gl.GetUniformLocation(program, "source"), 0);
            byte[] Draw(uint texture, int level)
            {
                gl.ActiveTexture(TextureUnit.Texture0); gl.BindTexture(type, texture);
                gl.Uniform1(gl.GetUniformLocation(program, "lod"), (float)level);
                gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
                var result = new byte[16 * 8 * 4];
                gl.ReadPixels(0, 0, 16, 8, PixelFormat.Rgba, PixelType.UnsignedByte, result.AsSpan());
                return result;
            }
            for (int level = 0; level < image.LevelCount; level++)
            {
                byte[] expected = Draw(decoded, level), actual = Draw(compressed, level);
                Assert.Contains(expected, value => value > 10 && value < 240);
                for (int at = 0; at < expected.Length; at++)
                    Assert.InRange(Math.Abs(expected[at] - actual[at]), 0, 3);
            }
            Assert.Equal(GLEnum.NoError, gl.GetError());
        }
        finally
        {
            gl.DeleteProgram(program); gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.DeleteFramebuffer(framebuffer); gl.DeleteVertexArray(vao);
            gl.DeleteTexture(compressed); gl.DeleteTexture(decoded); gl.DeleteTexture(target);
        }
    }

    private static void SetSampling(GL gl, TextureTarget type)
    {
        gl.TexParameter(type, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.NearestMipmapNearest);
        gl.TexParameter(type, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
    }
}
