using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using AssetsManager.Services.Viewer.Rendering.GameShaders;
using AssetsManager.Shaders;
using AssetsManager.Tests.Support;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering;

[Collection("Viewport native graphics")]
public sealed class SceneTextureSamplerGpuTests
{
    public static IEnumerable<object[]> Samples()
    {
        for (int host = 0; host < 3; host++)
        foreach (bool depth in new[] { false, true })
        foreach (float coordinate in new[] { .49f, .51f })
            yield return new object[] { host, depth, coordinate };
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void SceneDepthKeepsNearestAndSceneColorKeepsLinear(int host, bool depth, float coordinate)
    {
        using var context = new HiddenWglContext();
        using GL gl = GL.GetApi(context.GetProcAddress);
        using var runtime = new GameShaderRuntime(gl, false, null);
        string name = depth ? GameShaderRuntime.SceneDepthTexture : GameShaderRuntime.SceneColorTexture;
        GameShaderTranslator.TranslatedStage Stage(string source, bool samples) => new(source,
            new GameShaderTranslator.ShaderSidecar(Array.Empty<GameShaderTranslator.UniformBlock>(),
                samples ? new[] { new GameShaderTranslator.TextureBinding(name, GameShaderTranslator.TextureDimension.Texture2D,
                    new[] { new GameShaderTranslator.SamplerBinding("shared", name) }) } : Array.Empty<GameShaderTranslator.TextureBinding>(),
                Array.Empty<GameShaderTranslator.AttributeBinding>()),
            Array.Empty<GameShaderTranslator.AppliedPatch>(), Array.Empty<(string, string)>(), Array.Empty<uint>());
        var translated = new GameShaderTranslator.TranslatedProgram(
            Stage("#version 300 es\nprecision highp float;\nvoid main() { " +
                "vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2); gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0); }", false),
            Stage("#version 300 es\nprecision highp float;\nuniform highp sampler2D " + name + ";\n" +
                "uniform float sampleU;\nout vec4 result;\nvoid main() { result = vec4(texture(" + name + ", vec2(sampleU, 0.5)).rrr, 1.0); }", true));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        object linked = typeof(GameShaderRuntime).GetMethod("CreateProgram", flags).Invoke(runtime,
            new object[] { translated, GameMaterialKind.StaticMesh, false });
        using var linkedOwner = (IDisposable)linked;
        uint program = (uint)linked.GetType().GetField("Program", flags).GetValue(linked);
        uint texture = gl.GenTexture();
        uint target = gl.GenTexture();
        uint fbo = gl.GenFramebuffer();
        uint vao = gl.GenVertexArray();
        try
        {
            gl.BindTexture(TextureTarget.Texture2D, texture);
            if (depth) gl.TexImage2D<float>(TextureTarget.Texture2D, 0, InternalFormat.DepthComponent24, 2, 1, 0,
                PixelFormat.DepthComponent, PixelType.Float, new float[] { .25f, .75f });
            else gl.TexImage2D<byte>(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, 2, 1, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, new byte[] { 64, 64, 64, 255, 192, 192, 192, 255 });
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
                (int)(depth ? TextureMinFilter.Nearest : TextureMinFilter.Linear));
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
                (int)(depth ? TextureMagFilter.Nearest : TextureMagFilter.Linear));
            gl.BindTexture(TextureTarget.Texture2D, target);
            gl.TexImage2D<byte>(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, 1, 1, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, ReadOnlySpan<byte>.Empty);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, target, 0);
            Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
            var pass = new GameMaterialPass(0, "audit", Array.Empty<GameMaterialDefine>(), Array.Empty<KeyValuePair<string, bool>>(),
                Array.Empty<GameMaterialTexture>(), Array.Empty<GameMaterialParameter>(), GameMaterialPassState.Default);
            var frame = new GameShaderRuntime.Frame(Matrix4x4.Identity, Matrix4x4.Identity, Vector3.Zero, 0, null,
                SceneColor: depth ? 0 : texture, SceneDepth: depth ? texture : 0);
            gl.UseProgram(program);
            if (host == 0)
            {
                var material = new MapMaterialDefinition("audit", 0, false, false, null, null, null, null, null, null, null,
                    MapMaterialRenderState.Default, Array.Empty<string>());
                typeof(GameShaderRuntime).GetMethod("BindTextures", flags).Invoke(runtime,
                    new object[] { linked, pass, 0, material, null, frame, null, null });
            }
            else
            {
                var emitter = new VfxEmitterDefinition("audit", VfxCurveF.Const(1), VfxCurveF.Const(1), null, 0, 0,
                    false, false, 4, VfxCurve3.Const(Vector3.One), null, VfxCurve4.Const(Vector4.One),
                    null, null, null, null, VfxCurve3.Const(Vector3.Zero), "", Vector2.One, 1, false, false);
                if (host == 2) emitter = emitter with
                {
                    CustomMaterial = ModelMaterialDefinition.TextureOnly("audit") with
                    { Program = new GameMaterialProgram(GameMaterialKind.Particles, false, new[] { pass }) }
                };
                SeedParticle(runtime, linked, emitter, pass);
                runtime.BindParticle(emitter, false, 0, frame, null, _ => texture);
            }
            gl.Uniform1(gl.GetUniformLocation(program, "sampleU"), coordinate);
            gl.BindVertexArray(vao);
            gl.Viewport(0, 0, 1, 1);
            gl.Disable(EnableCap.DepthTest);
            gl.Disable(EnableCap.CullFace);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
            byte[] pixel = new byte[4];
            gl.ReadPixels<byte>(0, 0, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, pixel);
            int expected = depth ? coordinate < .5f ? 64 : 191 : (int)MathF.Round(64 + (coordinate * 2 - .5f) * 128);
            Assert.InRange((int)pixel[0], expected - 1, expected + 1);
            gl.ActiveTexture(TextureUnit.Texture0);
            gl.GetInteger(GLEnum.SamplerBinding, out int sampler);
            gl.GetSamplerParameter((uint)sampler, SamplerParameterI.MinFilter, out int min);
            gl.GetSamplerParameter((uint)sampler, SamplerParameterI.MagFilter, out int mag);
            Assert.Equal((int)(depth ? TextureMinFilter.Nearest : TextureMinFilter.Linear), min);
            Assert.Equal((int)(depth ? TextureMagFilter.Nearest : TextureMagFilter.Linear), mag);
            Assert.Equal(GLEnum.NoError, gl.GetError());
        }
        finally
        {
            gl.UseProgram(0);
            gl.BindSampler(0, 0);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.DeleteVertexArray(vao);
            gl.DeleteFramebuffer(fbo);
            gl.DeleteTexture(texture);
            gl.DeleteTexture(target);
        }
    }

    private static void SeedParticle(GameShaderRuntime runtime, object linked, VfxEmitterDefinition emitter, GameMaterialPass pass)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        Type type = typeof(GameShaderRuntime);
        var entry = (ITuple)type.GetMethod("ParticleEntry", flags).Invoke(runtime, new object[] { emitter, false });
        Type passType = type.GetNestedType("PassRuntimeEntry", BindingFlags.NonPublic);
        object globals = Activator.CreateInstance(type.GetNestedType("PassGlobals", BindingFlags.NonPublic), flags, null, new object[] { pass }, null);
        object ready = Activator.CreateInstance(passType, flags, null, new object[] { 0, linked, pass, globals }, null);
        Array passes = Array.CreateInstance(passType, 1);
        passes.SetValue(ready, 0);
        object cache = Activator.CreateInstance(type.GetNestedType("CacheEntry", BindingFlags.NonPublic), flags, null, new object[] { passes, null }, null);
        ((IDictionary)type.GetField("_programs", flags).GetValue(runtime))[entry[1]] = cache;
    }
}
