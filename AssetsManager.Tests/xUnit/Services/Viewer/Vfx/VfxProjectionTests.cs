using System;
using System.IO;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Parsing;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx;

public sealed class VfxProjectionTests
{
    [Theory]
    [InlineData(false, 5f, 200f)]
    [InlineData(true, 20f, 80f)]
    public void ReadsProjectionParametersAndMakesTheEmitterDrawable(bool authored, float range, float fading)
    {
        var emitter = ReadEmitter(authored);
        Assert.Equal(new VfxProjectionDefinition(range, fading), emitter.Projection);
        Assert.True(emitter.IsVisual);
        Assert.True(emitter.DrawsAsProjection);
        Assert.False(emitter.DrawsAsQuad);
        Assert.False((emitter with { Distortion = new VfxDistortionDefinition(0.03f, 1, "normal.tex") }).DrawsAsDistortion);
        Assert.Null(ReadEmitter(false, "VfxPrimitiveCameraQuad").Projection);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(30f)]
    [InlineData(-45f)]
    [InlineData(90f)]
    public void ComplexFootprintUsesXAndZAndUndoesItsWorldYaw(float degrees)
    {
        float angle = degrees * MathF.PI / 180f;
        var source = Instance();
        source[36] = MathF.Cos(angle);
        source[38] = -MathF.Sin(angle);
        var snapshot = (float[])source.Clone();
        float[] decal = new VfxProjectionGeometry().Prepare(ReadEmitter(true), source).ToArray();
        Assert.Equal(40f, decal[3]);
        Assert.Equal(25f, decal[4]);
        Assert.Equal(-angle, decal[9], 4);
        Assert.Equal(0.25f, decal[5]);
        Assert.Equal(snapshot, source);
    }

    [Fact]
    public void SimpleFootprintUsesXAndYItsRotationStreamAndWhiteModulation()
    {
        var emitter = ReadEmitter(false) with { AuthoredFeatures = new VfxEmitterAuthoredFeatures(HasLegacySimple: true) };
        var source = Instance();
        source[9] = MathF.PI / 12f;
        float[] decal = new VfxProjectionGeometry().Prepare(emitter, source).ToArray();
        Assert.Equal(7f, decal[4]);
        Assert.Equal(MathF.PI / 12f, decal[9]);
        Assert.Equal(new[] { 1f, 1f, 1f, 1f }, decal[5..9]);
    }

    [Theory]
    [InlineData(0f, 1f)]
    [InlineData(20f, 1f)]
    [InlineData(60f, 0.5f)]
    [InlineData(-60f, 0.5f)]
    [InlineData(100f, 0f)]
    [InlineData(200f, 0f)]
    public void FootprintFadesOutsideTheAuthoredHeightBand(float height, float expected)
    {
        var source = Instance();
        source[1] = height;
        float[] decal = new VfxProjectionGeometry().Prepare(ReadEmitter(true), source).ToArray();
        Assert.Equal(expected, decal[21], 5);
    }

    [Fact]
    public void ColorLookupUsesLifetimeSpeedAndBirthRandomWithoutEmitterUvTransforms()
    {
        var geometry = new VfxProjectionGeometry();
        var emitter = ReadEmitter(true) with { ColorLookUpTypeX = 1, ColorLookUpTypeY = 2,
            ColorLookUpScales = new Vector2(2f, 3f), ColorLookUpOffsets = new Vector2(0.1f, 0.2f) };
        var source = Instance();
        source[11] = 0.25f;
        source[12] = 3f;
        source[13] = 4f;
        source[34] = 0.75f;
        float[] decal = geometry.Prepare(emitter, source).ToArray();
        Assert.Equal(0.6f, decal[19], 5);
        Assert.Equal(15.2f, decal[20], 5);
        decal = geometry.Prepare(emitter with { ColorLookUpTypeX = 3, ColorLookUpTypeY = 0 }, source).ToArray();
        Assert.Equal(1.6f, decal[19], 5);
        Assert.Equal(3f, decal[20], 5);
        Assert.Equal(4096, VfxOpenGlRenderer.ResolveEmitterDrawCount(emitter, 0, 5000));
        Assert.Equal(0, VfxOpenGlRenderer.ResolveEmitterDrawCount(emitter, 4096, 1));
    }

