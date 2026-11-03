using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using AssetsManager.Services.Viewer.Rendering.GameShaders;
using AssetsManager.Shaders;
using AssetsManager.Tests.Support;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering;

[Collection("Viewport native graphics")]
public sealed class ShaderSamplerBindingGpuTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SamplerlessBufferDoesNotShareTheColorSamplerUnit(bool bufferInVertex)
    {
        using var context = new HiddenWglContext();
        using GL gl = GL.GetApi(context.GetProcAddress);
        using var runtime = new GameShaderRuntime(gl, false, null);
        const string buffer = "uniform highp usampler2D DATA;\n";
        const string read = "float(texelFetch(DATA, ivec2(0), 0).r)";
        string vertex = "#version 300 es\nprecision highp float;\nprecision highp int;\n" +
            (bufferInVertex ? buffer : "") +
            "void main() { vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2); " +
            "gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0); " +
            (bufferInVertex ? "gl_Position.x += " + read + " * 0.00001;" : "") + " }";
        string pixel = "#version 300 es\nprecision highp float;\nprecision highp int;\n" +
            "uniform highp sampler2D COLOR_TX;\n" + (bufferInVertex ? "" : buffer) +
            "out vec4 result;\nvoid main() { result = texture(COLOR_TX, vec2(0.5))" +
            (bufferInVertex ? "" : " + vec4(" + read + ")") + "; }";
        var raw = new GameShaderTranslator.TextureBinding("DATA", GameShaderTranslator.TextureDimension.Buffer,
            Array.Empty<GameShaderTranslator.SamplerBinding>());
        var color = new GameShaderTranslator.TextureBinding("COLOR__TX", GameShaderTranslator.TextureDimension.Texture2D,
            new[] { new GameShaderTranslator.SamplerBinding("colorSampler", "COLOR_TX") });
        GameShaderTranslator.TranslatedStage Stage(string source, GameShaderTranslator.TextureBinding[] textures) =>
            new(source, new GameShaderTranslator.ShaderSidecar(Array.Empty<GameShaderTranslator.UniformBlock>(),
                textures, Array.Empty<GameShaderTranslator.AttributeBinding>()),
                Array.Empty<GameShaderTranslator.AppliedPatch>(), Array.Empty<(string, string)>(), Array.Empty<uint>());
        var translated = new GameShaderTranslator.TranslatedProgram(
            Stage(vertex, bufferInVertex ? new[] { raw } : Array.Empty<GameShaderTranslator.TextureBinding>()),
            Stage(pixel, bufferInVertex ? new[] { color } : new[] { color, raw }));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object linked = typeof(GameShaderRuntime).GetMethod("CreateProgram", flags).Invoke(runtime,
            new object[] { translated, GameMaterialKind.StaticMesh, false });
        using var programOwner = (IDisposable)linked;
        uint program = (uint)linked.GetType().GetField("Program", flags).GetValue(linked);
        uint vao = gl.GenVertexArray();
        uint fbo = gl.GenFramebuffer();
        uint target = gl.GenTexture();
        try
        {
            gl.BindVertexArray(vao);
            gl.BindTexture(TextureTarget.Texture2D, target);
            gl.TexImage2D<byte>(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, 1, 1, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, ReadOnlySpan<byte>.Empty);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, target, 0);
            Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
            var material = new MapMaterialDefinition("audit", 0, false, false, null, null,
                null, null, null, null, null, MapMaterialRenderState.Default, Array.Empty<string>());
            var pass = new GameMaterialPass(0, null, Array.Empty<GameMaterialDefine>(),
                Array.Empty<KeyValuePair<string, bool>>(), Array.Empty<GameMaterialTexture>(),
                Array.Empty<GameMaterialParameter>(), GameMaterialPassState.Default);
            var frame = new GameShaderRuntime.Frame(Matrix4x4.Identity, Matrix4x4.Identity, Vector3.Zero, 0, null);
            typeof(GameShaderRuntime).GetMethod("BindTextures", flags).Invoke(runtime,
                new object[] { linked, pass, 0, material, null, frame, null, null });
            gl.UseProgram(program);
            gl.Viewport(0, 0, 1, 1);
            gl.Disable(EnableCap.DepthTest);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
            Assert.Equal(GLEnum.NoError, gl.GetError());
            byte[] pixels = new byte[4];
            gl.ReadPixels<byte>(0, 0, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
            Assert.Equal(new byte[] { 128, 128, 128, 255 }, pixels);
            gl.GetUniform(program, gl.GetUniformLocation(program, "DATA"), out int dataUnit);
            gl.GetUniform(program, gl.GetUniformLocation(program, "COLOR_TX"), out int colorUnit);
            Assert.NotEqual(dataUnit, colorUnit);
        }
        finally
        {
            gl.UseProgram(0);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.DeleteTexture(target);
            gl.DeleteFramebuffer(fbo);
            gl.DeleteVertexArray(vao);
        }
    }
}
