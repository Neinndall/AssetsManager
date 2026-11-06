using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Services.Viewer.Rendering.Core;
using AssetsManager.Services.Viewer.Rendering.GameShaders;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Services.Viewer.Vfx.Semantics;
using AssetsManager.Tests.Support;
using AssetsManager.Tests.xUnit.Services.Viewer.Vfx;
using AssetsManager.Utils.Rendering;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering;

[Collection("Viewport native graphics")]
public sealed class VfxStencilGpuTests
{
    public static IEnumerable<object[]> CustomSamplerCases()
    {
        foreach (VfxPrimitiveKind primitive in new[] { VfxPrimitiveKind.CameraQuad, VfxPrimitiveKind.Mesh, VfxPrimitiveKind.AttachedMesh })
        for (int addressing = 0; addressing < 4; addressing++)
        foreach (bool linear in new[] { false, true })
        foreach (bool sharedTexture in new[] { false, true })
            yield return new object[] { primitive, addressing, linear, sharedTexture };
    }

    [Theory]
    [MemberData(nameof(CustomSamplerCases))]
    public void CustomPassSamplersKeepIndependentAddressingWithoutMutatingSharedTextures(
        VfxPrimitiveKind primitive, int addressing, bool linear, bool sharedTexture)
    {
        using var frame = new Frame(true);
        bool mesh = primitive != VfxPrimitiveKind.CameraQuad;
        var emitter = frame.Emitter(0, 0, new(.4f, .4f, .4f, 1), .8f, primitive);
        emitter.Instances[19] = .9f;
        var original = emitter.Def;
        var program = GameParticleProgramResolver.Create(original, mesh);
        var values = new Dictionary<string, Vector4>();
        VfxShaderParameterUtils.PopulateNativeParameters(values, original, 0);
        var parameters = new List<GameMaterialParameter>();
        foreach (var pair in values) parameters.Add(new(pair.Key, pair.Value, GameMaterialParamSource.Material));
        byte[] pixels = new byte[4 * 4 * 4], otherPixels = new byte[pixels.Length];
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++)
        {
            int at = (y * 4 + x) * 4;
            pixels[at] = (byte)(40 + x * 50); pixels[at + 1] = (byte)(40 + y * 50);
            pixels[at + 2] = 90; pixels[at + 3] = 255;
            otherPixels[at] = (byte)(60 + x * 40); otherPixels[at + 1] = (byte)(150 - y * 30);
            otherPixels[at + 2] = 160; otherPixels[at + 3] = 255;
        }
        uint texture = frame.Texture(4, pixels), other = sharedTexture ? texture : frame.Texture(4, otherPixels);
        emitter.ProgramTextures["first.tex"] = texture;
        emitter.ProgramTextures["second.tex"] = other;
        var samplerA = new GameMaterialSamplerState(addressing == 2 ? "Clamp" : addressing == 3 ? "Wrap" : "",
            addressing == 1 ? MapTextureWrap.Mirror : addressing == 3 ? MapTextureWrap.Clamp : MapTextureWrap.Repeat,
            MapTextureWrap.Repeat, MapTextureWrap.Repeat, linear, linear);
        var samplerB = new GameMaterialSamplerState(addressing == 2 ? "Wrap" : addressing == 3 ? "Clamp" : "",
            addressing == 3 ? MapTextureWrap.Repeat : MapTextureWrap.Clamp,
            MapTextureWrap.Clamp, MapTextureWrap.Clamp, linear, linear);
        GameMaterialPass Pass(string path, GameMaterialSamplerState sampler, bool blend) => program.Passes[0] with
        {
            Parameters = parameters,
            Textures = new[] { new GameMaterialTexture("TEXTURE", new MapTextureReference(path, 0), GameMaterialTextureSource.Material, sampler) },
            State = GameMaterialPassState.Default with { CullEnabled = false, DepthEnabled = false, WriteMask = 15,
                BlendEnabled = blend, SourceColor = MapBlendFactor.One, DestinationColor = MapBlendFactor.One,
                SourceAlpha = MapBlendFactor.One, DestinationAlpha = MapBlendFactor.Zero }
        };
        var first = Pass("first.tex", samplerA, false);
        var second = Pass("second.tex", samplerB, true);
        try
        {
            for (int repeat = 0; repeat < 2; repeat++)
            {
                frame.Clear();
                Draw(first);
                byte[] firstControl = frame.Pixel(32);
                frame.Clear();
                Draw(second);
                byte[] secondControl = frame.Pixel(32);
                Assert.True(Math.Abs(firstControl[0] - secondControl[0]) > 3,
                    "The controlled samplers must read visibly different texels.");
                frame.Clear();
                Draw(first); Draw(second);
                byte[] expected = frame.Pixel(32);
                frame.Clear();
                Draw(first, second);
                Assert.Equal((2, 2), frame.Renderer.LastParticleDrawCounts);
                Assert.Equal(expected, frame.Pixel(32));
                frame.Clear();
                Draw(first);
                Assert.Equal(firstControl, frame.Pixel(32));
                foreach (uint held in new[] { texture, other })
                {
                    frame.Gl.ActiveTexture(TextureUnit.Texture0);
                    frame.Gl.BindTexture(TextureTarget.Texture2D, held);
                    frame.Gl.GetTexParameter(TextureTarget.Texture2D, GLEnum.TextureMagFilter, out int filter);
                    frame.Gl.GetTexParameter(TextureTarget.Texture2D, GLEnum.TextureMinFilter, out int minFilter);
                    frame.Gl.GetTexParameter(TextureTarget.Texture2D, GLEnum.TextureWrapS, out int wrap);
                    frame.Gl.GetTexParameter(TextureTarget.Texture2D, GLEnum.TextureWrapT, out int wrapT);
                    Assert.Equal((int)TextureMagFilter.Nearest, filter);
                    Assert.Equal((int)TextureMinFilter.Nearest, minFilter);
                    Assert.Equal((int)TextureWrapMode.Repeat, wrap);
                    Assert.Equal((int)TextureWrapMode.Repeat, wrapT);
                }
            }
        }
        finally { frame.Gl.DeleteTexture(texture); if (other != texture) frame.Gl.DeleteTexture(other); }

