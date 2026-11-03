using System;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Services.Viewer.Rendering.Core;
using AssetsManager.Services.Viewer.Rendering.GameShaders;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering;

public sealed class CharacterPreviewContractTests
{
    [Fact]
    public void PbrConstantBuffersNormalizeTheGridOnceAndKeepParticleSunGainIndependent()
    {
        float[] data = new float[144];
        var frame = new GameShaderRuntime.Frame(Matrix4x4.Identity,Matrix4x4.Identity,Vector3.Zero,0,null);
        GameShaderRuntime.WritePerFramePixel(data,in frame,true,true);
        Assert.Equal(1f,data[64]); Assert.Equal(10f,data[27]); Assert.Equal(3f,data[88]);
        var sun = new MapSunData(Vector3.UnitY,Vector4.One,1,Vector4.One,Vector4.One,1)
            { PbrSunAdditionalScale = 7, HdrEnvDiffuseScale = 2 };
        frame = frame with { Sun = sun, LightGrid = new MapLightGridData(1,1,1,1,1,0,new byte[24],2*MathF.PI) };
        GameShaderRuntime.WritePerFramePixel(data,in frame,true,true);
        Assert.Equal(2f,data[64]); Assert.Equal(7f,data[27]); Assert.Equal(2f,data[88]);
        GameShaderRuntime.WritePerFramePixel(data,in frame,true,false);
        Assert.Equal(1f,data[27]);
        Assert.Equal(2*MathF.PI,data[64]);
    }

    [Fact]
    public void PbrPermutationsFollowAvailableOverridesAndTangentStreamsAndRemainStable()
    {
        var material = ModelMaterialDefinition.TextureOnly("base") with
        {
            EmissiveTexturePath = "emissive",
            Pbr = new("rma", "normal") { RmaPaths = new[] { "override", "rma" } }
        };
        uint? Available(string path) => path == "override" ? null : 1;
        var normal = CharacterPbrProgram.Resolve(material, true, Available);
        var pass = Assert.Single(normal.Program.Passes);
        Assert.Equal(new[] { "FEATURE_ROUGHNESS_METALLIC", "FEATURE_TANGENT" }, pass.Defines.Select(d => d.Name));
        Assert.Equal(new[] { "DIFFUSE_MAP", "RMA_MAP", "NORMAL_MAP" }, pass.Textures.Select(t => t.Name));
        Assert.Equal("rma", pass.Textures[1].Texture.VirtualPath);
        Assert.Same(normal, CharacterPbrProgram.Resolve(material, true, Available));
        var noTangents = CharacterPbrProgram.Resolve(material, false, Available);
        Assert.Single(Assert.Single(noTangents.Program.Passes).Defines);
        var missing = CharacterPbrProgram.Resolve(material, true, _ => null);
        var basePass = Assert.Single(missing.Program.Passes);
        Assert.Empty(basePass.Defines);
        Assert.Equal(new[] { "DIFFUSE_MAP", "EMISSIVE_MAP" }, basePass.Textures.Select(t => t.Name));
        var overrideReady = CharacterPbrProgram.Resolve(material, true, _ => 2);
        Assert.Equal("override", Assert.Single(overrideReady.Program.Passes).Textures[1].Texture.VirtualPath);
        Assert.Same(normal, CharacterPbrProgram.Resolve(material, true, Available));
        var authored = material with { BindingKind = ModelMaterialBindingKind.Authored };
        Assert.Same(authored, CharacterPbrProgram.Resolve(authored, true, _ => throw new Exception("Must not probe authored textures")));
    }

    [Fact]
    public void SharedEdgesDrawOnceWithinARangeAndRemainOwnedByBothVisibleRanges()
    {
        uint[] triangles = { 0, 1, 2, 2, 1, 3 };
        Vector3[] points = { new(0,0,0), new(1,0,0), new(0,1,0), new(1,1,0) };
        var all = GlMeshEdges.Build(triangles, points, 0, 6);
        Assert.Equal(10, all.Indices.Length);
        Assert.Equal((4f + MathF.Sqrt(2)) / 5f, all.Spacing, 6);
        var a = GlMeshEdges.Build(triangles, points, 0, 3);
        var b = GlMeshEdges.Build(triangles, points, 3, 3);
        Assert.Equal(6, a.Indices.Length); Assert.Equal(6, b.Indices.Length);
        bool HasSeam(uint[] indices) => Enumerable.Range(0, indices.Length / 2).Any(i => indices[2*i] == 1 && indices[2*i+1] == 2);
        Assert.True(HasSeam(a.Indices)); Assert.True(HasSeam(b.Indices));
        Assert.Empty(GlMeshEdges.Build(new uint[] { 1,1,1 }, points, 0, 3).Indices);
        Assert.Throws<ArgumentOutOfRangeException>(() => GlMeshEdges.Build(triangles, points, 3, 6));
    }

    [Fact]
    public void DefaultPreviewSkyIsLinearRadianceAndAuthoredGradientKeepsCubeOrientation()
    {
        float[] defaults = GameShaderImageLight.SkyTexels(null);
        Assert.Equal(16*16*6*4, defaults.Length);
        for (int i = 0; i < defaults.Length; i++) Assert.Equal(i % 4 == 3 ? 1f : .15f, defaults[i], 6);
        var sun = new MapSunData(Vector3.UnitY, Vector4.One, 1, new(1,0,0,1), new(0,0,1,1), 1) { HorizonColor = Vector4.Zero };
        float[] faces = GameShaderImageLight.SkyTexels(sun, 1);
        Assert.Equal(.125f, faces[2*4]);
        Assert.Equal(.125f, faces[3*4+2]);
        Assert.Equal(0f, faces[0]);
    }
}
