using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AssetsManager.Services.Viewer.Rendering.GameShaders;
using AssetsManager.Tests.xUnit.Infrastructure;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Map;

public sealed class GameShaderProgramPairTests
{
    [Fact]
    public void FallbackFindsAListCompiledInBothStages()
    {
        var read = Read(new[] { "A=1B=1", "A=0B=0" }, new[] { "A=0B=1", "A=0B=0" },
            new[] { "A=0", "A=1", "B=0", "B=1" }, "A=1", "B=1");

        Assert.True(read.Ready, read.Failure);
        Assert.Equal("0", read.Program.Defines.Single(d => d.Name == "A").Value);
        Assert.Equal("0", read.Program.Defines.Single(d => d.Name == "B").Value);
        Assert.Equal((byte)1, read.Program.Vertex[20]);
        Assert.Equal((byte)1, read.Program.Pixel[20]);
    }

    [Fact]
    public void StagesWithoutACommonListCannotProduceAReadyProgram()
    {
        var read = Read(new[] { "A=1B=1" }, new[] { "A=0B=1" },
            new[] { "A=0", "A=1", "B=0", "B=1" }, "A=1", "B=1");

        Assert.False(read.Ready);
        Assert.NotEmpty(read.Failure);
    }

    [Theory]
    [InlineData("DISABLE_FOW", "1")]
    [InlineData("DISABLE_SHADOWS", "1")]
    [InlineData("NUM_BLEND_WEIGHTS", "4")]
    [InlineData("LOW_QUALITY_MODE", "1")]
    [InlineData("FORCE_MANTIS_LIGHTING", "1")]
    [InlineData("USE_DYNAMIC_LIGHTING", "1")]
    [InlineData("GENERATE_SHADOW_MAP", "1")]
    [InlineData("IS_AROUND_GAME", "1")]
    public void FallbackPreservesEngineDefines(string name, string value)
    {
        var read = Read(new[] { name + "=0" }, new[] { name + "=0" },
            new[] { name + "=0", name + "=" + value }, name + "=" + value);

        Assert.False(read.Ready);
    }

    [Fact]
    public void ExactPairKeepsStageSpecificDefines()
    {
        var read = Read(new[] { "A=1" }, new[] { "B=1" },
            new[] { "A=1", "B=1" },
            new[] { "A=0", "A=1" }, new[] { "B=0", "B=1" });

        Assert.True(read.Ready, read.Failure);
        Assert.Null(read.Program.Fallback);
        Assert.Equal("1", read.Program.Defines.Single(d => d.Name == "A").Value);
        Assert.Equal("1", read.Program.Defines.Single(d => d.Name == "B").Value);
    }

    [Theory]
    [InlineData("A=0", false)]
    [InlineData("A=1", true)]
    public void NativePairsRequireTheirAuthoredFeatureSet(string key, bool ready)
    {
        string[] declarations = { "A=0", "A=1" };
        var read = Read(new[] { key }, new[] { key }, new[] { "A=1" },
            declarations, declarations, native: true);

        Assert.Equal(ready, read.Ready);
        if (ready) Assert.Null(read.Program.Fallback);
    }

    private static GameShaderProgramResolver.ShaderBytecodeRead Read(
        string[] vertexKeys, string[] pixelKeys, string[] declarations, params string[] requested) =>
        Read(vertexKeys, pixelKeys, requested, declarations, declarations);

    private static GameShaderProgramResolver.ShaderBytecodeRead Read(
        string[] vertexKeys, string[] pixelKeys, string[] requested,
        string[] vertexDeclarations, string[] pixelDeclarations, bool native = false)
    {
        using var bridge = new AssetsManagerTestBridge();
        string path = Path.Combine(bridge.RootPath, "ShaderCache.dx11.wad.client");
        const string shader = "Shaders/Audit/Pair";
        var chunks = new List<WadBakeEntry>();
        void Stage(string stage, string[] keys, string[] stageDeclarations)
        {
            string source = native ? stage == "vs" ? GameParticleProgramResolver.QuadVertexPath :
                GameParticleProgramResolver.QuadPixelPath : shader;
            string toc = GameShaderProgramResolver.TocPath(source, stage);
            byte[] table = Toc(keys, stageDeclarations);
            byte[] bundle = Bundle(keys.Length);
            chunks.Add(new(toc, () => new MemoryStream(table), WadChunkCompression.None));
            chunks.Add(new(toc + "_0", () => new MemoryStream(bundle), WadChunkCompression.None));
        }
        Stage("vs", vertexKeys, vertexDeclarations);
        Stage("ps", pixelKeys, pixelDeclarations);
        WadBuilder.Bake(chunks, path, new WadBakeSettings());
        using var wad = new WadFile(path);
        var defines = requested.Select(p => p.Split('=')).Select(p =>
            new GameMaterialDefine(p[0], p[1], GameMaterialDefineSource.Pass)).ToArray();
        var pass = new GameMaterialPass(0, shader, defines,
            Array.Empty<KeyValuePair<string, bool>>(), Array.Empty<GameMaterialTexture>(),
            Array.Empty<GameMaterialParameter>(), GameMaterialPassState.Default);
        if (native) pass = pass with
        {
            VertexShaderPath = GameParticleProgramResolver.QuadVertexPath,
            PixelShaderPath = GameParticleProgramResolver.QuadPixelPath
        };
        return GameShaderProgramResolver.ReadFromWad(pass, GameMaterialKind.StaticMesh, wad, path, false);
    }

    private static byte[] Toc(string[] keys, string[] declarations)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        void Text(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }
        Text("TOC3.0");
        writer.Write(keys.Length);
        writer.Write(declarations.Length);
        writer.Write(keys.Length);
        writer.Write(0);
        Text("baseDefines");
        foreach (string declaration in declarations)
        {
            string[] pair = declaration.Split('=');
            Text(pair[0]);
            Text(pair[1]);
        }
        Text("shaders");
        foreach (string key in keys) writer.Write(XxHash64Ext.Hash(key));
        for (uint i = 0; i < keys.Length; i++) writer.Write(i);
        return stream.ToArray();
    }

    private static byte[] Bundle(int count)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        for (int i = 0; i < count; i++)
        {
            // Reflection-only containers identify the chosen record without relying on an installed client.
            byte[] dxbc = new byte[76];
            Encoding.ASCII.GetBytes("DXBC").CopyTo(dxbc, 0);
            dxbc[20] = (byte)i;
            BitConverter.GetBytes(dxbc.Length).CopyTo(dxbc, 24);
            BitConverter.GetBytes(1).CopyTo(dxbc, 28);
            BitConverter.GetBytes(36).CopyTo(dxbc, 32);
            Encoding.ASCII.GetBytes("RDEF").CopyTo(dxbc, 36);
            BitConverter.GetBytes(32).CopyTo(dxbc, 40);
            dxbc[61] = 5;
            writer.Write(dxbc.Length);
            writer.Write(dxbc);
        }
        return stream.ToArray();
    }
}