        void Draw(params GameMaterialPass[] passes)
        {
            emitter.Def = original with { CustomMaterial = ModelMaterialDefinition.Default with
                { BindingKind = ModelMaterialBindingKind.Authored, AlphaCutoff = 0, Program = program with { Passes = passes } } };
            frame.Draw(emitter);
            string fallback = frame.Renderer.GameParticleFallback(emitter, mesh);
            Assert.True(fallback is null, fallback);
        }
    }

    public static IEnumerable<object[]> CustomPassCases()
    {
        foreach (VfxPrimitiveKind primitive in new[] { VfxPrimitiveKind.CameraQuad, VfxPrimitiveKind.Mesh, VfxPrimitiveKind.AttachedMesh })
        foreach (bool reverse in new[] { false, true })
        for (int mask = 0; mask < 3; mask++)
        foreach (bool additive in new[] { false, true })
        foreach (bool legacyWarp in new[] { false, true })
            yield return new object[] { primitive, reverse, mask, additive, legacyWarp };
    }

    [Theory]
    [MemberData(nameof(CustomPassCases))]
    public void CustomPassesKeepTheirBlendDepthAndWriteStateAndDoNotAffectStockDraws(
        VfxPrimitiveKind primitive, bool reverse, int mask, bool additive, bool legacyWarp)
    {
        using var frame = new Frame(true);
        bool mesh = primitive != VfxPrimitiveKind.CameraQuad;
        var emitter = frame.Emitter(0, 0, new(.4f, .6f, .8f, .5f), .8f, primitive);
        VfxEmitterDefinition original = emitter.Def;
        GameMaterialProgram program = GameParticleProgramResolver.Create(original, mesh);
        // A controlled custom material owns its parameters and texture instead of borrowing emitter aliases.
        var values = new Dictionary<string, Vector4>();
        VfxShaderParameterUtils.PopulateNativeParameters(values, original, 0);
        var parameters = new List<GameMaterialParameter>();
        foreach (var pair in values) parameters.Add(new(pair.Key, pair.Value, GameMaterialParamSource.Material));
        program = program with { Passes = new[] { program.Passes[0] with
        {
            Parameters = parameters,
            Textures = new[] { new GameMaterialTexture("TEXTURE", new MapTextureReference("controlled.tex", 0),
                GameMaterialTextureSource.Material, null) }
        } } };
        emitter.ProgramTextures["controlled.tex"] = emitter.Texture;
        var opaque = program.Passes[0] with { State = GameMaterialPassState.Default with
            { CullEnabled = false, DepthEnabled = true, DepthCompareFunc = 7, WriteMask = mask == 1 ? 16u : 31u } };
        var blended = program.Passes[0] with { State = GameMaterialPassState.Default with
        {
            CullEnabled = false, DepthEnabled = true, DepthCompareFunc = 3,
            BlendEnabled = true, SourceColor = additive ? MapBlendFactor.One : MapBlendFactor.SourceAlpha,
            DestinationColor = additive ? MapBlendFactor.One : MapBlendFactor.OneMinusSourceAlpha,
            SourceAlpha = MapBlendFactor.One, DestinationAlpha = MapBlendFactor.OneMinusSourceAlpha,
            WriteMask = mask == 2 ? 0u : 15u
        } };
        var stock = frame.Emitter(0, 0, new(0, 1, 0, 1), .8f, primitive);
        stock.Def = stock.Def with { BlendMode = 1,
            RenderState = stock.Def.RenderState with { RenderPass = original.RenderState.RenderPass + 1 } };
        frame.Draw(stock);
        byte[] stockControl = frame.Pixel(32);
        Assert.True(stockControl[1] > 0);

        for (int repeat = 0; repeat < 2; repeat++)
        {
            frame.Clear();
            emitter.Def = Definition(new[] { opaque }, false);
            frame.Draw(emitter);
            Assert.Equal((1, 1), frame.Renderer.LastParticleDrawCounts);
            emitter.Def = Definition(new[] { blended }, false);
            frame.Draw(emitter);
            Assert.Equal((1, 1), frame.Renderer.LastParticleDrawCounts);
            byte[] expected = frame.Pixel(32);
            Assert.True(expected[0] + expected[1] + expected[2] > 0);
            float expectedDepth = Depth();
            // Attached geometry retains its owner placement instead of the particle's translation.
            float planeDepth = primitive == VfxPrimitiveKind.AttachedMesh ? .5f : .6f;
            Assert.InRange(Math.Abs(expectedDepth - planeDepth), 0, .00001f);

            frame.Clear();
            emitter.Def = Definition(reverse ? new[] { blended, opaque } : new[] { opaque, blended }, legacyWarp);
            frame.Draw(emitter);
            Assert.Null(frame.Renderer.GameParticleFallback(emitter, mesh));
            Assert.Equal((2, 2), frame.Renderer.LastParticleDrawCounts);
            Assert.Equal(expected, frame.Pixel(32));
            Assert.InRange(Math.Abs(Depth() - expectedDepth), 0, .00001f);
            frame.Draw(stock);
            Assert.Equal(stockControl, frame.Pixel(32));

            frame.Clear();
            frame.Draw(emitter, stock);
            Assert.Equal((3, 3), frame.Renderer.LastParticleDrawCounts);
            Assert.Equal(stockControl, frame.Pixel(32));
        }

        VfxEmitterDefinition Definition(GameMaterialPass[] passes, bool warp) => original with
        {
            Distortion = warp ? new VfxDistortionDefinition(.25f, 1, "normal.tex") : null,
            CustomMaterial = ModelMaterialDefinition.Default with
            {
                BindingKind = ModelMaterialBindingKind.Authored, AlphaCutoff = 0,
                Program = program with { Passes = passes },
                RenderState = ModelMaterialRenderState.Default with { DoubleSided = true }
            }
        };
        float Depth()
        {
            Span<float> pixel = stackalloc float[1];
            frame.Gl.ReadPixels(32, 32, 1, 1, PixelFormat.DepthComponent, PixelType.Float, pixel);
            return pixel[0];
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ResolvedCustomMaterialUsesColourWhileMissingMaterialKeepsLegacyWarp(bool native, bool missing)
    {
        using var frame = new Frame(native);
        var emitter = frame.Emitter(0, 0, new(0, 1, 0, 1), 2);
        var colorDefinition = emitter.Def;
        GameMaterialProgram program = GameParticleProgramResolver.Create(colorDefinition, false);
        program = program with
        {
            Passes = new[] { program.Passes[0] with
                { State = GameMaterialPassState.Default with { CullEnabled = false, DepthEnabled = false, WriteMask = 15 } } }
        };
        var material = ModelMaterialDefinition.Default with
        {
            BindingKind = missing ? ModelMaterialBindingKind.Missing : ModelMaterialBindingKind.Authored,
            AlphaCutoff = 0, Program = program,
            RenderState = ModelMaterialRenderState.Default with { DoubleSided = true, DepthTest = false, DepthWrite = false }
        };
        var warp = new VfxDistortionDefinition(.25f, 1, "normal.tex");
        emitter.DistortionTexture = emitter.Texture;
        VfxEmitterDefinition reference = missing
            ? colorDefinition with { Distortion = warp }
            : colorDefinition with { CustomMaterial = material };
        byte[] expected = Draw(reference);
        var combined = colorDefinition with { CustomMaterial = material, Distortion = warp };
        Assert.Equal(missing, combined.DrawsAsDistortion);
        Assert.Equal(expected, Draw(combined));
        // A resolved custom program owns its tint parameters; its colour need not match the stock material's intensity.
        Assert.True(frame.Pixel(32)[1] > 0);
        if (missing) Assert.InRange((int)frame.Pixel(32)[1], 126, 129);
        Assert.Equal(0, frame.Pixel(32)[2]);
        if (native) Assert.Null(frame.Renderer.GameParticleFallback(emitter, false));

        byte[] Draw(VfxEmitterDefinition definition)
        {
            emitter.Def = definition;
            frame.Clear();
            frame.Gl.ClearColor(.25f, .5f, .75f, .6f);
            frame.Gl.Clear(ClearBufferMask.ColorBufferBit);
            frame.Renderer.CaptureScene(64, 64, true, false);
            frame.Draw(emitter);
            var row = new byte[64 * 4];
            frame.Gl.ReadPixels(0, 32, 64, 1, PixelFormat.Rgba, PixelType.UnsignedByte, row.AsSpan());
            return row;
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CustomMaterialResolutionControlsEmitterStencilInNativeAndStockDraws(bool native, bool missing)
    {
        using var frame = new Frame(native);
        var writer = frame.Emitter(1, 5, new(1, 0, 0, 1), .4f);
        var custom = frame.Emitter(2, 5, new(0, 1, 0, 1), 2);
        // A controlled custom pass uses installed particle bytecode, so the check exercises
        // the custom binding path without depending on a particular skin's material layout.
        GameMaterialProgram program = GameParticleProgramResolver.Create(custom.Def, false);
        var pass = program.Passes[0] with { State = GameMaterialPassState.Default with
            { CullEnabled = false, DepthEnabled = false, WriteMask = 15 } };
        program = program with { Passes = new[] { pass, pass } };
        custom.Def = custom.Def with
        {
            CustomMaterial = ModelMaterialDefinition.Default with
            {
                BindingKind = missing ? ModelMaterialBindingKind.Missing : ModelMaterialBindingKind.Authored,
                AlphaCutoff = 0,
                Program = program,
                RenderState = ModelMaterialRenderState.Default with { DoubleSided = true, DepthTest = false }
            }
        };
        frame.Draw(writer, custom);
        Assert.True(frame.Pixel(32)[1] > 0);
        Assert.Equal(missing, frame.Pixel(2)[1] == 0);
        Assert.Equal(197, frame.Stencil(32));
        Assert.Equal(192, frame.Stencil(2));
        Assert.Equal(native && !missing ? 3 : 2, frame.Renderer.LastParticleDrawCounts.Draws);
        if (native) Assert.Null(frame.Renderer.GameParticleFallback(custom, false));

        frame.Clear();
        custom.Def = custom.Def with { RenderState = custom.Def.RenderState with { StencilMode = 1 } };
        frame.Draw(custom);
        Assert.True(frame.Pixel(2)[1] > 0);
        Assert.Equal(missing ? 197 : 192, frame.Stencil(32));
        Assert.Equal(missing ? 197 : 192, frame.Stencil(2));
    }

    public static IEnumerable<object[]> Primitives()
    {
        foreach (var kind in new[] { VfxPrimitiveKind.CameraQuad, VfxPrimitiveKind.Mesh,
            VfxPrimitiveKind.AttachedMesh, VfxPrimitiveKind.CameraTrail, VfxPrimitiveKind.ArbitraryTrail,
            VfxPrimitiveKind.Beam, VfxPrimitiveKind.CameraSegmentBeam, VfxPrimitiveKind.PlanarProjection })
            foreach (bool native in new[] { false, true }) yield return new object[] { kind, native };
    }

    [Theory]
    [MemberData(nameof(Primitives))]
    public void EveryPrimitiveWritesAMaskThatClipsAnotherEmitter(VfxPrimitiveKind kind, bool native)
    {
        using var frame = new Frame(native, kind == VfxPrimitiveKind.PlanarProjection);
        var writer = frame.Emitter(1, 127, Vector4.UnitX + Vector4.UnitW, .4f, kind);
        var tester = frame.Emitter(2, 63, Vector4.UnitY + Vector4.UnitW, 2);
        // Attached meshes and ground projections are at different depths from the billboard.
        // This check isolates their stencil coverage; depth failure has its own regression below.
        tester.Def = tester.Def with { MiscRenderFlags = 1 };
        frame.Draw(writer, tester);
        Assert.Equal(255, frame.Stencil(32));
        Assert.Equal(255, frame.Pixel(32)[1]);
        Assert.Equal(0, frame.Pixel(2)[1]);
        Assert.Equal(192, frame.Stencil(2));
        if (native && kind != VfxPrimitiveKind.PlanarProjection)
            Assert.Null(frame.Renderer.GameParticleFallback(writer, writer.Def.IsMeshPrimitive));
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    public void ComparisonControlsColorAndMode4WritesOnlyWhereDifferent(byte mode, bool inside)
    {
        using var frame = new Frame(false);
        var writer = frame.Emitter(1, 5, new(1, 0, 0, 1), .4f);
        var tester = frame.Emitter(mode, 5, new(0, 1, 0, 1), 2);
        frame.Draw(writer, tester);
        Assert.Equal(inside ? 255 : 0, frame.Pixel(32)[1]);
        Assert.Equal(inside ? 0 : 255, frame.Pixel(2)[1]);
        Assert.Equal(inside ? 0 : 255, frame.Pixel(32)[0]);
        Assert.Equal(mode == 4 ? 197 : 192, frame.Stencil(2));
        Assert.Equal(197, frame.Stencil(32));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void FailedAlphaOrDepthDoesNotWriteStencil(bool native, bool depthFailure)
    {
        using var frame = new Frame(native);
        var writer = frame.Emitter(1, 5, new(1, 0, 0, depthFailure ? 1 : 0), .4f);
        writer.Def = writer.Def with { BlendMode = 1, RenderState = writer.Def.RenderState with { AlphaReference = 128 } };
        if (depthFailure) frame.Clear(depth: .1f);
        frame.Draw(writer);
        Assert.Equal(192, frame.Stencil(32));
        Assert.Equal(0, frame.Pixel(32)[0]);
        if (native) Assert.Null(frame.Renderer.GameParticleFallback(writer, false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HiddenOrRemovedWriterUsesPreviewFallbackButVisibleEmptyWriterStillCounts(bool hidden)
    {
        using var frame = new Frame(false);
        var writer = frame.Emitter(1, 5, new(1, 0, 0, 1), .4f);
        var tester = frame.Emitter(2, 5, new(0, 1, 0, 1), 2);
        writer.IsVisible = !hidden;
        writer.InstanceCount = 0;
        frame.Draw(writer, tester);
        Assert.Equal(hidden ? 255 : 0, frame.Pixel(32)[1]);
        frame.Clear();
        frame.Draw(tester);
        Assert.Equal(255, frame.Pixel(32)[1]);
    }

    [Fact]
    public void Mode4RejectsItsOwnSecondDrawAndPlainEmittersIgnorePreviousMasks()
    {
        using var frame = new Frame(false);
        var writer = frame.Emitter(4, 5, new(.25f, 0, 0, 1), .4f);
        writer.Def = writer.Def with { BlendMode = 0 };
        var scene = new VfxStencilScene();
        scene.Add(writer.Def, true);
        using (frame.Renderer.BeginRenderBatch(scene))
        {
            frame.Draw(writer);
            frame.Draw(writer);
            Assert.InRange(frame.Pixel(32)[0], 62, 65);
            var plain = frame.Emitter(0, 5, new(0, 1, 0, 1), 2);
            frame.Draw(plain);
            Assert.Equal(255, frame.Pixel(32)[1]);
            Assert.Equal(255, frame.Pixel(2)[1]);
        }
        Assert.Equal(197, frame.Stencil(32));
    }

    [Fact]
    public void SharedSceneReferencesAndMasksSurviveAcrossOwnersAndPhases()
    {
        using var frame = new Frame(false);
        using var other = new VfxOpenGlRenderer();
        other.Initialize(frame.Gl);
        var writer = frame.Emitter(1, 7, new(1, 0, 0, 1), .4f);
        writer.Def = writer.Def with { RenderState = writer.Def.RenderState with { StencilReferenceId = 0x1234 } };
        var tester = frame.Emitter(2, 9, new(0, 1, 0, 1), 2);
        tester.Def = tester.Def with { RenderState = tester.Def.RenderState with { StencilReferenceId = 0x1234 } };
        var passes = new IPreparedParticlePass[]
        {
            new ParticlePass(frame.Renderer, writer, false), new ParticlePass(other, tester, true)
        };
        PreparedParticlePasses.Render(passes, new List<IDisposable>());
        Assert.Equal(255, frame.Pixel(32)[1]);
        Assert.Equal(0, frame.Pixel(2)[1]);
        Assert.Equal(255, frame.Stencil(32));
        frame.Clear();
        PreparedParticlePasses.Render(new IPreparedParticlePass[] { passes[1] }, new List<IDisposable>());
        Assert.Equal(255, frame.Pixel(2)[1]);
    }

    [Fact]
    public void RestoresDistinctFrontAndBackStencilStateAtBatchBoundary()
    {
        using var frame = new Frame(false);
        var gl = frame.Gl;
        gl.Enable(EnableCap.StencilTest);
        gl.StencilFuncSeparate(TriangleFace.Front, StencilFunction.Less, 8, 0xa5);
        gl.StencilFuncSeparate(TriangleFace.Back, StencilFunction.Greater, 9, 0x5a);
        gl.StencilMaskSeparate(TriangleFace.Front, 0x11);
        gl.StencilMaskSeparate(TriangleFace.Back, 0x22);
        gl.StencilOpSeparate(TriangleFace.Front, StencilOp.Incr, StencilOp.Decr, StencilOp.Invert);
        gl.StencilOpSeparate(TriangleFace.Back, StencilOp.Zero, StencilOp.Replace, StencilOp.Keep);
        var names = new[] { GLEnum.StencilFunc, GLEnum.StencilRef, GLEnum.StencilValueMask, GLEnum.StencilWritemask,
            GLEnum.StencilFail, GLEnum.StencilPassDepthFail, GLEnum.StencilPassDepthPass,
            GLEnum.StencilBackFunc, GLEnum.StencilBackRef, GLEnum.StencilBackValueMask, GLEnum.StencilBackWritemask,
            GLEnum.StencilBackFail, GLEnum.StencilBackPassDepthFail, GLEnum.StencilBackPassDepthPass };
        int[] before = Array.ConvertAll(names, name => gl.GetInteger(name));
        using (frame.Renderer.BeginRenderBatch())
        {
            frame.Draw(frame.Emitter(1, 5, new(1, 0, 0, 1), .4f));
            Assert.Equal(0x3f, gl.GetInteger(GLEnum.StencilWritemask));
            Assert.Equal(0x3f, gl.GetInteger(GLEnum.StencilBackWritemask));
        }
        Assert.True(gl.IsEnabled(EnableCap.StencilTest));
        Assert.Equal(before, Array.ConvertAll(names, name => gl.GetInteger(name)));
    }

    [Fact]
    public void WireframeNeverWritesStencil()
    {
        using var frame = new Frame(false);
        var writer = frame.Emitter(1, 5, new(1, 0, 0, 1), 2);
        frame.Renderer.Render(new[] { new VfxRenderQueueEntry(writer, 0, 0) }, frame.ViewProjection, frame.View, true);
        for (int x = 0; x < 64; x++) Assert.Equal(192, frame.Stencil(x));
    }

    public static IEnumerable<object[]> PatternMaskCases()
    {
        foreach (byte writerMode in new byte[] { 1, 4 })
        foreach (byte reference in new byte[] { 0, 5, 63, 127 })
        foreach (byte testerMode in new byte[] { 2, 3, 4 })
        foreach (bool native in new[] { false, true })
            yield return new object[] { writerMode, reference, testerMode, native };
    }

    [Theory]
    [MemberData(nameof(PatternMaskCases))]
    public void AlphaCutoutMasksKeepTheirHolesAndClearBetweenFrames(byte writerMode, byte reference, byte testerMode, bool native)
    {
        using var frame = new Frame(native);
        GL gl = frame.Gl;
        uint pattern = gl.GenTexture();
        try
        {
            gl.BindTexture(TextureTarget.Texture2D, pattern);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            var writer = frame.Emitter(writerMode, reference, Vector4.One, 1);
            writer.Texture = pattern;
            writer.TextureWidth = 4;
            writer.Def = writer.Def with
            {
                RenderState = writer.Def.RenderState with { AlphaReference = 128 }
            };
            var tester = frame.Emitter(testerMode, reference, new(0, 1, 0, 1), 1);
            for (int currentFrame = 0; currentFrame < 2; currentFrame++)
            {
                // The second frame swaps transparent and opaque stripes on the same texture and framebuffer.
                byte[] texels = new byte[16];
                for (int stripe = 0; stripe < 4; stripe++)
                {
                    texels[stripe * 4] = 255;
                    texels[stripe * 4 + 3] = (stripe + currentFrame) % 2 == 1 ? (byte)255 : (byte)0;
                }
                gl.ActiveTexture(TextureUnit.Texture0);
                gl.BindTexture(TextureTarget.Texture2D, pattern);
                gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, 4, 1, 0,
                    PixelFormat.Rgba, PixelType.UnsignedByte, texels.AsSpan());
                frame.Clear();
                frame.Draw(writer, tester);
                int maskedReference = reference & 63;
                for (int stripe = 0; stripe < 4; stripe++)
                {
                    int x = stripe * 16 + 8;
                    bool opaque = (stripe + currentFrame) % 2 == 1;
                    bool writerPasses = opaque && (writerMode == 1 || maskedReference != 0);
                    int stencilBeforeTest = writerPasses ? maskedReference : 0;
                    bool testerPasses = testerMode == 2
                        ? stencilBeforeTest == maskedReference : stencilBeforeTest != maskedReference;
                    int finalStencil = testerMode == 4 && testerPasses ? maskedReference : stencilBeforeTest;
                    byte[] pixel = frame.Pixel(x);
                    Assert.Equal(testerPasses ? 255 : 0, pixel[1]);
                    Assert.Equal(!testerPasses && writerPasses ? 255 : 0, pixel[0]);
                    Assert.Equal(0, pixel[2]);
                    Assert.Equal(192 | finalStencil, frame.Stencil(x));
                }
                if (native) Assert.Null(frame.Renderer.GameParticleFallback(writer, false));
            }
        }
        finally { gl.DeleteTexture(pattern); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SceneDepthPrepassCopiesStencilWithoutChangingTheVisibleMask(bool scissor, bool fail)
    {
        using var frame = new Frame(false);
        GL gl = frame.Gl;
        using var prepass = new GlSceneDepthPass(gl);
        frame.Draw(frame.Emitter(1, 5, new(1, 0, 0, 1), .4f));
        var before = new byte[64];
        for (int x = 0; x < before.Length; x++) before[x] = frame.Stencil(x);
        gl.StencilFuncSeparate(TriangleFace.Front, StencilFunction.Never, 8, 0xa5);
        gl.StencilFuncSeparate(TriangleFace.Back, StencilFunction.Greater, 9, 0x5a);
        gl.StencilMaskSeparate(TriangleFace.Front, 0x05);
        gl.StencilMaskSeparate(TriangleFace.Back, 0x0a);
        gl.Enable(EnableCap.StencilTest);
        gl.Scissor(16, 0, 32, 64);
        if (scissor) gl.Enable(EnableCap.ScissorTest);
        else gl.Disable(EnableCap.ScissorTest);
        int framebuffer = gl.GetInteger(GLEnum.DrawFramebufferBinding);
        Action render = () => prepass.Render(64, 64, () =>
        {
            for (int x = 0; x < before.Length; x++) Assert.Equal(before[x], frame.Stencil(x));
            frame.Draw(frame.Emitter(1, 9, new(0, 0, 1, 1), 1));
            Assert.Equal(201, frame.Stencil(32));
            Assert.Equal(255, frame.Pixel(32)[2]);
        }, () =>
        {
            if (fail) throw new InvalidOperationException("controlled capture failure");
        });
        if (fail) Assert.Throws<InvalidOperationException>(render);
        else render();
        Assert.Equal(framebuffer, gl.GetInteger(GLEnum.DrawFramebufferBinding));
        Assert.Equal(framebuffer, gl.GetInteger(GLEnum.ReadFramebufferBinding));
        for (int x = 0; x < before.Length; x++) Assert.Equal(before[x], frame.Stencil(x));
        Assert.Equal(255, frame.Pixel(32)[0]);
        Assert.Equal(0, frame.Pixel(32)[2]);
        Assert.Equal(scissor, gl.IsEnabled(EnableCap.ScissorTest));
        Assert.True(gl.IsEnabled(EnableCap.StencilTest));
        Assert.Equal(0x05, gl.GetInteger(GLEnum.StencilWritemask));
        Assert.Equal(0x0a, gl.GetInteger(GLEnum.StencilBackWritemask));
        Assert.Equal((int)StencilFunction.Never, gl.GetInteger(GLEnum.StencilFunc));
        Assert.Equal((int)StencilFunction.Greater, gl.GetInteger(GLEnum.StencilBackFunc));
    }

    private sealed class ParticlePass(VfxOpenGlRenderer renderer, VfxPlaybackRuntime.EmitterState emitter,
        bool post) : IPreparedParticlePass
    {
        private VfxStencilScene _scene;
        public void PrepareStencilScene(VfxStencilScene scene) { scene.Add(emitter.Def, emitter.IsVisible); _scene = scene; }
        public IDisposable BeginPreparedRenderBatch() => renderer.BeginRenderBatch(_scene);
        private void Draw() => renderer.Render(new[] { new VfxRenderQueueEntry(emitter, 0, 0) }, Matrix4x4.Identity, Matrix4x4.Identity);
        public void RenderPreparedColorPass() { if (!post) Draw(); }
        public void RenderPreparedPostColorPass() { if (post) Draw(); }
        public void CapturePreparedDistortionFrame() { }
        public void RenderPreparedDistortionPass() { }
    }

    private sealed class Frame : IDisposable
    {
        private readonly HiddenWglContext _context = new();
        internal readonly GL Gl;
        internal readonly VfxOpenGlRenderer Renderer = new();
        internal readonly Matrix4x4 View, ViewProjection;
        private readonly uint _target, _white, _frame, _depth;
        internal Frame(bool native, bool projection = false)
        {
            Gl = GL.GetApi(_context.GetProcAddress);
            string install = native ? InstalledSkins.FindInstall() : null;
            if (native) Assert.NotNull(install);
            Renderer.Initialize(Gl, native ? InstalledSkins.Settings(install) : null);
            _target = Texture(64, new byte[64 * 64 * 4]);
            _white = Texture(1, new byte[] { 255, 255, 255, 255 });
            _depth = Gl.GenRenderbuffer();
            Gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depth);
            Gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.Depth24Stencil8, 64, 64);
            _frame = Gl.GenFramebuffer();
            Gl.BindFramebuffer(FramebufferTarget.Framebuffer, _frame);
            Gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, _target, 0);
            Gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment,
                RenderbufferTarget.Renderbuffer, _depth);
            Assert.Equal(GLEnum.FramebufferComplete, Gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
            View = projection ? Matrix4x4.CreateLookAt(new(0, 3, 0), Vector3.Zero, -Vector3.UnitZ) : Matrix4x4.Identity;
            ViewProjection = projection ? View * Matrix4x4.CreateOrthographic(2, 2, .1f, 10) : Matrix4x4.Identity;
            Gl.Viewport(0, 0, 64, 64);
            Clear();
        }

        internal void Clear(float depth = 1)
        {
            Gl.ColorMask(true, true, true, true);
            Gl.DepthMask(true);
            Gl.StencilMask(0xff);
            Gl.ClearColor(0, 0, 0, 0);
            Gl.ClearDepth(depth);
            // Keep the upper bits populated to check that particles own only the lower six.
            Gl.ClearStencil(192);
            Gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit);
        }

        internal VfxPlaybackRuntime.EmitterState Emitter(byte mode, byte reference, Vector4 color, float size,
            VfxPrimitiveKind kind = VfxPrimitiveKind.CameraQuad)
        {
            bool mesh = kind is VfxPrimitiveKind.Mesh or VfxPrimitiveKind.AttachedMesh;
            bool trail = kind is VfxPrimitiveKind.CameraTrail or VfxPrimitiveKind.ArbitraryTrail;
            var def = VfxStencilSceneTests.Definition(mode, reference) with
            {
                PrimitiveKind = kind, IsMeshPrimitive = mesh,
                Trail = trail ? new VfxTrailDefinition(VfxCurve3.Const(Vector3.One), 0, 0, 0, 0) : null,
                Beam = kind is VfxPrimitiveKind.Beam or VfxPrimitiveKind.CameraSegmentBeam ?
                    new VfxBeamDefinition(0, 0, 2, VfxCurve3.Const(Vector3.One), VfxCurve4.Const(Vector4.One),
                        false, Vector3.Zero, Vector3.Zero) : null
            };
            int count = trail || kind == VfxPrimitiveKind.CameraSegmentBeam ? 2 : 1;
            var instances = new float[count * VfxPlaybackRuntime.InstanceStride];
            var emitter = new VfxPlaybackRuntime.EmitterState
            {
                Def = def, Texture = _white, TextureWidth = 1, TextureHeight = 1,
                SystemOrigin = new(-.75f, 0, .2f), SystemTarget = new(.75f, 0, .2f),
                SystemOrientation = Matrix4x4.Identity, InstanceCount = count
            };
            for (int index = 0; index < count; index++)
            {
                int at = index * VfxPlaybackRuntime.InstanceStride;
                instances[at] = trail ? (index == 0 ? -.75f : .75f) : 0;
                instances[at + 2] = .2f;
                instances[at + 3] = size;
                instances[at + 4] = instances[at + 18] = size;
                instances[at + 5] = color.X; instances[at + 6] = color.Y;
                instances[at + 7] = color.Z; instances[at + 8] = color.W;
                instances[at + 21] = instances[at + 22] = instances[at + 31] = instances[at + 32] = 1;
                instances[at + 36] = instances[at + 40] = instances[at + 44] = 1;
                if (kind == VfxPrimitiveKind.CameraSegmentBeam)
                {
                    instances[at] = emitter.SystemOrigin.X;
                    instances[at + 1] = emitter.SystemOrigin.Y;
                    instances[at + 2] = emitter.SystemOrigin.Z;
                }
                emitter.Particles.Add(new VfxPlaybackRuntime.Particle
                {
                    Serial = (uint)index, Ordinal = (ushort)index, Life = 1, BirthFrame = Matrix4x4.Identity, TrailTiling = new(2, 1, 0),
                    BirthRotation = kind == VfxPrimitiveKind.ArbitraryTrail ? new(0, 0, MathF.PI / 2) : Vector3.Zero
                });
            }
            emitter.Instances = instances;
            if (mesh) Renderer.UploadEmitterMesh(emitter, new float[] { -1, -1, 0, 1, -1, 0, 0, 1, 0 },
                new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 }, new float[] { 0, 0, 1, 0, .5f, 1 }, null);
            return emitter;
        }

        internal void Draw(params VfxPlaybackRuntime.EmitterState[] emitters)
        {
            var queue = new VfxRenderQueueEntry[emitters.Length];
            for (int index = 0; index < emitters.Length; index++) queue[index] = new(emitters[index], 0, index);
            Renderer.Render(queue, ViewProjection, View);
        }
        internal byte[] Pixel(int x)
        {
            byte[] pixel = new byte[4];
            Gl.ReadPixels(x, 32, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, pixel.AsSpan());
            return pixel;
        }
        internal byte Stencil(int x)
        {
            Span<byte> pixel = stackalloc byte[4];
            Gl.ReadPixels(x, 32, 1, 1, PixelFormat.StencilIndex, PixelType.UnsignedByte, pixel);
            return pixel[0];
        }
        internal uint Texture(uint size, byte[] pixels)
        {
            uint texture = Gl.GenTexture();
            Gl.BindTexture(TextureTarget.Texture2D, texture);
            Gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, size, size, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, pixels.AsSpan());
            Gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            Gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            return texture;
        }
        public void Dispose()
        {
            Assert.Equal(GLEnum.NoError, Gl.GetError());
            Renderer.Dispose();
            Gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            Gl.DeleteFramebuffer(_frame); Gl.DeleteRenderbuffer(_depth);
            Gl.DeleteTexture(_target); Gl.DeleteTexture(_white);
            Gl.Dispose(); _context.Dispose();
        }
    }
}
