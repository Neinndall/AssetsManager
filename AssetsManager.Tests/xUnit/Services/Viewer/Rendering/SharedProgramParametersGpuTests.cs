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
public sealed class SharedProgramParametersGpuTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void InterleavedOwnersAndPassesKeepTheirParametersAndClocks(int host)
    {
        using var context = new HiddenWglContext();
        using GL gl = GL.GetApi(context.GetProcAddress);
        using var runtime = new GameShaderRuntime(gl, false, null);
        bool position = host is 4 or 5 or 6, staticMesh = host == 6;
        bool fraction = host == 3, particle = host is 1 or 2 or 5, mesh = host is 2 or 5;
        var kind = staticMesh ? GameMaterialKind.StaticMesh : particle ? GameMaterialKind.Particles : GameMaterialKind.SkinnedMesh;
        var globals = new GameShaderTranslator.UniformBlock("$Globals", "AuditGlobals", 64, new[]
        {
            Member("Tint", 0), Member("Drive", 16), Member("switch_Test", 32), Member("Optional", 48)
        });
        var vertexFrame = new GameShaderTranslator.UniformBlock("PerFrameVertexCB", "AuditVertexFrame", 544,
            Array.Empty<GameShaderTranslator.BlockMember>());
        var pixelFrame = new GameShaderTranslator.UniformBlock("PerFramePixelCB", "AuditPixelFrame", 576,
            Array.Empty<GameShaderTranslator.BlockMember>());
        var vertexGlobals = new GameShaderTranslator.UniformBlock("$Globals", "AuditGlobalsVS", 64, globals.Members);
        GameShaderTranslator.TranslatedStage Stage(string source, params GameShaderTranslator.UniformBlock[] blocks) =>
            new("#version 300 es\nprecision highp float;\n" + source,
                new GameShaderTranslator.ShaderSidecar(blocks, Array.Empty<GameShaderTranslator.TextureBinding>(),
                    Array.Empty<GameShaderTranslator.AttributeBinding>()),
                Array.Empty<GameShaderTranslator.AppliedPatch>(), Array.Empty<(string, string)>(), Array.Empty<uint>());
        var translated = new GameShaderTranslator.TranslatedProgram(
            Stage("layout(std140) uniform AuditVertexFrame { vec4 vf[34]; };\nflat out float clockVS;\n" +
                (position ? "layout(std140) uniform AuditGlobalsVS { vec4 gv[4]; };\nflat out vec2 playerXY;\n" : "") +
                "void main(){ vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2); " +
                "gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0); clockVS = vf[5].x; " +
                (position ? "playerXY = gv[1].xy;" : "") + " }",
                position ? new[] { vertexFrame, vertexGlobals } : new[] { vertexFrame }),
            Stage("layout(std140) uniform AuditGlobals { vec4 g[4]; };\n" +
                "layout(std140) uniform AuditPixelFrame { vec4 pf[36]; };\n" +
                "flat in float clockVS;\nout vec4 result;\n" +
                (position ? "flat in vec2 playerXY;\nvoid main(){ result = vec4(playerXY, g[1].zw); }" :
                    "void main(){ result = vec4(clockVS, pf[1].x, g[0].x + g[1].x, g[2].x + g[3].x); }"), globals, pixelFrame));
        object linked = typeof(GameShaderRuntime).GetMethod("CreateProgram", Flags)!.Invoke(runtime,
            new object[] { translated, kind, particle })!;
        using var linkedOwner = (IDisposable)linked;
        uint program = (uint)linked.GetType().GetField("Program", Flags)!.GetValue(linked)!;
        uint target = gl.GenTexture(), framebuffer = gl.GenFramebuffer(), vao = gl.GenVertexArray();
        try
        {
            gl.BindTexture(TextureTarget.Texture2D, target);
            gl.TexImage2D<float>(TextureTarget.Texture2D, 0, InternalFormat.Rgba32f, 1, 1, 0,
                PixelFormat.Rgba, PixelType.Float, ReadOnlySpan<float>.Empty);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, target, 0);
            Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
            gl.BindVertexArray(vao);
            gl.Viewport(0, 0, 1, 1);
            var materials = new ModelMaterialDefinition[2];
            var emitters = new VfxEmitterDefinition[2];
            var surfaces = new MapMaterialDefinition[2];
            for (int owner = 0; owner < 2; owner++)
            {
                var passes = new[] { Pass(owner, 0), Pass(owner, 1) };
                if (position)
                    for (int index = 0; index < passes.Length; index++)
                        passes[index] = passes[index] with
                        {
                            Parameters = new[] { new GameMaterialParameter("Drive", new Vector4(.2f, .3f, .4f, .5f), GameMaterialParamSource.Pass) },
                            PlayerPositionParameters = owner == 0
                                ? new[] { new GameMaterialPositionParameter("Drive", index == 0 ? 0b1101u : 0b0111u) }
                                : Array.Empty<GameMaterialPositionParameter>()
                        };
                materials[owner] = ModelMaterialDefinition.TextureOnly("audit") with
                {
                    Program = new GameMaterialProgram(kind, true, passes),
                    DynamicParameters = position ? Array.Empty<GameMaterialDynamicParameter>() : new[]
                    {
                        new GameMaterialDynamicParameter("Drive", state => new Vector4(
                            (fraction ? state.AnimationFraction((uint)(10 + state.Gear)) : state.Time) + state.Gear, 0, 0, 0))
                    }
                };
                object cacheOwner = materials[owner];
                if (staticMesh)
                {
                    surfaces[owner] = new MapMaterialDefinition("audit", 0, false, true, "audit", null, null, null,
                        null, null, null, MapMaterialRenderState.Default, Array.Empty<string>()) { Program = materials[owner].Program };
                    cacheOwner = surfaces[owner];
                }
                if (particle)
                {
                    emitters[owner] = new VfxEmitterDefinition("audit", VfxCurveF.Const(1), VfxCurveF.Const(1),
                        null, 0, 0, false, false, 3, VfxCurve3.Const(Vector3.One), null,
                        VfxCurve4.Const(Vector4.One), null, null, null, null, VfxCurve3.Const(Vector3.Zero),
                        "", Vector2.One, 1, false, mesh, CustomMaterial: materials[owner]);
                    var entry = (ITuple)typeof(GameShaderRuntime).GetMethod("ParticleEntry", Flags)!.Invoke(runtime,
                        new object[] { emitters[owner], mesh })!;
                    cacheOwner = entry[1]!;
                }
                Seed(runtime, cacheOwner, linked, passes);
            }
            // Forward, backward and repeated times exercise seek, pause and cache reuse on the same program.
            foreach (float time in new[] { .125f, .75f, .125f, .125f, 0f })
            foreach (var draw in new[] { (0, 0), (1, 1), (0, 1), (1, 0), (0, 0), (0, 0) })
            {
                int owner = draw.Item1, pass = draw.Item2;
                float ownTime = time + owner * .25f;
                var frame = new GameShaderRuntime.Frame(Matrix4x4.Identity, Matrix4x4.Identity, Vector3.Zero,
                    position ? 0f : ownTime, null, CharacterPosition: new Vector3(100, 200, 300));
                var worldPosition = new Vector3(ownTime + 10, -20 - owner, 30 + pass);
                float drive;
                if (staticMesh)
                {
                    drive = 0;
                    Assert.True(runtime.TryBind(surfaces[owner], pass, null, false, frame, _ => null, _ => null));
                }
                else if (particle)
                {
                    // Alternate a dynamic override with the authored pass value, including its removal.
                    var overrides = !position && owner == 0 ? new Dictionary<string, Vector4> { ["Drive"] = new(ownTime, 0, 0, 0) } : null;
                    drive = owner == 0 ? ownTime : .2f;
                    Assert.Equal(program, runtime.UseParticleProgram(emitters[owner], mesh, pass));
                    runtime.BindParticle(emitters[owner], mesh, pass, frame, overrides, _ => null);
                }
                else
                {
                    drive = (fraction ? ownTime / (4f + owner * 4f) : ownTime) + owner;
                    var clip = new AnimationClipCatalogItem("Dance", "Dance", "", 4f + owner * 4f,
                        null, null, null, Array.Empty<AnimationClipTimedCue>(), 0, false, "")
                    { MaterialAnimations = new[] { new GameMaterialAnimation((uint)(10 + owner), 0, 4f + owner * 4f) } };
                    var state = fraction ? GameMaterialState.Preview(null, clip) with { Gear = owner } : (GameMaterialState)owner;
                    Assert.True(runtime.TryBindSkinned(materials[owner], pass,
                        position ? Matrix4x4.CreateScale(-2, 3, 4) * Matrix4x4.CreateTranslation(worldPosition) : Matrix4x4.Identity,
                        Array.Empty<Matrix4x4>(), false, frame, _ => null, state: state));
                    Assert.Equal((int)program, gl.GetInteger(GLEnum.CurrentProgram));
                }
                gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
                var pixel = new float[4];
                gl.ReadPixels<float>(0, 0, 1, 1, PixelFormat.Rgba, PixelType.Float, pixel);
                float tint = owner == 0 ? .1f + pass * .1f : 0f;
                float switchAndOptional = pass == 0 ? 1.75f : 0f;
                var expected = new[] { ownTime, ownTime, tint + drive, switchAndOptional };
                if (position)
                {
                    var point = particle || staticMesh ? Vector3.Zero : worldPosition;
                    expected = owner != 0 ? new[] { .2f, .3f, .4f, .5f }
                        : pass == 0 ? new[] { point.X, .3f, point.Y, point.Z }
                        : new[] { point.X, point.Y, point.Z, .5f };
                }
                for (int channel = 0; channel < 4; channel++)
                    Assert.InRange(MathF.Abs(pixel[channel] - expected[channel]), 0, .00001f);
                Assert.Equal(GLEnum.NoError, gl.GetError());
            }
        }
        finally
        {
            gl.UseProgram(0);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.DeleteVertexArray(vao);
            gl.DeleteFramebuffer(framebuffer);
            gl.DeleteTexture(target);
        }
    }

    private static GameShaderTranslator.BlockMember Member(string name, uint offset) =>
        new(name, offset, 16, true, GameShaderTranslator.MemberScalar.Float, 1, 4, 0, false);

    private static GameMaterialPass Pass(int owner, int pass)
    {
        var parameters = new List<GameMaterialParameter>
        { new("Drive", new Vector4(.2f, 0, 0, 0), GameMaterialParamSource.Pass) };
        if (owner == 0) parameters.Add(new("Tint", new Vector4(.1f + pass * .1f, 0, 0, 0), GameMaterialParamSource.Pass));
        if (pass == 0) parameters.Add(new("Optional", new Vector4(.75f, 0, 0, 0), GameMaterialParamSource.Pass));
        return new GameMaterialPass(0, "audit", Array.Empty<GameMaterialDefine>(),
            pass == 0 ? new[] { new KeyValuePair<string, bool>("Test", true) } : Array.Empty<KeyValuePair<string, bool>>(),
            Array.Empty<GameMaterialTexture>(), parameters,
            GameMaterialPassState.Default with { CullEnabled = false, DepthEnabled = false });
    }

    private static void Seed(GameShaderRuntime runtime, object owner, object linked, GameMaterialPass[] passes)
    {
        Type type = typeof(GameShaderRuntime);
        Type passType = type.GetNestedType("PassRuntimeEntry", BindingFlags.NonPublic)!;
        Array entries = Array.CreateInstance(passType, passes.Length);
        for (int index = 0; index < passes.Length; index++)
        {
            object globals = Activator.CreateInstance(type.GetNestedType("PassGlobals", BindingFlags.NonPublic)!,
                Flags, null, new object[] { passes[index] }, null)!;
            entries.SetValue(Activator.CreateInstance(passType, Flags, null,
                new object[] { index, linked, passes[index], globals }, null), index);
        }
        object cache = Activator.CreateInstance(type.GetNestedType("CacheEntry", BindingFlags.NonPublic)!, Flags, null,
            new object[] { entries, null }, null)!;
        ((IDictionary)type.GetField("_programs", Flags)!.GetValue(runtime)!)[owner] = cache;
    }
}
