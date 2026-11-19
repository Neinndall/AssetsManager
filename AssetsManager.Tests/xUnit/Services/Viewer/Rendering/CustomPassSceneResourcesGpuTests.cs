using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Services.Viewer.Rendering.Core;
using AssetsManager.Services.Viewer.Rendering.GameShaders;
using AssetsManager.Services.Viewer.Parsing;
using AssetsManager.Services.Viewer.Runtime;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Services.Viewer.Vfx.Session;
using AssetsManager.Shaders;
using AssetsManager.Tests.Support;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using Silk.NET.OpenGL;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering;

[Collection("Viewport native graphics")]
public sealed class CustomPassSceneResourcesGpuTests
{
    private const BindingFlags NonPublicFlags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CustomPassSceneColorSamplerClampsEdgesEvenWithWrapModeOrAuthoredMaterialSampler(bool authoredRepeatSampler)
    {
        using var context = new HiddenWglContext();
        using GL gl = GL.GetApi(context.GetProcAddress);
        using var runtime = new GameShaderRuntime(gl, false, null);

        var samplerTex = new GameMaterialSamplerState("No_Mip", MapTextureWrap.Repeat, MapTextureWrap.Repeat, MapTextureWrap.Repeat, false, false);
        var textures = new List<GameMaterialTexture>();
        if (authoredRepeatSampler)
            textures.Add(new GameMaterialTexture(GameShaderRuntime.SceneColorTexture,
                new MapTextureReference("scene_color.tex", 0), GameMaterialTextureSource.Material, samplerTex));

        var pass = new GameMaterialPass(0, "scene-color-clamp", Array.Empty<GameMaterialDefine>(), Array.Empty<KeyValuePair<string, bool>>(),
            textures, Array.Empty<GameMaterialParameter>(), GameMaterialPassState.Default);

        var translated = MakeScreenQuadShader(new[]
        {
            (GameShaderRuntime.SceneColorTexture, GameShaderTranslator.TextureDimension.Texture2D),
            ("PIXEL_COLOR_REMAP_RAMP_SharedTexture", GameShaderTranslator.TextureDimension.Texture2D)
        },
        $"uniform highp sampler2D {GameShaderRuntime.SceneColorTexture};\n" +
        "uniform highp sampler2D PIXEL_COLOR_REMAP_RAMP_SharedTexture;\n" +
        "out vec4 result;\n" +
        "void main() {\n" +
        $"  vec4 sceneCol = texture({GameShaderRuntime.SceneColorTexture}, vec2(1.25, 0.5));\n" +
        "  vec4 ramp = texture(PIXEL_COLOR_REMAP_RAMP_SharedTexture, vec2(0.5));\n" +
        "  result = vec4(sceneCol.r, ramp.r, ramp.a, 1.0);\n" +
        "}");

        object linked = typeof(GameShaderRuntime).GetMethod("CreateProgram", NonPublicFlags)!.Invoke(
            runtime, new object[] { translated, GameMaterialKind.Particles, false })!;
        using var linkedOwner = (IDisposable)linked;

        uint colorTex = gl.GenTexture();
        uint target = gl.GenTexture();
        uint fbo = gl.GenFramebuffer();
        uint vao = gl.GenVertexArray();
        try
        {
            gl.BindTexture(TextureTarget.Texture2D, colorTex);
            gl.TexImage2D<byte>(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, 2, 1, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, new byte[] { 40, 40, 40, 255, 220, 220, 220, 255 });
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);

            gl.BindTexture(TextureTarget.Texture2D, target);
            gl.TexImage2D<byte>(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, 1, 1, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, ReadOnlySpan<byte>.Empty);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, target, 0);
            Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));

            var emitter = CustomEmitter("clamp-color", pass, renderPass: -1);
            SeedParticle(runtime, emitter, false, (pass, linked));

            gl.UseProgram(ProgramHandle(linked));
            runtime.BindParticle(emitter, false, 0,
                new GameShaderRuntime.Frame(Matrix4x4.Identity, Matrix4x4.Identity, Vector3.Zero, 0f, null, SceneColor: colorTex),
                null,
                name => name == GameShaderRuntime.SceneColorTexture ? colorTex : null);

            gl.Viewport(0, 0, 1, 1);
            gl.Disable(EnableCap.DepthTest);
            gl.Disable(EnableCap.CullFace);
            gl.BindVertexArray(vao);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

            byte[] pixel = new byte[4];
            gl.ReadPixels<byte>(0, 0, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, pixel);
            // U=1.25 must clamp to the right texel (220), never wrap back to the left texel (40).
            Assert.InRange(pixel[0], 218, 222);
            // Unbound _SharedTexture must resolve to neutral black (0,0,0,0).
            Assert.Equal(0, pixel[1]);
            Assert.Equal(0, pixel[2]);

            gl.ActiveTexture(TextureUnit.Texture0);
            gl.GetInteger(GLEnum.SamplerBinding, out int samplerObj);
            gl.GetSamplerParameter((uint)samplerObj, SamplerParameterI.WrapS, out int wrapS);
            gl.GetSamplerParameter((uint)samplerObj, SamplerParameterI.MinFilter, out int minFilter);
            Assert.Equal((int)TextureWrapMode.ClampToEdge, wrapS);
            Assert.Equal((int)TextureMinFilter.Linear, minFilter);
            Assert.Equal(GLEnum.NoError, gl.GetError());
        }
        finally
        {
            gl.UseProgram(0);
            gl.BindSampler(0, 0);
            gl.BindSampler(1, 0);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.DeleteVertexArray(vao);
            gl.DeleteFramebuffer(fbo);
            gl.DeleteTexture(colorTex);
            gl.DeleteTexture(target);
        }
    }

    [Fact]
    public void MultiPassCustomMaterialRequestsPerPassSceneInputsDrawsBothAndCoexistsWithStockEmitters()
    {
        using var context = new HiddenWglContext();
        using GL gl = GL.GetApi(context.GetProcAddress);
        using var renderer = new VfxOpenGlRenderer();
        renderer.Initialize(gl);
        GameShaderRuntime runtime = GetGameShaders(renderer);

        var pass0 = new GameMaterialPass(0, "depth-opaque", Array.Empty<GameMaterialDefine>(), Array.Empty<KeyValuePair<string, bool>>(),
            Array.Empty<GameMaterialTexture>(), Array.Empty<GameMaterialParameter>(),
            new GameMaterialPassState(BlendEnabled: false, MapBlendFactor.One, MapBlendFactor.Zero, MapBlendFactor.One, MapBlendFactor.Zero,
                CullEnabled: false, GameMaterialWinding.CounterClockwise, DepthEnabled: false, DepthCompareFunc: 3, WriteMask: 0x0F));
        var pass1 = new GameMaterialPass(1, "color-additive", Array.Empty<GameMaterialDefine>(), Array.Empty<KeyValuePair<string, bool>>(),
            Array.Empty<GameMaterialTexture>(), Array.Empty<GameMaterialParameter>(),
            new GameMaterialPassState(BlendEnabled: true, MapBlendFactor.One, MapBlendFactor.One, MapBlendFactor.One, MapBlendFactor.One,
                CullEnabled: false, GameMaterialWinding.CounterClockwise, DepthEnabled: false, DepthCompareFunc: 3, WriteMask: 0x0F));

        var shader0 = MakeParticleQuadShader(new[]
        {
            (GameShaderRuntime.SceneDepthTexture, GameShaderTranslator.TextureDimension.Texture2D),
            ("PIXEL_COLOR_REMAP_RAMP_SharedTexture", GameShaderTranslator.TextureDimension.Texture2D)
        },
        $"uniform highp sampler2D {GameShaderRuntime.SceneDepthTexture};\n" +
        "uniform highp sampler2D PIXEL_COLOR_REMAP_RAMP_SharedTexture;\n" +
        "out vec4 result;\n" +
        "void main() {\n" +
        $"  float d = texelFetch({GameShaderRuntime.SceneDepthTexture}, ivec2(gl_FragCoord.xy), 0).r;\n" +
        "  vec4 neutral = texelFetch(PIXEL_COLOR_REMAP_RAMP_SharedTexture, ivec2(0, 0), 0);\n" +
        "  result = vec4(d > 0.2 && d < 0.4 ? 0.5 : 0.0, neutral.r, 0.0, 1.0);\n" +
        "}");
        var shader1 = MakeParticleQuadShader(new[]
        {
            (GameShaderRuntime.SceneColorTexture, GameShaderTranslator.TextureDimension.Texture2D)
        },
        $"uniform highp sampler2D {GameShaderRuntime.SceneColorTexture};\n" +
        "out vec4 result;\n" +
        "void main() {\n" +
        $"  vec4 col = texture({GameShaderRuntime.SceneColorTexture}, vec2(0.5, 0.5));\n" +
        "  result = vec4(0.0, 0.0, col.g > 0.7 ? 0.75 : 0.0, 1.0);\n" +
        "}");

        var emitterDef = CustomEmitter("multipass-scene", new[] { pass0, pass1 }, renderPass: -1);
        SeedParticle(runtime, emitterDef, false, (pass0, CompileParticleProgram(runtime, shader0)), (pass1, CompileParticleProgram(runtime, shader1)));

        Assert.Equal((true, true), renderer.SceneInputsFor(emitterDef));

        uint target = CreateColorTexture(gl, 64, 64);
        uint depth = CreateDepthRenderbuffer(gl, 64, 64);
        uint fbo = CreateFramebuffer(gl, target, depth);
        try
        {
            gl.Viewport(0, 0, 64, 64);
            gl.ClearColor(0f, 0.9f, 0f, 1f);
            gl.ClearDepth(0.3);
            gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            renderer.CaptureScene(64, 64, true, true);

            var view = Matrix4x4.CreateLookAt(new Vector3(0f, 0f, 10f), Vector3.Zero, Vector3.UnitY);
            var viewProj = view * Matrix4x4.CreateOrthographic(2f, 2f, 0.1f, 100f);
            var customState = MakeEmitterState(emitterDef);
            var stockEmitter = StockQuadEmitter("stock-after", color: new Vector4(0f, 20f / 255f, 0f, 1f), renderPass: -1, blendMode: 0);
            var stockState = MakeEmitterState(stockEmitter, new Vector4(0f, 20f / 255f, 0f, 1f));
            stockState.Texture = CreateSolidTexture(gl, 255, 255, 255, 255);
            try
            {
                renderer.Render(new[]
                {
                    new VfxRenderQueueEntry(customState, 0, 0),
                    new VfxRenderQueueEntry(stockState, 0, 1)
                }, viewProj, view);

                byte[] pixel = ReadCenterPixel(gl, 64, 64);
                // Pass 0 writes R ~128, Pass 1 adds B ~191, stock additive emitter adds G ~20.
                Assert.InRange(pixel[0], 120, 136);
                Assert.InRange(pixel[1], 15, 25);
                Assert.InRange(pixel[2], 185, 200);
                Assert.Equal(3, renderer.LastParticleDrawCounts.Draws);
                Assert.Equal(GLEnum.NoError, gl.GetError());
            }
            finally
            {
                gl.DeleteTexture(stockState.Texture);
            }
        }
        finally
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.DeleteFramebuffer(fbo);
            gl.DeleteRenderbuffer(depth);
            gl.DeleteTexture(target);
        }
    }

    [Fact]
    public void StaleSceneCapturesDoNotLeakAfterClearResetOrChannelWithdrawalAndRecoverWhenReenabled()
    {
        using var context = new HiddenWglContext();
        using GL gl = GL.GetApi(context.GetProcAddress);
        using var renderer = new VfxOpenGlRenderer();
        renderer.Initialize(gl);
        GameShaderRuntime runtime = GetGameShaders(renderer);

        var pass = new GameMaterialPass(0, "scene-reader", Array.Empty<GameMaterialDefine>(), Array.Empty<KeyValuePair<string, bool>>(),
            Array.Empty<GameMaterialTexture>(), Array.Empty<GameMaterialParameter>(),
            new GameMaterialPassState(BlendEnabled: false, MapBlendFactor.One, MapBlendFactor.Zero, MapBlendFactor.One, MapBlendFactor.Zero,
                CullEnabled: false, GameMaterialWinding.CounterClockwise, DepthEnabled: false, DepthCompareFunc: 3, WriteMask: 0x0F));
        var shader = MakeParticleQuadShader(new[]
        {
            (GameShaderRuntime.SceneColorTexture, GameShaderTranslator.TextureDimension.Texture2D),
            (GameShaderRuntime.SceneDepthTexture, GameShaderTranslator.TextureDimension.Texture2D)
        },
        $"uniform highp sampler2D {GameShaderRuntime.SceneColorTexture};\n" +
        $"uniform highp sampler2D {GameShaderRuntime.SceneDepthTexture};\n" +
        "out vec4 result;\n" +
        "void main() {\n" +
        $"  vec4 col = texture({GameShaderRuntime.SceneColorTexture}, vec2(0.5, 0.5));\n" +
        $"  float d = texelFetch({GameShaderRuntime.SceneDepthTexture}, ivec2(gl_FragCoord.xy), 0).r;\n" +
        "  result = vec4(col.r, d, col.b, 1.0);\n" +
        "}");

        var emitterDef = CustomEmitter("stale-check", pass, renderPass: -1);
        var linked = CompileParticleProgram(runtime, shader);
        SeedParticle(runtime, emitterDef, false, (pass, linked));
        var state = MakeEmitterState(emitterDef);

        uint target = CreateColorTexture(gl, 64, 64);
        uint depth = CreateDepthRenderbuffer(gl, 64, 64);
        uint fbo = CreateFramebuffer(gl, target, depth);
        try
        {
            // Frame 1: Capture bright red color + 0.25 depth at 64x64.
            gl.Viewport(0, 0, 64, 64);
            gl.ClearColor(0.8f, 0f, 0.2f, 1f);
            gl.ClearDepth(0.25);
            gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            renderer.CaptureScene(64, 64, true, true);
            gl.ClearColor(0f, 0f, 0f, 1f);
            gl.Clear(ClearBufferMask.ColorBufferBit);
            renderer.Render(new[] { new VfxRenderQueueEntry(state, 0, 0) }, Matrix4x4.Identity, Matrix4x4.Identity);
            byte[] f1 = ReadCenterPixel(gl, 64, 64);
            Assert.InRange(f1[0], 195, 212);
            Assert.InRange(f1[1], 58, 70);

            // Frame 2a: Switching/clearing textures must invalidate stale captures.
            renderer.ClearTextures();
            SeedParticle(runtime, emitterDef, false, (pass, CompileParticleProgram(runtime, shader)));
            gl.ClearColor(0f, 0f, 0f, 1f);
            gl.Clear(ClearBufferMask.ColorBufferBit);
            renderer.Render(new[] { new VfxRenderQueueEntry(state, 0, 0) }, Matrix4x4.Identity, Matrix4x4.Identity);
            byte[] f2a = ReadCenterPixel(gl, 64, 64);
            Assert.Equal(0, f2a[0]);
            Assert.Equal(0, f2a[1]);
            Assert.Equal(0, f2a[2]);

            // Frame 2b: Capture both at 64x64, then resize to 32x32 while capturing ONLY Color -> stale 64x64 Depth must not be exposed.
            gl.ClearColor(0.2f, 0f, 0.8f, 1f);
            gl.ClearDepth(0.75);
            gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            renderer.CaptureScene(64, 64, true, true);
            gl.Viewport(0, 0, 32, 32);
            gl.ClearColor(0.4f, 0f, 0.6f, 1f);
            gl.Clear(ClearBufferMask.ColorBufferBit);
            renderer.CaptureScene(32, 32, captureColor: true, captureDepth: false);
            gl.ClearColor(0f, 0f, 0f, 1f);
            gl.Clear(ClearBufferMask.ColorBufferBit);
            renderer.Render(new[] { new VfxRenderQueueEntry(state, 0, 0) }, Matrix4x4.Identity, Matrix4x4.Identity);
            byte[] f2b = ReadCenterPixel(gl, 32, 32);
            Assert.InRange(f2b[0], 95, 110);
            Assert.Equal(0, f2b[1]);

            // Frame 3: Re-capture both at 32x32 -> both channels recover immediately.
            gl.ClearColor(0.9f, 0f, 0.1f, 1f);
            gl.ClearDepth(0.6);
            gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            renderer.CaptureScene(32, 32, true, true);
            gl.ClearColor(0f, 0f, 0f, 1f);
            gl.Clear(ClearBufferMask.ColorBufferBit);
            renderer.Render(new[] { new VfxRenderQueueEntry(state, 0, 0) }, Matrix4x4.Identity, Matrix4x4.Identity);
            byte[] f3 = ReadCenterPixel(gl, 32, 32);
            Assert.InRange(f3[0], 220, 240);
            Assert.InRange(f3[1], 145, 160);
            Assert.Equal(GLEnum.NoError, gl.GetError());
        }
        finally
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.DeleteFramebuffer(fbo);
            gl.DeleteRenderbuffer(depth);
            gl.DeleteTexture(target);
        }
    }

    [Fact]
    public void PreparedPassesGroundAndOverCustomColorCaptureSeesEarlierContributorsAcrossOwners()
    {
        using var context = new HiddenWglContext();
        using GL gl = GL.GetApi(context.GetProcAddress);

        using var ownerA = new VfxRenderSession();
        using var ownerB = new VfxRenderSession();
        ownerA.Initialize(gl);
        ownerB.Initialize(gl);
        ownerA.SetViewportSize(64, 64);
        ownerB.SetViewportSize(64, 64);

        // Owner A: Ground emitter (draws bright green on ground before characters) + Under emitter (adds 0.4 green).
        var groundDef = StockQuadEmitter("ground-emitter", isGround: true, color: new Vector4(0f, 0.5f, 0f, 1f), renderPass: -1);
        var underDef = StockQuadEmitter("under-emitter", isGround: false, color: new Vector4(0f, 0.4f, 0f, 1f), renderPass: -1);

        // Owner B: Custom Under pass reading SceneColor (should see Ground's green even with GlSceneDepthPass prepass!)
        // and Custom Over pass reading SceneColor (should see Ground + Under).
        var readColorPass = new GameMaterialPass(0, "read-scene-color", Array.Empty<GameMaterialDefine>(), Array.Empty<KeyValuePair<string, bool>>(),
            Array.Empty<GameMaterialTexture>(), Array.Empty<GameMaterialParameter>(),
            new GameMaterialPassState(BlendEnabled: false, MapBlendFactor.One, MapBlendFactor.Zero, MapBlendFactor.One, MapBlendFactor.Zero,
                CullEnabled: false, GameMaterialWinding.CounterClockwise, DepthEnabled: false, DepthCompareFunc: 3, WriteMask: 0x0F));
        var readColorShader = MakeParticleQuadShader(new[]
        {
            (GameShaderRuntime.SceneColorTexture, GameShaderTranslator.TextureDimension.Texture2D)
        },
        $"uniform highp sampler2D {GameShaderRuntime.SceneColorTexture};\n" +
        "out vec4 result;\n" +
        "void main() {\n" +
        $"  vec4 bg = texture({GameShaderRuntime.SceneColorTexture}, vec2(0.5, 0.5));\n" +
        "  result = vec4(bg.g, 0.0, 1.0, 1.0);\n" +
        "}");

        var customUnderDef = CustomEmitter("custom-under-color", readColorPass, renderPass: -1);
        var customOverDef = CustomEmitter("custom-over-color", readColorPass, renderPass: 0);

        VfxOpenGlRenderer rendererB = GetSessionRenderer(ownerB);
        GameShaderRuntime runtimeB = GetGameShaders(rendererB);

        uint white = CreateSolidTexture(gl, 255, 255, 255, 255);
        ownerA.SetSystem(new VfxSystemModel
        {
            Name = "OwnerA",
            TotalDuration = 1,
            Definition = new VfxSystemDefinition(1, "OwnerA", "ownera.troybin", new[] { groundDef })
        });
        ownerB.SetSystem(new VfxSystemModel
        {
            Name = "OwnerB",
            TotalDuration = 1,
            Definition = new VfxSystemDefinition(2, "OwnerB", "ownerb.troybin", new[] { customUnderDef })
        });
        ownerA.Seek(0.1);
        ownerA.ProcessPendingGpuState();
        ownerA.Graphs[0].Root.Emitters[0].Texture = white;
        ownerB.Seek(0.1);
        ownerB.ProcessPendingGpuState();
        SeedParticle(runtimeB, customUnderDef, false, (readColorPass, CompileParticleProgram(runtimeB, readColorShader)));
        var view = Matrix4x4.CreateLookAt(new Vector3(0f, 10f, 0f), Vector3.Zero, -Vector3.UnitZ);
        var viewProj = view * Matrix4x4.CreateOrthographic(2f, 2f, 0.1f, 100f);

        uint target = CreateColorTexture(gl, 64, 64);
        uint depth = CreateDepthStencilRenderbuffer(gl, 64, 64);
        uint fbo = CreateFramebuffer(gl, target, depth, stencil: true);
        using var depthPass = new GlSceneDepthPass(gl);
        var batches = new List<IDisposable>();
        try
        {
            gl.Viewport(0, 0, 64, 64);
            gl.ClearColor(0f, 0f, 0f, 1f);
            gl.ClearDepth(1.0);
            gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit);

            Assert.True(ownerA.PrepareRenderFrame(viewProj, view));
            Assert.True(ownerB.PrepareRenderFrame(viewProj, view));
            Assert.True(((IPreparedParticlePass)ownerA).HasPreparedGroundPass);
            Assert.True(((IPreparedParticlePass)ownerB).NeedsPreparedPostGroundColorCapture);

            byte[] groundBeforeCapture = null;
            PreparedParticlePasses.Render(
                new IPreparedParticlePass[] { ownerA, ownerB },
                batches,
                renderCharacters: () => groundBeforeCapture = ReadCenterPixel(gl, 64, 64),
                renderSceneDepth: capture => depthPass.Render(64, 64, static () => { }, capture));

            Assert.NotNull(groundBeforeCapture);
            Assert.InRange(groundBeforeCapture[1], 118, 138);
            byte[] underAfterGround = ReadCenterPixel(gl, 64, 64);
            // Ground drew ~128 green before Under custom material; SceneColor capture after ground+characters must carry ~128 into result.r.
            Assert.InRange(underAfterGround[2], 250, 255);
            Assert.InRange(underAfterGround[0], 118, 138);

            // Now test Under contributing before Over custom material.
            ownerA.SetSystem(new VfxSystemModel
            {
                Name = "OwnerAUnder",
                TotalDuration = 1,
                Definition = new VfxSystemDefinition(3, "OwnerAUnder", "owneraunder.troybin", new[] { underDef })
            });
            ownerB.SetSystem(new VfxSystemModel
            {
                Name = "OwnerBOver",
                TotalDuration = 1,
                Definition = new VfxSystemDefinition(4, "OwnerBOver", "ownerbover.troybin", new[] { customOverDef })
            });
            ownerA.Seek(0.1);
            ownerA.ProcessPendingGpuState();
            ownerA.Graphs[0].Root.Emitters[0].Texture = white;
            ownerB.Seek(0.1);
            ownerB.ProcessPendingGpuState();
            SeedParticle(runtimeB, customOverDef, false, (readColorPass, CompileParticleProgram(runtimeB, readColorShader)));

            gl.ClearColor(0f, 0f, 0f, 1f);
            gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit);
            Assert.True(ownerA.PrepareRenderFrame(viewProj, view));
            Assert.True(ownerB.PrepareRenderFrame(viewProj, view));
            Assert.False(((IPreparedParticlePass)ownerB).NeedsPreparedSceneCapture);
            Assert.True(((IPreparedParticlePass)ownerB).NeedsPreparedPostColorCapture);

            PreparedParticlePasses.Render(
                new IPreparedParticlePass[] { ownerA, ownerB },
                batches,
                renderCharacters: null,
                renderSceneDepth: capture => depthPass.Render(64, 64, static () => { }, capture));

            byte[] overAfterUnder = ReadCenterPixel(gl, 64, 64);
            // Under drew ~102 green; Over custom material must sample Under's green into result.r.
            Assert.InRange(overAfterUnder[0], 92, 112);
            Assert.InRange(overAfterUnder[2], 250, 255);
            Assert.Equal(GLEnum.NoError, gl.GetError());
        }
        finally
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.DeleteFramebuffer(fbo);
            gl.DeleteRenderbuffer(depth);
            gl.DeleteTexture(target);
            gl.DeleteTexture(white);
        }
    }

    [Fact]
    public void PreparedSessionAndMapParticleRendererWithdrawAndRecoverPerPhaseSceneCaptures()
    {
        using var context = new HiddenWglContext();
        using GL gl = GL.GetApi(context.GetProcAddress);

        using var session = new VfxRenderSession();
        using var mapRenderer = new MapParticleRenderer();
        session.Initialize(gl);
        mapRenderer.Initialize(gl);
        session.SetViewportSize(64, 64);

        var readColorPass = new GameMaterialPass(0, "read-scene-color", Array.Empty<GameMaterialDefine>(), Array.Empty<KeyValuePair<string, bool>>(),
            Array.Empty<GameMaterialTexture>(), Array.Empty<GameMaterialParameter>(),
            new GameMaterialPassState(BlendEnabled: false, MapBlendFactor.One, MapBlendFactor.Zero, MapBlendFactor.One, MapBlendFactor.Zero,
                CullEnabled: false, GameMaterialWinding.CounterClockwise, DepthEnabled: false, DepthCompareFunc: 3, WriteMask: 0x0F));
        var readColorShader = MakeParticleQuadShader(new[]
        {
            (GameShaderRuntime.SceneColorTexture, GameShaderTranslator.TextureDimension.Texture2D)
        },
        $"uniform highp sampler2D {GameShaderRuntime.SceneColorTexture};\n" +
        "out vec4 result;\n" +
        "void main() {\n" +
        $"  vec4 bg = texture({GameShaderRuntime.SceneColorTexture}, vec2(0.5, 0.5));\n" +
        "  result = vec4(bg.g, 0.0, 1.0, 1.0);\n" +
        "}");

        var stockUnder = StockQuadEmitter("stock-under", isGround: false, color: new Vector4(0f, 0.4f, 0f, 1f), renderPass: -1);
        var customUnder = CustomEmitter("session-custom-under", readColorPass, renderPass: -1);
        var mapCustomOver = CustomEmitter("map-custom-over", readColorPass, renderPass: 0);

        VfxOpenGlRenderer sessionRenderer = GetSessionRenderer(session);
        GameShaderRuntime sessionShaders = GetGameShaders(sessionRenderer);
        var mapGlRenderer = (VfxOpenGlRenderer)typeof(MapParticleRenderer).GetField("_renderer", NonPublicFlags)!.GetValue(mapRenderer)!;
        GameShaderRuntime mapShaders = GetGameShaders(mapGlRenderer);

        const uint mapSystemHash = 0x41000001u;
        var mapSystem = new VfxSystemDefinition(mapSystemHash, "MapOver", "Maps/Test/MapOver", new[] { mapCustomOver });
        var placeable = new MapPlaceableData(
            0x10000001u,
            0x20000001u,
            MapParticleParser.MapParticleClass,
            "MapOverPlacement",
            Matrix4x4.Identity,
            MapPlaceableData.EveryLayer,
            null,
            new Dictionary<uint, BinTreeProperty>());
        var mapData = new MapParticleData(placeable, mapSystemHash, false, false);
        MapParticleRuntime mapRuntime = MapParticleRuntime.Create(
            mapData,
            mapSystem,
            new Dictionary<uint, VfxSystemDefinition> { [mapSystemHash] = mapSystem },
            new Dictionary<uint, uint>());
        mapRuntime.Graph.Update(0.1f);
        SeedParticle(mapShaders, mapCustomOver, false, (readColorPass, CompileParticleProgram(mapShaders, readColorShader)));

        uint white = CreateSolidTexture(gl, 255, 255, 255, 255);
        uint target = CreateColorTexture(gl, 64, 64);
        uint depth = CreateDepthStencilRenderbuffer(gl, 64, 64);
        uint fbo = CreateFramebuffer(gl, target, depth, stencil: true);
        var batches = new List<IDisposable>();
        try
        {
            var view = Matrix4x4.CreateLookAt(new Vector3(0f, 10f, 0f), Vector3.Zero, -Vector3.UnitZ);
            var viewProj = view * Matrix4x4.CreateOrthographic(2f, 2f, 0.1f, 100f);

            // Frame 1: Session requests post-ground SceneColor.
            session.SetSystem(new VfxSystemModel
            {
                Name = "SessionCustom",
                TotalDuration = 1,
                Definition = new VfxSystemDefinition(10, "SessionCustom", "sessioncustom.troybin", new[] { customUnder })
            });
            session.Seek(0.1);
            session.ProcessPendingGpuState();
            SeedParticle(sessionShaders, customUnder, false, (readColorPass, CompileParticleProgram(sessionShaders, readColorShader)));

            gl.Viewport(0, 0, 64, 64);
            gl.ClearColor(0f, 0f, 0f, 1f);
            gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit);

            Assert.True(session.PrepareRenderFrame(viewProj, view));
            Assert.True(((IPreparedParticlePass)session).NeedsPreparedPostGroundColorCapture);
            PreparedParticlePasses.Render(
                new IPreparedParticlePass[] { session },
                batches,
                renderCharacters: () =>
                {
                    gl.ClearColor(0f, 0.6f, 0f, 1f);
                    gl.Clear(ClearBufferMask.ColorBufferBit);
                });
            Assert.True(sessionRenderer.HasValidColorCapture);
            byte[] frame1 = ReadCenterPixel(gl, 64, 64);
            Assert.InRange(frame1[0], 145, 162);

            // Frame 2: Withdraw SceneColor in session (switch to stock emitter) and pair with MapParticleRenderer Over custom pass.
            session.SetSystem(new VfxSystemModel
            {
                Name = "SessionStock",
                TotalDuration = 1,
                Definition = new VfxSystemDefinition(11, "SessionStock", "sessionstock.troybin", new[] { stockUnder })
            });
            session.Seek(0.1);
            session.ProcessPendingGpuState();
            session.Graphs[0].Root.Emitters[0].Texture = white;

            Assert.True(session.PrepareRenderFrame(viewProj, view));
            Assert.False(sessionRenderer.HasValidColorCapture);
            Assert.False(((IPreparedParticlePass)session).NeedsPreparedSceneCapture);
            Assert.False(((IPreparedParticlePass)session).NeedsPreparedPostGroundColorCapture);
            Assert.False(((IPreparedParticlePass)session).NeedsPreparedPostColorCapture);

            Assert.True(mapRenderer.PrepareRenderFrame(new[] { mapRuntime }, viewProj, view, 64, 64));
            Assert.False(((IPreparedParticlePass)mapRenderer).NeedsPreparedSceneCapture);
            Assert.True(((IPreparedParticlePass)mapRenderer).NeedsPreparedPostColorCapture);

            gl.ClearColor(0f, 0f, 0f, 1f);
            gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit);
            PreparedParticlePasses.Render(new IPreparedParticlePass[] { session, mapRenderer }, batches);
            Assert.False(sessionRenderer.HasValidColorCapture);
            Assert.True(mapGlRenderer.HasValidColorCapture);
            byte[] frame2 = ReadCenterPixel(gl, 64, 64);
            Assert.InRange(frame2[0], 92, 112);
            Assert.InRange(frame2[2], 250, 255);

            // Frame 3: Empty MapParticleRenderer PrepareRenderFrame clears previous capture validity.
            Assert.False(mapRenderer.PrepareRenderFrame(Array.Empty<MapParticleRuntime>(), viewProj, view, 64, 64));
            Assert.False(mapGlRenderer.HasValidColorCapture);

            // Frame 4: Ground custom material reading SceneColor still receives backdrop capture even without renderSceneDepth callback.
            var customGround = CustomEmitter("session-custom-ground", readColorPass, renderPass: -1, isGround: true);
            session.SetSystem(new VfxSystemModel
            {
                Name = "SessionCustomGround",
                TotalDuration = 1,
                Definition = new VfxSystemDefinition(12, "SessionCustomGround", "sessioncustomground.troybin", new[] { customGround })
            });
            session.Seek(0.1);
            session.ProcessPendingGpuState();
            SeedParticle(sessionShaders, customGround, false, (readColorPass, CompileParticleProgram(sessionShaders, readColorShader)));

            gl.ClearColor(0f, 0.7f, 0f, 1f);
            gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit);
            Assert.True(session.PrepareRenderFrame(viewProj, view));
            Assert.True(((IPreparedParticlePass)session).HasPreparedGroundPass);
            Assert.True(((IPreparedParticlePass)session).NeedsPreparedSceneCapture);

            byte[] afterGroundWithoutCallback = null;
            PreparedParticlePasses.Render(
                new IPreparedParticlePass[] { session },
                batches,
                renderCharacters: () => afterGroundWithoutCallback = ReadCenterPixel(gl, 64, 64));
            Assert.NotNull(afterGroundWithoutCallback);
            Assert.InRange(afterGroundWithoutCallback[0], 170, 186);
            Assert.InRange(afterGroundWithoutCallback[2], 250, 255);
            Assert.Equal(GLEnum.NoError, gl.GetError());
        }
        finally
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.DeleteFramebuffer(fbo);
            gl.DeleteRenderbuffer(depth);
            gl.DeleteTexture(target);
            gl.DeleteTexture(white);
        }
    }

    private static VfxEmitterDefinition CustomEmitter(string name, GameMaterialPass pass, int renderPass, bool isGround = false)
        => CustomEmitter(name, new[] { pass }, renderPass, isGround);

    private static VfxEmitterDefinition CustomEmitter(string name, IReadOnlyList<GameMaterialPass> passes, int renderPass, bool isGround = false)
        => new(name, VfxCurveF.Const(10), VfxCurveF.Const(1), null, 0, 0, false, false, 1,
            VfxCurve3.Const(Vector3.One), null, VfxCurve4.Const(Vector4.One), null, null, null, null,
            VfxCurve3.Const(Vector3.Zero), string.Empty, Vector2.One, 1, true, false,
            IsGroundLayer: isGround,
            RenderState: VfxEmitterRenderState.Default with { DisableBackfaceCull = true, RenderPass = renderPass },
            CustomMaterial: ModelMaterialDefinition.TextureOnly(name) with
            {
                Program = new GameMaterialProgram(GameMaterialKind.Particles, false, passes)
            });

    private static VfxEmitterDefinition StockQuadEmitter(string name, bool isGround = false, Vector4? color = null, int renderPass = -1, int blendMode = 1)
    {
        Vector4 c = color ?? Vector4.One;
        return new(name, VfxCurveF.Const(10), VfxCurveF.Const(1), null, 0, 0, false, false, blendMode,
            VfxCurve3.Const(Vector3.One * 2f), null, VfxCurve4.Const(c), null, null, null, null,
            VfxCurve3.Const(Vector3.Zero), string.Empty, Vector2.One, 1, true, false,
            IsGroundLayer: isGround, MiscRenderFlags: 1,
            RenderState: VfxEmitterRenderState.Default with { DisableBackfaceCull = true, AlphaReference = 0, RenderPass = renderPass });
    }

    private static VfxPlaybackRuntime.EmitterState MakeEmitterState(VfxEmitterDefinition def, Vector4? color = null)
    {
        Vector4 c = color ?? Vector4.One;
        var inst = new float[VfxPlaybackRuntime.InstanceStride];
        inst[3] = inst[4] = inst[18] = 2f;
        inst[5] = c.X;
        inst[6] = c.Y;
        inst[7] = c.Z;
        inst[8] = c.W;
        inst[36] = inst[40] = inst[44] = 1f;
        return new VfxPlaybackRuntime.EmitterState
        {
            Def = def,
            Instances = inst,
            InstanceCount = 1,
            IsVisible = true
        };
    }

    private static GameShaderTranslator.TranslatedProgram MakeScreenQuadShader(
        IReadOnlyList<(string Name, GameShaderTranslator.TextureDimension Dim)> textures,
        string pixelBody)
    {
        var bindings = new GameShaderTranslator.TextureBinding[textures.Count];
        for (int i = 0; i < textures.Count; i++)
            bindings[i] = new GameShaderTranslator.TextureBinding(textures[i].Name, textures[i].Dim,
                new[] { new GameShaderTranslator.SamplerBinding("s" + i, textures[i].Name) });

        GameShaderTranslator.TranslatedStage Stage(string source, bool isPixel) => new(source,
            new GameShaderTranslator.ShaderSidecar(Array.Empty<GameShaderTranslator.UniformBlock>(),
                isPixel ? bindings : Array.Empty<GameShaderTranslator.TextureBinding>(),
                Array.Empty<GameShaderTranslator.AttributeBinding>()),
            Array.Empty<GameShaderTranslator.AppliedPatch>(), Array.Empty<(string, string)>(), Array.Empty<uint>());

        return new GameShaderTranslator.TranslatedProgram(
            Stage("#version 300 es\nprecision highp float;\nvoid main() { vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2); gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0); }", false),
            Stage("#version 300 es\nprecision highp float;\n" + pixelBody, true));
    }

    private static GameShaderTranslator.TranslatedProgram MakeParticleQuadShader(
        IReadOnlyList<(string Name, GameShaderTranslator.TextureDimension Dim)> textures,
        string pixelBody)
    {
        var bindings = new GameShaderTranslator.TextureBinding[textures.Count];
        for (int i = 0; i < textures.Count; i++)
            bindings[i] = new GameShaderTranslator.TextureBinding(textures[i].Name, textures[i].Dim,
                new[] { new GameShaderTranslator.SamplerBinding("s" + i, textures[i].Name) });

        GameShaderTranslator.TranslatedStage Stage(string source, bool isPixel) => new(source,
            new GameShaderTranslator.ShaderSidecar(Array.Empty<GameShaderTranslator.UniformBlock>(),
                isPixel ? bindings : Array.Empty<GameShaderTranslator.TextureBinding>(),
                Array.Empty<GameShaderTranslator.AttributeBinding>()),
            Array.Empty<GameShaderTranslator.AppliedPatch>(), Array.Empty<(string, string)>(), Array.Empty<uint>());

        return new GameShaderTranslator.TranslatedProgram(
            Stage("#version 300 es\nprecision highp float;\n" +
                  "layout(location = 0) in vec2 vfxCorner;\n" +
                  "layout(location = 1) in vec3 vfxPos;\n" +
                  "void main() { gl_Position = vec4(vfxPos.xy + vfxCorner * 2.0, vfxPos.z, 1.0); }", false),
            Stage("#version 300 es\nprecision highp float;\n" + pixelBody, true));
    }

    private static object CompileParticleProgram(GameShaderRuntime runtime, GameShaderTranslator.TranslatedProgram translated)
        => typeof(GameShaderRuntime).GetMethod("CreateProgram", NonPublicFlags)!.Invoke(
            runtime, new object[] { translated, GameMaterialKind.Particles, true })!;

    private static void SeedParticle(GameShaderRuntime runtime, VfxEmitterDefinition emitter, bool mesh,
        params (GameMaterialPass Pass, object Linked)[] passEntries)
    {
        Type type = typeof(GameShaderRuntime);
        var entry = (ITuple)type.GetMethod("ParticleEntry", NonPublicFlags)!.Invoke(runtime, new object[] { emitter, mesh })!;
        Type passType = type.GetNestedType("PassRuntimeEntry", BindingFlags.NonPublic)!;
        Type globalsType = type.GetNestedType("PassGlobals", BindingFlags.NonPublic)!;
        Array passes = Array.CreateInstance(passType, passEntries.Length);
        for (int i = 0; i < passEntries.Length; i++)
        {
            object globals = Activator.CreateInstance(globalsType, NonPublicFlags, null, new object[] { passEntries[i].Pass }, null)!;
            object ready = Activator.CreateInstance(passType, NonPublicFlags, null, new object[] { i, passEntries[i].Linked, passEntries[i].Pass, globals }, null)!;
            passes.SetValue(ready, i);
        }
        object cache = Activator.CreateInstance(type.GetNestedType("CacheEntry", BindingFlags.NonPublic)!, NonPublicFlags, null, new object[] { passes, null }, null)!;
        ((IDictionary)type.GetField("_programs", NonPublicFlags)!.GetValue(runtime)!)[entry[1]!] = cache;
    }

    private static VfxOpenGlRenderer GetSessionRenderer(VfxRenderSession session)
        => (VfxOpenGlRenderer)typeof(VfxRenderSession).GetField("_renderer", NonPublicFlags)!.GetValue(session)!;

    private static GameShaderRuntime GetGameShaders(VfxOpenGlRenderer renderer)
        => (GameShaderRuntime)typeof(VfxOpenGlRenderer).GetField("_gameShaders", NonPublicFlags)!.GetValue(renderer)!;

    private static uint ProgramHandle(object linked)
        => (uint)linked.GetType().GetField("Program", NonPublicFlags)!.GetValue(linked)!;

    private static uint CreateColorTexture(GL gl, uint width, uint height)
    {
        uint tex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, tex);
        gl.TexImage2D<byte>(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, width, height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, ReadOnlySpan<byte>.Empty);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        return tex;
    }

    private static uint CreateSolidTexture(GL gl, byte r, byte g, byte b, byte a)
    {
        uint tex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, tex);
        gl.TexImage2D<byte>(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, 1, 1, 0, PixelFormat.Rgba, PixelType.UnsignedByte, new[] { r, g, b, a });
        return tex;
    }

    private static uint CreateDepthRenderbuffer(GL gl, uint width, uint height)
    {
        uint rb = gl.GenRenderbuffer();
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, rb);
        gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24, width, height);
        return rb;
    }

    private static uint CreateDepthStencilRenderbuffer(GL gl, uint width, uint height)
    {
        uint rb = gl.GenRenderbuffer();
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, rb);
        gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.Depth24Stencil8, width, height);
        return rb;
    }

    private static uint CreateFramebuffer(GL gl, uint colorTex, uint depthRb, bool stencil = false)
    {
        uint fbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, colorTex, 0);
        gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer,
            stencil ? FramebufferAttachment.DepthStencilAttachment : FramebufferAttachment.DepthAttachment,
            RenderbufferTarget.Renderbuffer, depthRb);
        Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
        return fbo;
    }

    private static byte[] ReadCenterPixel(GL gl, int width, int height)
    {
        byte[] pixel = new byte[4];
        gl.ReadPixels<byte>(width / 2, height / 2, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, pixel);
        return pixel;
    }
}
