using System;
using System.Collections.Generic;
using System.Linq;
using AssetsManager.Services.Viewer.Rendering.GameShaders;
using AssetsManager.Shaders;
using AssetsManager.Tests.Support;
using AssetsManager.Utils.Rendering;
using Silk.NET.OpenGL;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering;

[Collection("Viewport native graphics")]
public sealed class GameShaderSamplerFallbackGpuTests
{
    private const string VertexSource = """
        #version 330 core
        void main()
        {
            vec2 pos = gl_VertexID == 0 ? vec2(-1.0, -1.0)
                     : gl_VertexID == 1 ? vec2( 3.0, -1.0)
                     :                    vec2(-1.0,  3.0);
            gl_Position = vec4(pos, 0.0, 1.0);
        }
        """;

    private const string FragmentSource = """
        #version 400 core
        uniform sampler2DArray uSky;
        uniform usampler2D tUnsigned;
        uniform isampler2D tSigned;
        uniform sampler2DShadow uShadow;
        uniform samplerCubeArray uCubeArray;
        uniform sampler2D uExtra[2];
        out vec4 FragColor;
        void main()
        {
            vec4 sky = textureLod(uSky, vec3(0.5, 0.5, 0.0), 0.0);
            uint uVal = texelFetch(tUnsigned, ivec2(0, 0), 0).r;
            int iVal = texelFetch(tSigned, ivec2(0, 0), 0).r;
            float lit = texture(uShadow, vec3(0.5, 0.5, 0.5));
            vec4 cubeArr = texture(uCubeArray, vec4(1.0, 0.0, 0.0, 0.0));
            vec4 a0 = texture(uExtra[0], vec2(0.5));
            vec4 a1 = texture(uExtra[1], vec2(0.5));
            FragColor = vec4(sky.rgb * lit + cubeArr.rgb + vec3(float(uVal), float(iVal), a0.r + a1.r), 1.0);
        }
        """;

    [Fact]
    public void FallbackSamplersIsolateSignedUnsignedAndArrayUniformsFromUnitZero()
    {
        using var context = new HiddenWglContext();
        using GL gl = GL.GetApi(context.GetProcAddress);
        using var runtime = new GameShaderRuntime(gl, gles: false, settings: null);

        uint program = GlShaderCompiler.CreateRawProgram(gl, VertexSource, FragmentSource);
        uint fbo = gl.GenFramebuffer();
        uint color = gl.GenTexture();
        uint sky = gl.GenTexture();
        uint vao = gl.GenVertexArray();
        try
        {
            gl.BindTexture(TextureTarget.Texture2D, color);
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, 4, 4, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, ReadOnlySpan<byte>.Empty);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, color, 0);
            Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));

            gl.BindTexture(TextureTarget.Texture2DArray, sky);
            byte[] skyBytes = { 128, 64, 192, 255 };
            gl.TexImage3D(TextureTarget.Texture2DArray, 0, InternalFormat.Rgba8, 1, 1, 1, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, skyBytes.AsSpan());
            gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);

            gl.UseProgram(program);
            int skyLoc = gl.GetUniformLocation(program, "uSky");
            Assert.True(skyLoc >= 0);
            gl.Uniform1(skyLoc, 0);

            var seen = new HashSet<string>(StringComparer.Ordinal) { "uSky" };
            var samplers = new List<GameShaderRuntime.SamplerRuntime>
            {
                new("uSky", GameShaderTranslator.TextureDimension.CubeArray, null, skyLoc, 0)
            };

            runtime.AssignUnmatchedActiveSamplers(program, seen, samplers);

            var fallbacks = samplers.Where(s => s.IsFallback).ToList();
            Assert.Equal(6, fallbacks.Count);
            Assert.Equal(fallbacks.Count, fallbacks.Select(s => s.Unit).Distinct().Count());
            Assert.All(fallbacks, s => Assert.NotEqual(0u, s.Unit));
            Assert.Single(fallbacks, s => s.TextureName == "tSigned" && s.Fallback == GameShaderRuntime.FallbackSamplerKind.SignedInteger && s.Dimension == GameShaderTranslator.TextureDimension.Buffer);
            Assert.Single(fallbacks, s => s.TextureName == "tUnsigned" && s.Fallback == GameShaderRuntime.FallbackSamplerKind.UnsignedInteger && s.Dimension == GameShaderTranslator.TextureDimension.Buffer);
            Assert.Single(fallbacks, s => s.TextureName == "uShadow" && s.Fallback == GameShaderRuntime.FallbackSamplerKind.DepthCompare && s.Dimension == GameShaderTranslator.TextureDimension.Texture2D);
            Assert.Single(fallbacks, s => s.TextureName == "uCubeArray" && s.Fallback == GameShaderRuntime.FallbackSamplerKind.Color && s.Dimension == GameShaderTranslator.TextureDimension.CubeArray);
            Assert.Single(fallbacks, s => s.TextureName == "uExtra[0]" && s.Fallback == GameShaderRuntime.FallbackSamplerKind.Color && s.Dimension == GameShaderTranslator.TextureDimension.Texture2D);
            Assert.Single(fallbacks, s => s.TextureName == "uExtra[1]" && s.Fallback == GameShaderRuntime.FallbackSamplerKind.Color && s.Dimension == GameShaderTranslator.TextureDimension.Texture2D);

            gl.ActiveTexture(TextureUnit.Texture0);
            gl.BindTexture(TextureTarget.Texture2DArray, sky);
            gl.BindSampler(0, 0);
            foreach (var fallback in fallbacks)
                runtime.BindFallbackSampler(fallback);

            gl.Viewport(0, 0, 4, 4);
            gl.Disable(EnableCap.DepthTest);
            gl.Disable(EnableCap.Blend);
            gl.BindVertexArray(vao);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
            Assert.Equal(GLEnum.NoError, gl.GetError());

            byte[] pixel = new byte[4];
            gl.ReadPixels(2, 2, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, pixel.AsSpan());
            Assert.Equal(128, pixel[0]);
            Assert.Equal(64, pixel[1]);
            Assert.Equal(192, pixel[2]);
            Assert.Equal(255, pixel[3]);
            Assert.Equal(GLEnum.NoError, gl.GetError());
        }
        finally
        {
            gl.UseProgram(0);
            gl.BindVertexArray(0);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.DeleteVertexArray(vao);
            gl.DeleteTexture(sky);
            gl.DeleteTexture(color);
            gl.DeleteFramebuffer(fbo);
            gl.DeleteProgram(program);
        }
    }
}