    [Fact]
    public void ProjectionKeepsAuthoredColorWhileBillboardsKeepTheirAdditivePreprocessing()
    {
        Vector4 color = new(0.8f, 0.4f, 0.2f, 0.5f);
        var definition = ReadEmitter(true) with { BlendMode = 0, BirthColor = VfxCurve4.Const(color),
            Rate = VfxCurveF.Const(1f), ParticleLifetime = VfxCurveF.Const(1f), IsSingleParticle = true };
        foreach (bool projection in new[] { true, false })
        {
            var emitter = projection ? definition : definition with { PrimitiveKind = VfxPrimitiveKind.CameraQuad, Projection = null };
            var runtime = new VfxPlaybackRuntime(7);
            runtime.SetSystem(new VfxSystemDefinition(1, "color", "color", new[] { emitter }), Vector3.Zero);
            runtime.Update(0.1f);
            var state = Assert.Single(runtime.Emitters);
            Assert.True(state.InstanceCount > 0);
            float[] packed = state.PrepareInstances(1).ToArray();
            Assert.Equal(projection ? new[] { 0.8f, 0.4f, 0.2f, 0.5f } : new[] { 0.4f, 0.2f, 0.1f, 1f }, packed[5..9]);
        }
    }

    [Fact]
    public void ColorRampOnlyDrawsWithoutErosionOrTextureMult()
    {
        VfxEmitterDefinition plain = ReadEmitter(true);
        Assert.True(VfxOpenGlRenderer.UsesProjectionColorRamp(plain, hasRamp: true));
        Assert.False(VfxOpenGlRenderer.UsesProjectionColorRamp(plain, hasRamp: false));
        Assert.False(VfxOpenGlRenderer.UsesProjectionColorRamp(
            plain with { AlphaErosion = new VfxAlphaErosionDefinition("erosion.tex", VfxCurveF.Const(1f), 0.1f, 0.1f, 0) }, hasRamp: true));
        Assert.False(VfxOpenGlRenderer.UsesProjectionColorRamp(
            plain with { TextureMultPath = "mult.tex" }, hasRamp: true));
    }

    [Fact]
    public void ProjectionShaderCompilesAndLinksOnDesktopOpenGl()
    {
        using var context = new AssetsManager.Tests.Support.HiddenWglContext();
        using var gl = Silk.NET.OpenGL.GL.GetApi(context.GetProcAddress);
        uint program = AssetsManager.Utils.Rendering.GlShaderCompiler.CreateProgram(gl, false,
            VfxProjectionShaderSource.Vertex, VfxProjectionShaderSource.Fragment);
        try
        {
            Assert.NotEqual(0u, program);
            Assert.True(gl.GetUniformLocation(program, "uViewProj") >= 0);
            Assert.True(gl.GetUniformLocation(program, "uColorMap") >= 0);
        }
        finally { gl.DeleteProgram(program); }
    }

    private static float[] Instance()
    {
        var source = new float[VfxPlaybackRuntime.InstanceStride];
        source[3] = 40f; source[4] = 7f; source[18] = 25f;
        source[5] = 0.25f; source[6] = 0.5f; source[7] = 1f; source[8] = 0.75f;
        source[36] = 1f; source[40] = 1f; source[44] = 1f;
        return source;
    }

    private static VfxEmitterDefinition ReadEmitter(bool authored, string primitiveClass = "VfxPrimitivePlanarProjection")
    {
        var properties = authored ? new BinTreeProperty[] {
            new BinTreeStruct(Fnv1a.HashLower("mProjection"), Fnv1a.HashLower("VfxProjectionDefinitionData"),
                new BinTreeProperty[] { new BinTreeF32(Fnv1a.HashLower("mYRange"), 20f),
                    new BinTreeF32(Fnv1a.HashLower("mFading"), 80f) }) } : Array.Empty<BinTreeProperty>();
        var emitter = new BinTreeStruct(0, Fnv1a.HashLower("VfxEmitterDefinitionData"), new BinTreeProperty[] {
            new BinTreeStruct(Fnv1a.HashLower("primitive"), Fnv1a.HashLower(primitiveClass), properties) });
        var obj = new BinTreeObject("Effects/Projection", "VfxSystemDefinitionData", new BinTreeProperty[] {
            new BinTreeContainer(Fnv1a.HashLower("complexEmitterDefinitionData"), BinPropertyType.Struct,
                new BinTreeProperty[] { emitter }) });
        using var stream = new MemoryStream();
        new BinTree(new[] { obj }, Array.Empty<string>()).Write(stream);
        return Assert.Single(Assert.Single(VfxGraphParser.ParseDocument(stream.ToArray()).Systems).Value.Emitters);
    }
}
