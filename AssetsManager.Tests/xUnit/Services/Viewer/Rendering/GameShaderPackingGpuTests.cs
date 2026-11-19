using System;
using System.Numerics;
using System.Reflection;
using AssetsManager.Services.Viewer.Rendering.GameShaders;
using AssetsManager.Tests.Support;
using AssetsManager.Utils.Rendering;
using Silk.NET.OpenGL;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering;

[Collection("Viewport native graphics")]
public sealed class GameShaderPackingGpuTests
{
    [Theory]
    [InlineData(0xc0003c00u)]
    [InlineData(0u)]
    [InlineData(0x80000000u)]
    [InlineData(0x7bff0400u)]
    [InlineData(0x84000400u)]
    [InlineData(0x80010001u)]
    [InlineData(0x83ff03ffu)]
    public void DesktopProfilePreservesHalfValuesAcrossStages(uint packed)
    {
        using var context = new HiddenWglContext();
        using GL gl = GL.GetApi(context.GetProcAddress);
        using var runtime = new GameShaderRuntime(gl, false, null);
        string header = "#version 300 es\nprecision highp float;\nprecision highp int;\n";
        string vertex = header + "flat out vec2 halfValue; void main(){ " +
            "vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2); " +
            "gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0); " +
            $"halfValue = unpackHalf2x16({packed}u); }}";
        string pixel = header + "flat in vec2 halfValue; out vec4 result; " +
            "void main(){ result = vec4(halfValue, unpackHalf2x16(packHalf2x16(halfValue))); }";
        MethodInfo profile = typeof(GameShaderRuntime).GetMethod("SourceForProfile", BindingFlags.Instance | BindingFlags.NonPublic)!;
        string Desktop(string source, bool vertexStage) => (string)profile.Invoke(runtime, new object[] { source, vertexStage, false })!;
        uint program = GlShaderCompiler.CreateRawProgram(gl, Desktop(vertex, true), Desktop(pixel, false));
        uint texture = gl.GenTexture(), framebuffer = gl.GenFramebuffer(), vao = gl.GenVertexArray();
        try
        {
            gl.BindTexture(TextureTarget.Texture2D, texture);
            gl.TexImage2D<float>(TextureTarget.Texture2D, 0, InternalFormat.Rgba32f, 1, 1, 0,
                PixelFormat.Rgba, PixelType.Float, ReadOnlySpan<float>.Empty);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, texture, 0);
            Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
            gl.BindVertexArray(vao); gl.UseProgram(program); gl.Viewport(0, 0, 1, 1);
            gl.Disable(EnableCap.DepthTest); gl.Disable(EnableCap.Blend);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
            Span<float> actual = stackalloc float[4];
            gl.ReadPixels(0, 0, 1, 1, PixelFormat.Rgba, PixelType.Float, actual);
            float low = (float)BitConverter.UInt16BitsToHalf((ushort)packed);
            float high = (float)BitConverter.UInt16BitsToHalf((ushort)(packed >> 16));
            AssertExact(low, actual[0]);
            AssertExact(high, actual[1]);
            AssertExact(low, actual[2]);
            AssertExact(high, actual[3]);
            Assert.Equal(GLEnum.NoError, gl.GetError());

            static void AssertExact(float expected, float observed)
            {
                if (expected == 0f)
                    Assert.Equal(0f, observed);
                else
                    Assert.Equal(BitConverter.SingleToUInt32Bits(expected), BitConverter.SingleToUInt32Bits(observed));
            }
        }
        finally
        {
            gl.UseProgram(0); gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.DeleteProgram(program); gl.DeleteTexture(texture); gl.DeleteFramebuffer(framebuffer); gl.DeleteVertexArray(vao);
        }
    }

    [Fact]
    public void DesktopProfileMatchesCpuAcrossAll65536HalfValues()
    {
        using var context = new HiddenWglContext();
        using GL gl = GL.GetApi(context.GetProcAddress);
        using var runtime = new GameShaderRuntime(gl, false, null);
        string header = "#version 300 es\nprecision highp float;\nprecision highp int;\n";
        string vertex = header + "void main(){ " +
            "vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2); " +
            "gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0); }";
        string pixel = header + "uniform uint uSalt; out vec4 result;\n" +
            "void main(){\n" +
            "  uint idx = uint(gl_FragCoord.y) * 256u + uint(gl_FragCoord.x);\n" +
            "  uint pairBits = (idx * 2u) | ((idx * 2u + 1u) << 16u);\n" +
            "  result = vec4(unpackHalf2x16(pairBits ^ uSalt), 0.0, 0.0);\n" +
            "}";
        MethodInfo profile = typeof(GameShaderRuntime).GetMethod("SourceForProfile", BindingFlags.Instance | BindingFlags.NonPublic)!;
        string Desktop(string source, bool vertexStage) => (string)profile.Invoke(runtime, new object[] { source, vertexStage, false })!;
        uint program = GlShaderCompiler.CreateRawProgram(gl, Desktop(vertex, true), Desktop(pixel, false));
        uint texture = gl.GenTexture(), framebuffer = gl.GenFramebuffer(), vao = gl.GenVertexArray();
        try
        {
            gl.BindTexture(TextureTarget.Texture2D, texture);
            gl.TexImage2D<float>(TextureTarget.Texture2D, 0, InternalFormat.Rgba32f, 256, 128, 0,
                PixelFormat.Rgba, PixelType.Float, ReadOnlySpan<float>.Empty);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, texture, 0);
            Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
            gl.BindVertexArray(vao); gl.UseProgram(program); gl.Viewport(0, 0, 256, 128);
            gl.Disable(EnableCap.DepthTest); gl.Disable(EnableCap.Blend);
            int saltLocation = gl.GetUniformLocation(program, "uSalt");
            if (saltLocation >= 0) gl.Uniform1(saltLocation, 0u);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
            float[] actual = new float[256 * 128 * 4];
            gl.ReadPixels<float>(0, 0, 256, 128, PixelFormat.Rgba, PixelType.Float, actual);
            for (int pair = 0; pair < 32768; pair++)
            {
                for (int lane = 0; lane < 2; lane++)
                {
                    ushort bits = (ushort)(pair * 2 + lane);
                    float expected = (float)BitConverter.UInt16BitsToHalf(bits);
                    float observed = actual[pair * 4 + lane];
                    Assert.Equal(BitConverter.SingleToUInt32Bits(expected), BitConverter.SingleToUInt32Bits(observed));
                }
            }
            Assert.Equal(GLEnum.NoError, gl.GetError());
        }
        finally
        {
            gl.UseProgram(0); gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.DeleteProgram(program); gl.DeleteTexture(texture); gl.DeleteFramebuffer(framebuffer); gl.DeleteVertexArray(vao);
        }
    }

    [Fact]
    public void DesktopProfileDropsPackingExtensionWhenOnlyUnpackHalfIsUsed()
    {
        string header = "#version 300 es\nprecision highp float;\nprecision highp int;\n";
        string unpackOnly = GameShaderRuntime.ToDesktopGlsl(header + "void main(){ vec2 a = unpackHalf2x16(0x80010001u); }");
        string withPack = GameShaderRuntime.ToDesktopGlsl(header + "void main(){ uint p = packHalf2x16(vec2(1.0)); vec2 a = unpackHalf2x16(p); }");

        Assert.DoesNotContain("GL_ARB_shading_language_packing", unpackOnly, StringComparison.Ordinal);
        Assert.Contains("dxbcUnpackHalf2x16(0x80010001u)", unpackOnly, StringComparison.Ordinal);
        Assert.Contains("GL_ARB_shading_language_packing", withPack, StringComparison.Ordinal);
        Assert.Contains("dxbcUnpackHalf2x16(p)", withPack, StringComparison.Ordinal);
    }
}
